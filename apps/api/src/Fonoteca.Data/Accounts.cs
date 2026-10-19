using Microsoft.AspNetCore.Identity;

namespace Fonoteca.Data;

/// <summary>
/// A person who signs in, to the web client and to a Subsonic client alike.
/// </summary>
/// <remarks>
/// ADR 0018. The library has no owner: the catalogue, the files and the passes
/// are one shared thing, and every account may do everything (the owner's
/// choice, "roles may come later"). What an account owns is what a person
/// owns — their hearts (<c>Love.AccountId</c>) and their API keys.
/// Playlists stay everyone's.
///
/// ASP.NET Core Identity's own user, so the password hash, the lockout and the
/// security stamp that signs a person out everywhere on a reset are the
/// framework's and not a copy of them.
/// </remarks>
public sealed class Account : IdentityUser<Guid>
{
    /// <summary>
    /// The password again, protected by ASP.NET Core Data Protection, for
    /// Subsonic's token check.
    /// </summary>
    /// <remarks>
    /// <b>Held recoverably because the protocol leaves no other way.</b> A
    /// Subsonic client signs in with <c>t = md5(password + salt)</c>, which a
    /// server can only check by computing it, and the owner chose one password
    /// for both clients. Identity's hash beside it is what the web sign-in
    /// checks. The key that opens this is in the Data Protection key ring, not
    /// in the database, so a database backup alone holds nothing usable. Set
    /// in the same save as the hash, every time, by <c>Accounts.SetPassword</c>.
    /// </remarks>
    public string? SubsonicPassword { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
}

/// <summary>
/// A Subsonic API key: OpenSubsonic's <c>apiKeyAuthentication</c>.
/// </summary>
/// <remarks>
/// Made by the server, shown once, never expiring, revoked by deleting the row
/// — all the extension's own terms. Only the SHA-256 of the key is kept: a key
/// is 32 random bytes, so a slow hash buys nothing a fast one does not, and
/// the lookup has to be by the hash.
/// </remarks>
public sealed class ApiKey
{
    public const int NameLength = 100;

    public required Guid Id { get; init; }

    public required Guid AccountId { get; init; }

    /// <summary>What the person called it, so they know which one to revoke.</summary>
    public required string Name { get; set; }

    public required byte[] Hash { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }
}
