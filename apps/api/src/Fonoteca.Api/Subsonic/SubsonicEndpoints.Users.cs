using Fonoteca.Api.Endpoints;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace Fonoteca.Api.Subsonic;

/// <summary>
/// Accounts, as OpenSubsonic's user management sees them.
/// </summary>
/// <remarks>
/// ADR 0018. Each method is the accounts page's own handler, called directly,
/// so the last admin, the password rules and the protected copy Subsonic
/// signs in with stay that page's. The roles are <see cref="AccountRoles"/>,
/// each <c>{name}Role</c> here. Another account is an admin's to read or
/// change; a person's own password needs the settings role — the protocol's
/// words both. A password comes clear or as <c>enc:</c> hex, like <c>p</c>.
/// One music folder, so <c>musicFolderId</c> is read by nothing; no LDAP, so
/// <c>ldapAuthenticated</c> is not either; and <c>scrobblingEnabled</c> is
/// false, since nothing here scrobbles.
/// </remarks>
public static partial class SubsonicEndpoints
{
    /// <summary>The signed-in account, or the one <c>username</c> names.</summary>
    private static async Task<IResult> GetUser(HttpContext http, UserManager<Account> users)
    {
        var name = Text(http, "username");

        if (name is not null && !Own(http, users, name) && !May(http, AccountRoles.Admin)) return NotAuthorized();

        var account = name is null
            ? await users.GetUserAsync(http.User).ConfigureAwait(false)
            : await users.FindByNameAsync(name).ConfigureAwait(false);

        return account is null
            ? NoSuchUser()
            : SubsonicResult.Ok("user", user => WriteUser(user, AccountEndpoints.Response(account)));
    }

    private static async Task<IResult> GetUsers(HttpContext http, UserManager<Account> users, CancellationToken cancellationToken)
    {
        if (!May(http, AccountRoles.Admin)) return NotAuthorized();

        var accounts = (await AccountEndpoints.GetAccounts(users, cancellationToken).ConfigureAwait(false)).Value!;

        return SubsonicResult.Ok("users", list => list.Children("user", accounts, WriteUser));
    }

    private static async Task<IResult> CreateUser(
        HttpContext http,
        UserManager<Account> users,
        IDataProtectionProvider protection,
        IClock clock)
    {
        if (!May(http, AccountRoles.Admin)) return NotAuthorized();

        if (Missing(http, "username", "password", "email") is { } missing) return missing;
        if (PasswordSent(http) is not { } password) return NotHex();

        var created = await AccountEndpoints.CreateAccount(
            new AccountRequest(
                Text(http, "username"),
                password,
                Text(http, "email"),
                AccountEndpoints.Each(RolesSent(http, Account.DefaultRoles))),
            users,
            protection,
            clock).ConfigureAwait(false);

        return Refusal(created.Result) ?? SubsonicResult.Ok();
    }

    /// <remarks>
    /// The password is checked before anything is saved, and set last, so a
    /// refused one changes nothing and a refused role change — the last admin's
    /// — leaves the password as it was.
    /// </remarks>
    private static async Task<IResult> UpdateUser(
        HttpContext http,
        UserManager<Account> users,
        IDataProtectionProvider protection,
        CancellationToken cancellationToken)
    {
        if (!May(http, AccountRoles.Admin)) return NotAuthorized();

        if (Missing(http, "username") is { } missing) return missing;
        if (await users.FindByNameAsync(Text(http, "username")!).ConfigureAwait(false) is not { } account) return NoSuchUser();

        string? password = null;

        if (Text(http, "password") is not null)
        {
            if (PasswordSent(http) is not { } sent) return NotHex();

            foreach (var validator in users.PasswordValidators)
            {
                var checkedOut = await validator.ValidateAsync(users, account, sent).ConfigureAwait(false);
                if (!checkedOut.Succeeded) return Refused(checkedOut);
            }

            password = sent;
        }

        var rolesSent = Enum.GetValues<AccountRoles>().Any(role => Text(http, RoleName(role)) is not null);

        var updated = await AccountEndpoints.UpdateAccount(
            account.Id,
            new AccountUpdateRequest(
                Text(http, "email"),
                rolesSent ? AccountEndpoints.Each(RolesSent(http, account.Roles)) : null,
                Text(http, "maxBitRate") is not null ? Number(http, "maxBitRate", account.MaxBitRate ?? 0, int.MaxValue) : null),
            users,
            cancellationToken).ConfigureAwait(false);

        if (Refusal(updated.Result) is { } refused) return refused;

        if (password is not null)
        {
            var set = await AccountEndpoints.SetPasswordAsync(users, protection, account, password).ConfigureAwait(false);
            if (!set.Succeeded) return Refused(set);
        }

        return SubsonicResult.Ok();
    }

