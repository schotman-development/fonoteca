using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Fonoteca.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// Accounts, as a test needs them.
/// </summary>
/// <remarks>
/// Every <c>/api</c> route takes a signed-in account (ADR 0018). A test of a
/// library endpoint is not a test of signing in, so <see cref="SignedIn"/>
/// signs every request of its host in as <see cref="AccountId"/>, which has
/// no row and holds every role. A test of signing in, or of Subsonic, makes real accounts with
/// <see cref="SetUpAsync"/> instead and leaves this off.
/// </remarks>
internal static class TestSignIn
{
    private const string SchemeName = "Test";

    /// <summary>The account every request of a <see cref="SignedIn"/> host acts as.</summary>
    public static readonly Guid AccountId = Guid.Parse("0199f0a0-0000-7000-8000-000000000001");

    public static IWebHostBuilder SignedIn(this IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, Handler>(SchemeName, null);
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultScheme = SchemeName;
                options.DefaultAuthenticateScheme = SchemeName;
                options.DefaultChallengeScheme = SchemeName;
            });
        });

    /// <summary>
    /// Makes the first account as the first visit does, and answers a client
    /// signed in as it, and its id.
    /// </summary>
    public static async Task<(HttpClient Client, Guid Id)> SetUpAsync(
        this WebApplicationFactory<Program> factory,
        string name,
        string password,
        CancellationToken cancellationToken)
    {
        var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/account/setup", UriKind.Relative),
            new { name, password },
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var account = await response.Content.ReadFromJsonAsync<AccountRow>(cancellationToken);
        return (client, account!.Id);
    }

    /// <summary>
    /// Another account, made by a client already signed in as an admin; with
    /// no roles named, OpenSubsonic's defaults.
    /// </summary>
    public static async Task<Guid> AddAccountAsync(
        this HttpClient signedIn,
        string name,
        string password,
        CancellationToken cancellationToken,
        params string[]? roles)
    {
        using var response = await signedIn.PostAsJsonAsync(
            new Uri("/api/accounts", UriKind.Relative),
            new { name, password, roles = roles is { Length: > 0 } ? roles : null },
            cancellationToken);

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<AccountRow>(cancellationToken))!.Id;
    }

    private sealed record AccountRow(Guid Id, string Name);

    private sealed class Handler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, AccountId.ToString()),
                        new Claim(ClaimTypes.Name, "tester"),
                        .. Enum.GetValues<AccountRoles>().Select(role => new Claim(ClaimTypes.Role, role.ToString())),
                    ],
                    SchemeName)),
                SchemeName)));
    }
}
