using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Polly;

namespace Fonoteca.Providers.ListenBrainz;

/// <summary>
/// <see cref="IListenBrainz"/> over ListenBrainz's API v1: <c>validate-token</c>,
/// <c>user/{name}/listens</c> and <c>submit-listens</c>.
/// </summary>
/// <remarks>
/// <b>A page is the API's largest, 1000, and is paged by <c>max_ts</c></b>,
/// which is exclusive: a second listen in the same second as the last of a
/// page is skipped, which costs nothing here, where one second holds one play.
///
/// <b>One listen is <c>single</c>, several <c>import</c>, and one with no time
/// <c>playing_now</c></b>, the three listen types the API names. A play sent
/// late keeps its own time and goes as a listen, never as playing now.
/// ListenBrainz keeps one listen per user, second and track, so a submission
/// retried by the pipeline is the same listen.
/// </remarks>
public sealed class ListenBrainzClient(IHttpClientFactory clients) : IListenBrainz
{
    /// <summary>Name used in <see cref="ProviderException.Provider"/> and in log messages.</summary>
    public const string ProviderName = "ListenBrainz";

    /// <summary>The most listens a page holds, which is also the most a submission may carry.</summary>
    public const int PageSize = 1000;

    /// <summary>What ListenBrainz is told sent the listens, beside the User-Agent.</summary>
    private const string SubmissionClient = "Fonoteca";

    public async Task<string?> UserOfAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        using var request = Request(HttpMethod.Get, "1/validate-token", token);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest) return null;

        var answer = await ReadAsync(response, ListenBrainzJsonContext.Default.ValidateTokenAnswer, cancellationToken).ConfigureAwait(false);

        return answer.Valid && !string.IsNullOrWhiteSpace(answer.UserName) ? answer.UserName : null;
    }

    public async Task<IReadOnlyList<HeardListen>> ListensAsync(string user, DateTimeOffset? before, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);

        var query = $"1/user/{Uri.EscapeDataString(user)}/listens?count={PageSize}"
            + (before is { } until ? $"&max_ts={until.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}" : string.Empty);

        using var request = Request(HttpMethod.Get, query, token: null);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        var answer = await ReadAsync(response, ListenBrainzJsonContext.Default.ListensAnswer, cancellationToken).ConfigureAwait(false);

        return
        [
            .. (answer.Payload?.Listens ?? [])
                .Where(listen => listen.TrackMetadata is { ArtistName.Length: > 0, TrackName.Length: > 0 })
                .Select(listen => new HeardListen(
                    DateTimeOffset.FromUnixTimeSeconds(listen.ListenedAt),
                    listen.TrackMetadata!.ArtistName!,
                    listen.TrackMetadata.TrackName!,
                    Blank(listen.TrackMetadata.ReleaseName),
                    MbidOf(listen.TrackMetadata.MbidMapping?.RecordingMbid) ?? MbidOf(listen.TrackMetadata.AdditionalInfo?.RecordingMbid))),
        ];
    }

    public async Task SubmitAsync(string token, IReadOnlyList<ListenToSubmit> listens, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(listens);

        if (listens.Count is 0 or > PageSize) throw new ArgumentOutOfRangeException(nameof(listens), listens.Count, "One to a thousand listens.");

        var type = listens[0].ListenedAt is null ? "playing_now" : listens.Count == 1 ? "single" : "import";

        var body = new Submission(type, [.. listens.Select(Payload)]);

        using var request = Request(HttpMethod.Post, "1/submit-listens", token);
        request.Content = JsonContent.Create(body, ListenBrainzJsonContext.Default.Submission);

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            var said = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new ProviderRejectedException(ProviderName, $"ListenBrainz refused the submission ({(int)response.StatusCode}): {said}");
        }

        if (!response.IsSuccessStatusCode) throw Unavailable($"the submission answered {(int)response.StatusCode}", cause: null);
    }

    private static SubmittedListen Payload(ListenToSubmit listen) =>
        new(
            listen.ListenedAt?.ToUnixTimeSeconds(),
            new SubmittedTrack(
                listen.Artist,
                listen.Track,
                listen.Release,
                new SubmittedInfo(
                    listen.Recording?.ToString(),
                    listen.TrackNumber,
                    listen.DurationSeconds is { } seconds ? seconds * 1000 : null,
                    SubmissionClient)));

    private static HttpRequestMessage Request(HttpMethod method, string path, string? token)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var http = clients.CreateClient(ListenBrainzOptions.HttpClientName);

        try
        {
            return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException cause)
        {
            throw Unavailable(cause.Message, cause);
        }
        catch (TaskCanceledException cause) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable("the request timed out", cause);
        }
        catch (ExecutionRejectedException cause)
        {
            throw Unavailable(cause.Message, cause);
        }
    }

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode) throw Unavailable($"the request answered {(int)response.StatusCode}", cause: null);

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return JsonSerializer.Deserialize(payload, type) ?? throw Unavailable("the answer was empty", cause: null);
        }
        catch (JsonException cause)
        {
            throw Unavailable("the response was not the JSON we expect", cause);
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static Mbid? MbidOf(string? value) => Guid.TryParse(value, out var id) ? new Mbid(id) : null;

    private static ProviderUnavailableException Unavailable(string reason, Exception? cause) =>
        cause is null
            ? new ProviderUnavailableException(ProviderName, $"ListenBrainz request failed: {reason}.")
            : new ProviderUnavailableException(ProviderName, $"ListenBrainz request failed: {reason}.", cause);
}

