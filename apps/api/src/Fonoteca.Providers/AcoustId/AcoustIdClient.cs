using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Providers.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Fonoteca.Providers.AcoustId;

/// <summary>
/// <see cref="IAcoustIdLookup"/> over the AcoustID v2 web service.
/// </summary>
/// <remarks>
/// A POST rather than a GET, which is the unusual choice and the right one: a
/// full-length Chromaprint fingerprint is several kilobytes of base64, and in a
/// query string that is a URL long enough for an intermediate proxy to truncate
/// or reject. AcoustID documents POST as preferred for exactly this reason and
/// accepts a gzipped body, which this sends by default.
///
/// The rate limit is not enforced here. It lives in a
/// <see cref="RateLimitedHandler"/> underneath the resilience pipeline on the
/// named client, so retries are gated too — see <see cref="RequestGate"/>.
/// </remarks>
public sealed class AcoustIdClient(
    IHttpClientFactory clients,
    IOptions<AcoustIdOptions> options,
    ILogger<AcoustIdClient> logger) : IAcoustIdLookup
{
    /// <summary>Name used in <see cref="ProviderException.Provider"/> and in log messages.</summary>
    public const string ProviderName = "AcoustID";

    /// <summary>
    /// What to ask for alongside the match.
    /// </summary>
    /// <remarks>
    /// Identifiers and submission counts, not metadata. <c>meta=recordings</c>
    /// would also return titles and artists, mirrored from MusicBrainz at
    /// whatever point AcoustID last synchronised — a second, staler copy of
    /// facts <see cref="IMusicBrainzCatalogue"/> already owns. Asking only for
    /// what AcoustID is authoritative about also keeps the response a few
    /// hundred bytes instead of a few kilobytes, 100,000 times.
    ///
    /// Space-separated, which is what the documented <c>a+b</c> form in their
    /// URL examples decodes to.
    /// </remarks>
    private const string RequestedMeta = "recordingids sources";

    public async Task<IReadOnlyList<AcoustIdMatch>> LookupAsync(
        AudioFingerprint fingerprint,
        CancellationToken cancellationToken = default)
    {
        var config = options.Value;

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            // Refused here rather than sent and refused there. The remote error
            // is "invalid API key", which reads like a wrong key rather than a
            // missing one and names nothing the reader can go and edit.
            throw new ProviderRejectedException(
                ProviderName,
                "No AcoustID API key is configured. Set Fonoteca:AcoustIdApiKey — keys are free "
                + "for non-commercial use from https://acoustid.org/new-application.");
        }

        if (string.IsNullOrWhiteSpace(fingerprint.Value))
        {
            throw new ArgumentException("The fingerprint is empty.", nameof(fingerprint));
        }

        // Whole seconds is what the API takes and what fpcalc reports. Rounded
        // rather than truncated: a 240.7-second track sent as 240 is one second
        // further from the truth than it needs to be, and duration is half of
        // what separates a track from its extended mix.
        var seconds = (int)Math.Round(fingerprint.Duration.TotalSeconds, MidpointRounding.AwayFromZero);

        if (seconds <= 0)
        {
            throw new ArgumentException(
                "The fingerprint's duration must be at least one second.", nameof(fingerprint));
        }

        var body = await SendAsync(config, fingerprint.Value, seconds, cancellationToken)
            .ConfigureAwait(false);

        var matches = Parse(body);
        ProviderLog.AcoustIdMatched(logger, matches.Count, seconds);
        return matches;
    }

    private async Task<string> SendAsync(
        AcoustIdOptions config,
        string fingerprint,
        int seconds,
        CancellationToken cancellationToken)
    {
        using var content = await BuildContentAsync(config, fingerprint, seconds, cancellationToken)
            .ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, "lookup") { Content = content };

        var http = clients.CreateClient(AcoustIdOptions.HttpClientName);

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException cause)
        {
            throw Unavailable(cause.Message, cause);
        }
        catch (TaskCanceledException cause) when (!cancellationToken.IsCancellationRequested)
        {
            // Cancellation the caller did not ask for is a timeout wearing its
            // clothes. The distinction matters: one is a user pressing stop.
            throw Unavailable("the request timed out", cause);
        }
        catch (ExecutionRejectedException cause)
        {
            // The resilience pipeline gave up before the network did — an open
            // circuit after repeated failures, or the total-attempt timeout.
            throw Unavailable(cause.Message, cause);
        }

        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            // Read the body before trusting the status. AcoustID answers a bad
            // key with 400 and a JSON error document that says which of their
            // fourteen error codes it was, and that document is the only useful
            // part of the response.
            var failure = Diagnose(response.StatusCode, payload);
            if (failure is not null) throw failure;

            return payload;
        }
    }

    /// <summary>Builds the form body, gzipped when configured.</summary>
    private static async Task<HttpContent> BuildContentAsync(
        AcoustIdOptions config,
        string fingerprint,
        int seconds,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client"] = config.ApiKey,
            ["format"] = "json",
            ["duration"] = seconds.ToString(CultureInfo.InvariantCulture),
            ["meta"] = RequestedMeta,
            ["fingerprint"] = fingerprint,
        };

        var form = new FormUrlEncodedContent(fields);

        if (!config.CompressRequests) return form;

        byte[] encoded;

        using (form)
        {
            encoded = await form.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        var content = new ByteArrayContent(Gzip(encoded));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        // Not Content-Type. Their server inflates the body and then parses the
        // parameters, so as far as the form encoding is concerned nothing has
        // changed — which is exactly what Content-Encoding means and what a
        // gzip content type would wrongly claim.
        content.Headers.ContentEncoding.Add("gzip");

        return content;
    }

    private static byte[] Gzip(byte[] payload)
    {
        using var buffer = new MemoryStream(payload.Length);

        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(payload);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The exception this response deserves, or null if it is a good answer.
    /// </summary>
    /// <remarks>
    /// The whole point is the split between "ask again later" and "a human must
    /// change something", because the first is survivable inside a batch of
    /// 100,000 files and the second poisons every remaining one.
    /// </remarks>
    private static ProviderException? Diagnose(HttpStatusCode status, string payload)
    {
        var error = ReadError(payload);

        // 429 and 5xx are theirs to fix and are usually gone in a minute; a
        // 503 in particular is what a rate limit looks like once the gate has
        // already been outrun by something else on the same address.
        var transportIsTransient =
            status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
            || (int)status >= 500;

        if (error is not null)
        {
            var reason = error.Message ?? "no reason given";

            // 5 is their own "internal error". Everything else in their table is
            // something in the request: a bad key, a malformed fingerprint, a
            // duration they will not accept.
            return error.Code == 5 || transportIsTransient
                ? new ProviderUnavailableException(
                    ProviderName, $"AcoustID reported an error: {reason} (code {error.Code}).")
                : new ProviderRejectedException(
                    ProviderName, $"AcoustID refused the request: {reason} (code {error.Code}).");
        }

        if (transportIsTransient)
        {
            return new ProviderUnavailableException(
                ProviderName, $"AcoustID returned {(int)status} {status}.");
        }

        // A non-2xx with no error document at all is not AcoustID answering —
        // it is a proxy, a captive portal or a load balancer in the way.
        return (int)status is >= 200 and < 300
            ? null
            : new ProviderRejectedException(
                ProviderName,
                $"AcoustID returned {(int)status} {status} with no error document. "
                + "Something between this process and the service replaced the response.");
    }

    private static AcoustIdErrorBody? ReadError(string payload)
    {
        try
        {
            var body = JsonSerializer.Deserialize(payload, AcoustIdJsonContext.Default.AcoustIdResponse);
            return body?.Error;
        }
        catch (JsonException)
        {
            // Not JSON at all. Diagnose falls through to the status code, which
            // is all the information there is.
            return null;
        }
    }

    private static List<AcoustIdMatch> Parse(string payload)
    {
        AcoustIdResponse? body;

        try
        {
            body = JsonSerializer.Deserialize(payload, AcoustIdJsonContext.Default.AcoustIdResponse);
        }
        catch (JsonException cause)
        {
            throw Unavailable("the response was not valid JSON", cause);
        }

        if (body is null || !string.Equals(body.Status, "ok", StringComparison.Ordinal))
        {
            throw Unavailable($"the response reported status '{body?.Status ?? "none"}'", null);
        }

        var results = body.Results ?? [];
        var matches = new List<AcoustIdMatch>(results.Count);

        foreach (var result in results)
        {
            // No cluster id is nothing to key on, and never happens against the
            // real service. Skipping beats fabricating an identifier.
            if (result.Id is not { } acoustId) continue;

            var recordings = new List<AcoustIdRecordingRef>(result.Recordings?.Count ?? 0);

            foreach (var recording in result.Recordings ?? [])
            {
                if (recording.Id is { } mbid)
                {
                    recordings.Add(new AcoustIdRecordingRef(new Mbid(mbid), recording.Sources));
                }
            }

            // Clusters with no MusicBrainz link are kept. The audio is still
            // recognised — it is in the database, nobody has tagged it — and
            // that is a different fact from "unknown", worth telling apart when
            // deciding whether a file is worth submitting back.
            matches.Add(new AcoustIdMatch(acoustId, result.Score, recordings));
        }

        // Their results arrive best-first already; sorting makes that a promise
        // this class keeps rather than one it passes through.
        matches.Sort((left, right) => right.Score.CompareTo(left.Score));

        return matches;
    }

    private static ProviderUnavailableException Unavailable(string reason, Exception? cause) =>
        cause is null
            ? new ProviderUnavailableException(ProviderName, $"AcoustID lookup failed: {reason}.")
            : new ProviderUnavailableException(ProviderName, $"AcoustID lookup failed: {reason}.", cause);
}
