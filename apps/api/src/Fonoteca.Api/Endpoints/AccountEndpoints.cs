using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Endpoints;

/// <summary>
/// Accounts: signing in, the accounts page, and Subsonic API keys.
/// </summary>
/// <remarks>
/// ADR 0018 is the design record. <b>Every account is equal</b> (the owner's
/// choice, "roles may come later"): any signed-in account adds, resets and
/// removes accounts, its own included, but never the last. <b>The first
/// account is made by the first visit</b>, while none exists, and claims the hearts given before
/// there were accounts; after that <c>/api/account/setup</c> refuses.
///
/// <b>One password for both clients</b>, the owner's choice: Identity keeps its
/// hash for this sign-in, and <see cref="SetPasswordAsync"/> keeps a protected
/// copy beside it for Subsonic's token check in the same save
/// (<see cref="Account.SubsonicPassword"/>).
/// </remarks>
public static class AccountEndpoints
{
    /// <summary>Data Protection's purpose for the Subsonic copy of a password.</summary>
    private const string PasswordPurpose = "Fonoteca.Accounts.SubsonicPassword";

    /// <summary>
    /// One setup or removal at a time, so two first visits cannot both make
    /// the first account and two accounts removing each other cannot leave none.
    /// </summary>
    /// <remarks>ponytail: one process; a second instance would need a database lock.</remarks>
    private static readonly SemaphoreSlim Census = new(1, 1);

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var mine = app.MapGroup("/api/account").WithTags("Accounts");

        mine.MapGet("/setup", GetSetup)
            .AllowAnonymous()
            .WithName("GetAccountSetup")
            .WithSummary("Whether the first account is still to be made.");