internal sealed record ValidateTokenAnswer(
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("user_name")] string? UserName);

internal sealed record ListensAnswer([property: JsonPropertyName("payload")] ListensPayload? Payload);

internal sealed record ListensPayload([property: JsonPropertyName("listens")] List<HeardListenJson>? Listens);

internal sealed record HeardListenJson(
    [property: JsonPropertyName("listened_at")] long ListenedAt,
    [property: JsonPropertyName("track_metadata")] HeardTrackJson? TrackMetadata);

internal sealed record HeardTrackJson(
    [property: JsonPropertyName("artist_name")] string? ArtistName,
    [property: JsonPropertyName("track_name")] string? TrackName,
    [property: JsonPropertyName("release_name")] string? ReleaseName,
    [property: JsonPropertyName("additional_info")] RecordingJson? AdditionalInfo,
    [property: JsonPropertyName("mbid_mapping")] RecordingJson? MbidMapping);

internal sealed record RecordingJson([property: JsonPropertyName("recording_mbid")] string? RecordingMbid);

internal sealed record Submission(
    [property: JsonPropertyName("listen_type")] string ListenType,
    [property: JsonPropertyName("payload")] List<SubmittedListen> Payload);

internal sealed record SubmittedListen(
    [property: JsonPropertyName("listened_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ListenedAt,
    [property: JsonPropertyName("track_metadata")] SubmittedTrack TrackMetadata);

internal sealed record SubmittedTrack(
    [property: JsonPropertyName("artist_name")] string ArtistName,
    [property: JsonPropertyName("track_name")] string TrackName,
    [property: JsonPropertyName("release_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReleaseName,
    [property: JsonPropertyName("additional_info")] SubmittedInfo AdditionalInfo);

internal sealed record SubmittedInfo(
    [property: JsonPropertyName("recording_mbid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RecordingMbid,
    [property: JsonPropertyName("tracknumber"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TrackNumber,
    [property: JsonPropertyName("duration_ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DurationMs,
    [property: JsonPropertyName("submission_client")] string SubmissionClient);

[JsonSerializable(typeof(ValidateTokenAnswer))]
[JsonSerializable(typeof(ListensAnswer))]
[JsonSerializable(typeof(Submission))]
internal sealed partial class ListenBrainzJsonContext : JsonSerializerContext;
