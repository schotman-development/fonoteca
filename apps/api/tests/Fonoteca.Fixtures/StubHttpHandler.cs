using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text;

namespace Fonoteca.Fixtures;

/// <summary>
/// The fake socket: answers from a delegate and records everything that was sent.
/// </summary>
/// <remarks>
/// Installed as the <i>primary</i> handler, so every real handler in the
/// pipeline — the resilience pipeline, the rate gate, the decompression
/// settings — is still in the chain above it. That is what makes these tests
/// worth more than a mocked client would be: the thing under test is the wiring
/// as much as the code.
///
/// In Fonoteca.Fixtures rather than beside the provider tests because two suites
/// now need it: the adapters, and the download path in Fonoteca.Api. A second
/// copy would be a second set of rules about what counts as a recorded request.
/// </remarks>
public sealed class StubHttpHandler(
    Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    /// <summary>The common case: answer immediately, from the request alone.</summary>
    public StubHttpHandler(Func<RecordedRequest, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request)))
    {
    }

    /// <summary>Every request that reached the wire, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        var recorded = new RecordedRequest
        {
            Method = request.Method,
            Uri = request.RequestUri!,
            UserAgent = request.Headers.UserAgent.Count > 0
                ? request.Headers.UserAgent.ToString()
                : null,
            ContentEncoding = request.Content is null
                ? []
                : [.. request.Content.Headers.ContentEncoding],
            AllHeaders = request.Headers.ToDictionary(
                static header => header.Key,
                static header => (IReadOnlyList<string>)[.. header.Value],
                StringComparer.OrdinalIgnoreCase),
            Body = body,
        };

        _requests.Enqueue(recorded);

        // The token is passed on so a stub can model a server that never
        // answers — the caller's timeout is then what ends the wait, which is
        // the only way to test that there is one.
        return await respond(recorded, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Always answers with this status and body.</summary>
    public static StubHttpHandler Returning(HttpStatusCode status, string body) =>
        new(_ => Json(status, body));

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>One request as it left the pipeline.</summary>
public sealed record RecordedRequest
{
    public required HttpMethod Method { get; init; }
    public required Uri Uri { get; init; }
    public required string? UserAgent { get; init; }
    public required IReadOnlyList<string> ContentEncoding { get; init; }

    /// <summary>Every request header that reached the wire, by name.</summary>
    /// <remarks>
    /// Case-insensitive, because HTTP header names are — and because the whole
    /// point of asserting on one is to prove a client put it there, which a
    /// lookup that missed on casing would silently deny.
    /// </remarks>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> AllHeaders { get; init; }
    public required byte[] Body { get; init; }

    /// <summary>The values sent under one header name; empty when it was not sent.</summary>
    public IReadOnlyList<string> Headers(string name) =>
        AllHeaders.TryGetValue(name, out var values) ? values : [];

    public bool IsGzipped =>
        ContentEncoding.Contains("gzip", StringComparer.OrdinalIgnoreCase);

    /// <summary>The form body, ungzipped first when it was sent compressed.</summary>
    public IReadOnlyDictionary<string, string> Form()
    {
        var bytes = Body;

        if (IsGzipped)
        {
            using var compressed = new MemoryStream(Body);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var plain = new MemoryStream();
            gzip.CopyTo(plain);
            bytes = plain.ToArray();
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in Encoding.UTF8.GetString(bytes).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=', StringComparison.Ordinal);
            if (split < 0) continue;

            // '+' is a space in form encoding, which UnescapeDataString does not
            // know — and `meta` is two space-separated words, so it matters.
            var key = Uri.UnescapeDataString(pair[..split].Replace('+', ' '));
            var value = Uri.UnescapeDataString(pair[(split + 1)..].Replace('+', ' '));

            fields[key] = value;
        }

        return fields;
    }
}
