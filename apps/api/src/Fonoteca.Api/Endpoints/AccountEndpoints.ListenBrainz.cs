using System.Security.Claims;
using System.Security.Cryptography;
using Fonoteca.Api.Listening;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Endpoints;

/// <summary>
/// The signed-in account's ListenBrainz connection (client issue #67): its
/// user token, which its plays are sent with and its listens counted from.
/// </summary>
/// <remarks>
/// A token is asked about before it is kept, so a mistyped one is refused
/// here rather than every play later. The first connection of a ListenBrainz
/// user counts the listens it holds, once (<see cref="ListenBrainzService"/>);
/// disconnecting keeps the plays, and connecting the same user again counts
/// nothing twice.
/// </remarks>
public static partial class AccountEndpoints
{
    private const string ListenBrainzPurpose = "Fonoteca.Accounts.ListenBrainzToken";

    private static void MapListenBrainz(RouteGroupBuilder mine)
    {
        mine.MapGet("/listenbrainz", GetListenBrainz)
            .WithName("GetListenBrainz")
            .WithSummary("The signed-in account's ListenBrainz connection and its plays.");

        mine.MapPost("/listenbrainz", ConnectListenBrainz)
            .RequireRole(AccountRoles.Settings, AccountRoles.Admin)
            .WithName("ConnectListenBrainz")
            .WithSummary("Send this account's plays to ListenBrainz with this user token, and count the listens it holds.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        mine.MapDelete("/listenbrainz", DisconnectListenBrainz)
            .RequireRole(AccountRoles.Settings, AccountRoles.Admin)
            .WithName("DisconnectListenBrainz")
            .WithSummary("Stop sending this account's plays to ListenBrainz; the plays stay.");
    }

    internal static async Task<Ok<ListenBrainzResponse>> GetListenBrainz(
        ClaimsPrincipal user,
        UserManager<Account> users,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var account = (await users.GetUserAsync(user).ConfigureAwait(false))!;

        return TypedResults.Ok(await ListenBrainzOf(db, account, cancellationToken).ConfigureAwait(false));
    }

    internal static async Task<Results<Ok<ListenBrainzResponse>, ProblemHttpResult>> ConnectListenBrainz(
        ListenBrainzRequest request,
        ClaimsPrincipal user,
        UserManager<Account> users,
        FonotecaDbContext db,
        IDataProtectionProvider protection,
        IListenBrainz listenBrainz,
        ListenBrainzService plays,
        CancellationToken cancellationToken)
    {
        var token = request?.Token?.Trim();

        if (string.IsNullOrEmpty(token) || token.Length > 200)
        {
            return TypedResults.Problem(
                detail: "Paste the user token from your ListenBrainz settings page.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        string? name;

        try
        {
            name = await listenBrainz.UserOfAsync(token, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderUnavailableException cause)
        {
            return TypedResults.Problem(detail: cause.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (name is null)
        {
            return TypedResults.Problem(
                detail: "ListenBrainz does not know that token.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var account = (await users.GetUserAsync(user).ConfigureAwait(false))!;

        // Another ListenBrainz user's listens have not been counted yet.
        if (account.ListenBrainzUser != name) account.ListenBrainzSeededUtc = null;

        account.ListenBrainzToken = ListenBrainzProtector(protection).Protect(token);
        account.ListenBrainzUser = name;

        await users.UpdateAsync(account).ConfigureAwait(false);

        plays.Wake(account.Id);

        return TypedResults.Ok(await ListenBrainzOf(db, account, cancellationToken).ConfigureAwait(false));
    }

    internal static async Task<NoContent> DisconnectListenBrainz(
        ClaimsPrincipal user,
        UserManager<Account> users)
    {
        var account = (await users.GetUserAsync(user).ConfigureAwait(false))!;

        account.ListenBrainzToken = null;
        await users.UpdateAsync(account).ConfigureAwait(false);

        return TypedResults.NoContent();
    }

    /// <summary>The token the account's plays are sent with, or null where it has none or the key ring that protected it is gone.</summary>
    internal static string? ListenBrainzToken(IDataProtectionProvider protection, Account account)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (account.ListenBrainzToken is null) return null;

        try
        {
            return ListenBrainzProtector(protection).Unprotect(account.ListenBrainzToken);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static IDataProtector ListenBrainzProtector(IDataProtectionProvider protection) =>
        protection.CreateProtector(ListenBrainzPurpose);

    private static async Task<ListenBrainzResponse> ListenBrainzOf(FonotecaDbContext db, Account account, CancellationToken cancellationToken)
    {
        var plays = db.Plays.IgnoreQueryFilters().Where(play => play.AccountId == account.Id);

        return new ListenBrainzResponse(
            account.ListenBrainzToken is null ? null : account.ListenBrainzUser,
            account.ListenBrainzSeededUtc,
            await plays.CountAsync(cancellationToken).ConfigureAwait(false),
            await plays.CountAsync(play => play.ForwardedUtc == null, cancellationToken).ConfigureAwait(false));
    }
}

public sealed record ListenBrainzRequest(string? Token);

/// <param name="User">The ListenBrainz user plays are sent to; null when none is connected.</param>
/// <param name="SeededUtc">When the listens ListenBrainz held were counted; null until they have been.</param>
/// <param name="Plays">Every play counted for this account.</param>
/// <param name="Unsent">Plays ListenBrainz has not taken yet.</param>
public sealed record ListenBrainzResponse(string? User, DateTimeOffset? SeededUtc, int Plays, int Unsent);