        mine.MapPost("/setup", PostSetup)
            .AllowAnonymous()
            .WithName("SetUpFirstAccount")
            .WithSummary("Make the first account and sign in as it; refused once any account exists.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        mine.MapPost("/login", Login)
            .AllowAnonymous()
            .WithName("SignIn")
            .WithSummary("Sign in with a name and password; the answer sets the session cookie.")
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        mine.MapPost("/logout", Logout)
            .WithName("SignOut")
            .WithSummary("Sign out of this browser.");

        mine.MapGet("/", GetMe)
            .WithName("GetSignedInAccount")
            .WithSummary("The signed-in account; 401 when nobody is.");

        mine.MapGet("/keys", GetKeys)
            .WithName("GetApiKeys")
            .WithSummary("The signed-in account's Subsonic API keys, without the keys themselves.");

        mine.MapPost("/keys", CreateKey)
            .WithName("CreateApiKey")
            .WithSummary("A new Subsonic API key. The key is in this answer and never again.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        mine.MapDelete("/keys/{id:guid}", RevokeKey)
            .WithName("RevokeApiKey")
            .WithSummary("Revoke one of the signed-in account's API keys.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        var everyone = app.MapGroup("/api/accounts").WithTags("Accounts");

        everyone.MapGet("/", GetAccounts)
            .WithName("GetAccounts")
            .WithSummary("Every account.");

        everyone.MapPost("/", CreateAccount)
            .WithName("CreateAccount")
            .WithSummary("Add an account.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        everyone.MapPost("/{id:guid}/password", ResetPassword)
            .WithName("ResetAccountPassword")
            .WithSummary("Set an account's password, for the web and Subsonic alike; signs it out elsewhere.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        everyone.MapDelete("/{id:guid}", DeleteAccount)
            .WithName("DeleteAccount")
            .WithSummary("Remove an account, with its hearts and API keys; never the last one.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    internal static async Task<Ok<AccountSetupResponse>> GetSetup(
        UserManager<Account> users,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(new AccountSetupResponse(
            !await users.Users.AnyAsync(cancellationToken).ConfigureAwait(false)));

    internal static async Task<Results<Ok<AccountResponse>, ProblemHttpResult>> PostSetup(
        AccountRequest request,
        UserManager<Account> users,
        SignInManager<Account> signIn,
        IDataProtectionProvider protection,
        FonotecaDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        await Census.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (await users.Users.AnyAsync(cancellationToken).ConfigureAwait(false))
            {
                return TypedResults.Problem(
                    detail: "An account exists already; sign in with it.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var (account, problem) = await CreateAsync(request, users, protection, clock).ConfigureAwait(false);
            if (problem is not null) return problem;

            // Hearts given before there were accounts are the first account's.
            await db.Loves
                .IgnoreQueryFilters()
                .Where(love => love.AccountId == null)
                .ExecuteUpdateAsync(love => love.SetProperty(row => row.AccountId, account!.Id), cancellationToken)
                .ConfigureAwait(false);

            await signIn.SignInAsync(account!, isPersistent: true).ConfigureAwait(false);

            return TypedResults.Ok(Response(account!));
        }
        finally
        {
            Census.Release();
        }
    }

    /// <remarks>
    /// Identity's own sign-in, lockout included: five wrong passwords lock the
    /// account for five minutes, here and on Subsonic alike. Wrong name and
    /// wrong password are one answer, so the form does not say which names
    /// exist.
    /// </remarks>
    internal static async Task<Results<Ok<AccountResponse>, ProblemHttpResult>> Login(
        SignInRequest request,
        UserManager<Account> users,
        SignInManager<Account> signIn)
    {
        var account = string.IsNullOrWhiteSpace(request?.Name) ? null : await users.FindByNameAsync(request.Name.Trim()).ConfigureAwait(false);

        var result = account is null
            ? SignInResult.Failed
            : await signIn.PasswordSignInAsync(account, request!.Password ?? string.Empty, isPersistent: true, lockoutOnFailure: true).ConfigureAwait(false);

        if (result.Succeeded) return TypedResults.Ok(Response(account!));

        return TypedResults.Problem(
            detail: result.IsLockedOut
                ? "Too many wrong passwords; this account is locked for a few minutes."
                : "Wrong name or password.",
            statusCode: StatusCodes.Status401Unauthorized);
    }

    internal static async Task<NoContent> Logout(SignInManager<Account> signIn)
    {
        await signIn.SignOutAsync().ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    internal static async Task<Results<Ok<AccountResponse>, UnauthorizedHttpResult>> GetMe(
        ClaimsPrincipal user,
        UserManager<Account> users)
    {
        var account = await users.GetUserAsync(user).ConfigureAwait(false);
        return account is null ? TypedResults.Unauthorized() : TypedResults.Ok(Response(account));
    }

    internal static async Task<Ok<IReadOnlyList<ApiKeyResponse>>> GetKeys(
        ClaimsPrincipal user,
        UserManager<Account> users,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var id = Guid.Parse(users.GetUserId(user)!);

        IReadOnlyList<ApiKeyResponse> keys = await db.ApiKeys
            .Where(key => key.AccountId == id)
            .OrderBy(key => key.CreatedUtc)
            .Select(key => new ApiKeyResponse(key.Id, key.Name, key.CreatedUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(keys);
    }

    /// <remarks>
    /// 32 random bytes, which OpenSubsonic requires be the server's and never
    /// a person's; only their SHA-256 is kept (<see cref="ApiKey"/>).
    /// </remarks>
    internal static async Task<Results<Ok<CreatedApiKeyResponse>, ProblemHttpResult>> CreateKey(
        ApiKeyRequest request,
        ClaimsPrincipal user,
        UserManager<Account> users,
        FonotecaDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var name = request?.Name?.Trim();

        if (string.IsNullOrEmpty(name) || name.Length > ApiKey.NameLength)
        {
            return TypedResults.Problem(
                detail: $"Name the key, in at most {ApiKey.NameLength} characters, so you know which to revoke.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        var key = new ApiKey
        {
            Id = Guid.CreateVersion7(),
            AccountId = Guid.Parse(users.GetUserId(user)!),
            Name = name,
            Hash = HashOf(secret),
            CreatedUtc = StoreTime.ToStorePrecision(clock.UtcNow),
        };

        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new CreatedApiKeyResponse(key.Id, key.Name, key.CreatedUtc, secret));
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> RevokeKey(
        Guid id,
        ClaimsPrincipal user,
        UserManager<Account> users,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var account = Guid.Parse(users.GetUserId(user)!);

        var revoked = await db.ApiKeys
            .Where(key => key.Id == id && key.AccountId == account)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        return revoked == 0
            ? TypedResults.Problem(detail: "No such key.", statusCode: StatusCodes.Status404NotFound)
            : TypedResults.NoContent();
    }

    internal static async Task<Ok<IReadOnlyList<AccountResponse>>> GetAccounts(
        UserManager<Account> users,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AccountResponse> accounts = (await users.Users
                .OrderBy(account => account.CreatedUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(Response)
            .ToList();

        return TypedResults.Ok(accounts);
    }

    internal static async Task<Results<Ok<AccountResponse>, ProblemHttpResult>> CreateAccount(
        AccountRequest request,
        UserManager<Account> users,
        IDataProtectionProvider protection,
        IClock clock)
    {
        var (account, problem) = await CreateAsync(request, users, protection, clock).ConfigureAwait(false);
        if (problem is not null) return problem;

        return TypedResults.Ok(Response(account!));
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> ResetPassword(
        Guid id,
        PasswordRequest request,
        ClaimsPrincipal user,
        UserManager<Account> users,
        SignInManager<Account> signIn,
        IDataProtectionProvider protection)
    {
        var account = await users.FindByIdAsync(id.ToString()).ConfigureAwait(false);
        if (account is null) return NoSuchAccount();

        var result = await SetPasswordAsync(users, protection, account, request?.Password ?? string.Empty).ConfigureAwait(false);
        if (!result.Succeeded) return Refused(result);

        // A reset changes the security stamp, which signs the account out
        // everywhere — the person resetting their own included, so they are
        // signed in again here.
        if (users.GetUserId(user) == account.Id.ToString())
        {
            await signIn.RefreshSignInAsync(account).ConfigureAwait(false);
        }

        return TypedResults.NoContent();
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteAccount(
        Guid id,
        UserManager<Account> users,
        CancellationToken cancellationToken)
    {
        await Census.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var account = await users.FindByIdAsync(id.ToString()).ConfigureAwait(false);
            if (account is null) return NoSuchAccount();

            // With none left the first visit would make one, so whoever reached
            // the port next would own the library.
            if (!await users.Users.AnyAsync(other => other.Id != id, cancellationToken).ConfigureAwait(false))
            {
                return TypedResults.Problem(
                    detail: "This is the only account; add another before removing it.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            // Hearts and keys go by the foreign keys' cascade.
            var result = await users.DeleteAsync(account).ConfigureAwait(false);
            return result.Succeeded ? TypedResults.NoContent() : Refused(result);
        }
        finally
        {
            Census.Release();
        }
    }

    /// <summary>
    /// Sets a password for both clients: Identity's hash, and the protected
    /// copy Subsonic's token check reads, saved together.
    /// </summary>
    /// <remarks>
    /// Through a reset token, Identity's own path for setting a password without
    /// the old one, so its validators run, the hash and the copy land in one
    /// update, and the security stamp changes.
    /// </remarks>
    internal static async Task<IdentityResult> SetPasswordAsync(
        UserManager<Account> users,
        IDataProtectionProvider protection,
        Account account,
        string password)
    {
        account.SubsonicPassword = Protector(protection).Protect(password);

        var token = await users.GeneratePasswordResetTokenAsync(account).ConfigureAwait(false);
        return await users.ResetPasswordAsync(account, token, password).ConfigureAwait(false);
    }

    /// <summary>The password Subsonic's token check needs, or null where it cannot be read.</summary>
    /// <remarks>
    /// Null when the Data Protection key ring that protected it is gone — a
    /// new machine, a cleared home directory. The account then signs in to
    /// Subsonic again once its password is set again.
    /// </remarks>
    internal static string? SubsonicPassword(IDataProtectionProvider protection, Account account)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (account.SubsonicPassword is null) return null;

        try
        {
            return Protector(protection).Unprotect(account.SubsonicPassword);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>What an API key is looked up by.</summary>
    internal static byte[] HashOf(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    private static IDataProtector Protector(IDataProtectionProvider protection) =>
        protection.CreateProtector(PasswordPurpose);

    private static async Task<(Account? Account, ProblemHttpResult? Problem)> CreateAsync(
        AccountRequest request,
        UserManager<Account> users,
        IDataProtectionProvider protection,
        IClock clock)
    {
        var account = new Account
        {
            Id = Guid.CreateVersion7(),
            UserName = request?.Name?.Trim(),
            CreatedUtc = StoreTime.ToStorePrecision(clock.UtcNow),
        };

        var password = request?.Password ?? string.Empty;
        account.SubsonicPassword = Protector(protection).Protect(password);

        var result = await users.CreateAsync(account, password).ConfigureAwait(false);
        return result.Succeeded ? (account, null) : (null, Refused(result));
    }

    private static AccountResponse Response(Account account) =>
        new(account.Id, account.UserName!, account.CreatedUtc);

    private static ProblemHttpResult Refused(IdentityResult result) =>
        TypedResults.Problem(
            detail: string.Join(" ", result.Errors.Select(error => error.Description)),
            statusCode: StatusCodes.Status400BadRequest);

    private static ProblemHttpResult NoSuchAccount() =>
        TypedResults.Problem(detail: "No such account.", statusCode: StatusCodes.Status404NotFound);
}

/// <summary>
/// Who a request acts as: the signed-in account, on the web or on Subsonic.
/// </summary>
/// <remarks>
/// Replaces <see cref="SingleUserCallerContext"/> as what a request resolves,
/// so the journal names the account that pressed the button. Outside a
/// request there is nobody signed in, and the system acts.
/// </remarks>
internal sealed class AccountCallerContext(IHttpContextAccessor http) : ICallerContext
{
    private ClaimsPrincipal? User => http.HttpContext?.User is { Identity.IsAuthenticated: true } user ? user : null;

    public string ActorId => User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? SystemCallerContext.SystemId;

    public string ActorName => User?.Identity?.Name ?? SystemCallerContext.SystemId;

    public string? DeviceId => null;
}

/// <param name="Needed">True while no account exists, so the first visit makes one.</param>
public sealed record AccountSetupResponse(bool Needed);

/// <param name="Name">Letters, digits and <c>-._@+</c>; the name a Subsonic client signs in with too.</param>
public sealed record AccountRequest(string? Name, string? Password);

public sealed record SignInRequest(string? Name, string? Password);

public sealed record PasswordRequest(string? Password);

public sealed record AccountResponse(Guid Id, string Name, DateTimeOffset CreatedUtc);

public sealed record ApiKeyRequest(string? Name);

public sealed record ApiKeyResponse(Guid Id, string Name, DateTimeOffset CreatedUtc);

/// <param name="Key">The key itself, to paste into the client. Not shown again.</param>
public sealed record CreatedApiKeyResponse(Guid Id, string Name, DateTimeOffset CreatedUtc, string Key);