    private static async Task<IResult> DeleteUser(HttpContext http, UserManager<Account> users, CancellationToken cancellationToken)
    {
        if (!May(http, AccountRoles.Admin)) return NotAuthorized();

        if (Missing(http, "username") is { } missing) return missing;
        if (await users.FindByNameAsync(Text(http, "username")!).ConfigureAwait(false) is not { } account) return NoSuchUser();

        var deleted = await AccountEndpoints.DeleteAccount(account.Id, users, cancellationToken).ConfigureAwait(false);
        return Refusal(deleted.Result) ?? SubsonicResult.Ok();
    }

    /// <remarks>
    /// <c>SetPasswordAsync</c> rather than the accounts page's reset handler,
    /// which signs a person changing their own password in again with a
    /// cookie — a cookie a Subsonic client has no use for.
    /// </remarks>
    private static async Task<IResult> ChangePassword(
        HttpContext http,
        UserManager<Account> users,
        IDataProtectionProvider protection)
    {
        if (Missing(http, "username", "password") is { } missing) return missing;

        var name = Text(http, "username")!;
        var allowed = Own(http, users, name)
            ? May(http, AccountRoles.Settings) || May(http, AccountRoles.Admin)
            : May(http, AccountRoles.Admin);

        if (!allowed) return NotAuthorized();
        if (await users.FindByNameAsync(name).ConfigureAwait(false) is not { } account) return NoSuchUser();
        if (PasswordSent(http) is not { } password) return NotHex();

        var set = await AccountEndpoints.SetPasswordAsync(users, protection, account, password).ConfigureAwait(false);
        return set.Succeeded ? SubsonicResult.Ok() : Refused(set);
    }

    private static void WriteUser(ISubsonicWriter user, AccountResponse account)
    {
        user.Attr("username", account.Name);
        user.Attr("email", account.Email);
        user.Attr("scrobblingEnabled", false);
        user.Attr("maxBitRate", account.MaxBitRate);

        foreach (var role in Enum.GetValues<AccountRoles>().Where(role => role != AccountRoles.None))
        {
            user.Attr(RoleName(role), account.Roles.Contains(role));
        }

        user.Numbers("folder", TheRoot);
    }

    /// <summary><c>CoverArt</c> as the protocol spells it: <c>coverArtRole</c>.</summary>
    private static string RoleName(AccountRoles role)
    {
        var name = role.ToString();
        return string.Concat(char.ToLowerInvariant(name[0]).ToString(), name.AsSpan(1), "Role");
    }

    /// <summary><paramref name="roles"/>, with each <c>{name}Role</c> the request sends set or cleared.</summary>
    private static AccountRoles RolesSent(HttpContext http, AccountRoles roles)
    {
        foreach (var role in Enum.GetValues<AccountRoles>().Where(role => role != AccountRoles.None))
        {
            if (!bool.TryParse(Text(http, RoleName(role)), out var given)) continue;

            roles = given ? roles | role : roles & ~role;
        }

        return roles;
    }

    private static string? PasswordSent(HttpContext http) => Decoded(Text(http, "password") ?? string.Empty);

    /// <summary>Whether <paramref name="name"/> is the signed-in account's, as Identity compares names.</summary>
    private static bool Own(HttpContext http, UserManager<Account> users, string name) =>
        users.NormalizeName(name) == users.NormalizeName(http.User.Identity!.Name);

    /// <summary>Whether the signed-in account holds <paramref name="role"/>.</summary>
    private static bool May(HttpContext http, AccountRoles role) => http.User.IsInRole(role.ToString());

    /// <summary>Error 10 naming the first of <paramref name="names"/> not sent; null when all were.</summary>
    private static SubsonicResult? Missing(HttpContext http, params string[] names) =>
        names.FirstOrDefault(name => Text(http, name) is null) is { } missing
            ? SubsonicResult.Error(10, $"Required parameter is missing: {missing}.")
            : null;

    private static SubsonicResult NotAuthorized() =>
        SubsonicResult.Error(50, "User is not authorized for the given operation.");

    private static SubsonicResult NoSuchUser() => SubsonicResult.Error(70, "No such user.");

    private static SubsonicResult NotHex() => SubsonicResult.Error(0, "The password after enc: is not hex.");

    private static SubsonicResult Refused(IdentityResult result) =>
        SubsonicResult.Error(0, string.Join(" ", result.Errors.Select(error => error.Description)));
}
