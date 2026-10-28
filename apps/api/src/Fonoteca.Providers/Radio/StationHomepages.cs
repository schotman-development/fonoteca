using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Providers.Radio;

/// <summary>
/// A radio station's own home page, read for the icons it names.
/// </summary>
/// <remarks>
/// <b>The one client here that asks an address nobody vetted</b> — a home page
/// is whatever a client saved, often copied from radio-browser, where anyone
/// can enter one — so it connects only to public addresses
/// (<see cref="ConnectAsync"/>): never this machine, the home network or the
/// mesh behind it, on the first request or on any redirect. It reads only a
/// page that says it is HTML, and only the first <see cref="MostBytes"/> of
/// it, since a "home page" can as well be the stream.
/// </remarks>
public sealed class StationHomepages(IHttpClientFactory clients)
{
    /// <summary>Name of the configured <c>HttpClient</c>.</summary>
    public const string HttpClientName = "station-homepages";

    /// <summary>Name used in <see cref="ProviderException.Provider"/> and in log messages.</summary>
    public const string ProviderName = "station home page";

    private const int MostBytes = 1 << 20;

    /// <summary>The icons the page names, and the address it was read from after redirects; none where it is not a page.</summary>
    /// <exception cref="ProviderUnavailableException">The page could not be read.</exception>
    public async Task<(PageIconList Icons, Uri? Page)> IconsAsync(string homepage, CancellationToken cancellationToken = default)
    {
        using var response = await GetAsync(homepage, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode
            || response.Content.Headers.ContentType?.MediaType is not ("text/html" or "application/xhtml+xml"))
        {
            return (PageIconList.None, null);
        }

        var page = response.RequestMessage?.RequestUri ?? new Uri(homepage);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[MostBytes];
            var read = 0;

            try
            {
                for (int got; read < buffer.Length && (got = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false)) > 0;)
                {
                    read += got;
                }

                return (PageIcons.From(Encoding.UTF8.GetString(buffer, 0, read), page), page);
            }
            catch (Exception cause) when (cause is IOException or HttpRequestException or RegexMatchTimeoutException)
            {
                throw Unavailable(homepage, cause.Message, cause);
            }
        }
    }

    /// <summary>Whether the address answers with a picture: how a <c>/favicon.ico</c> nothing named is checked before it is kept.</summary>
    /// <exception cref="ProviderUnavailableException">It could not be asked.</exception>
    public async Task<bool> IsImageAsync(string url, CancellationToken cancellationToken = default)
    {
        using var response = await GetAsync(url, cancellationToken).ConfigureAwait(false);

        return response.IsSuccessStatusCode
            && response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Whether an address is on the public internet: not loopback, not
    /// private, not link-local, not the shared space a carrier-grade NAT or a
    /// mesh such as NetBird uses, not multicast or reserved.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();

            return bytes[0] switch
            {
                0 or 10 or 127 or >= 224 => false,
                100 => bytes[1] is < 64 or >= 128,
                169 => bytes[1] != 254,
                172 => bytes[1] is < 16 or >= 32,
                192 => bytes[1] != 168,
                _ => true,
            };
        }

        return !(address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6UniqueLocal
            || address.IsIPv6Multicast);
    }

    /// <summary>
    /// The connection, made only where every address the name resolves to is
    /// public — every one, so a name answering with a public and a private
    /// address cannot pick the private one later.
    /// </summary>
    internal static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        if (addresses.Length == 0 || !addresses.All(IsPublic))
        {
            throw new HttpRequestException($"{host} is not on the public internet, so it is not asked.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellationToken)
    {
        var http = clients.CreateClient(HttpClientName);

        try
        {
            return await http.GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException cause)
        {
            throw Unavailable(url, cause.Message, cause);
        }
        catch (TaskCanceledException cause) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable(url, "the request timed out", cause);
        }
    }

    private static ProviderUnavailableException Unavailable(string url, string reason, Exception cause) =>
        new(ProviderName, $"{url} could not be read: {reason}.", cause);
}
