using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;

namespace Fonoteca.Data;

/// <summary>
/// A person who signs in, to the web client and to a Subsonic client alike.
/// </summary>
/// <remarks>
/// ADR 0018. The library has no owner: the catalogue, the files and the passes
/// are one shared thing, and an account may do what its <see cref="Roles"/>
/// say. What an account owns is what a person owns — their hearts
/// (<c>Love.AccountId</c>), their tags, their playlist folders and their API
/// keys. Playlists stay everyone's.
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

    /// <summary>What this account may do: OpenSubsonic's roles, on the web as on Subsonic.</summary>
    public AccountRoles Roles { get; set; }

    /// <summary>OpenSubsonic's <c>maxBitRate</c>, kept and reported; nothing transcodes, so nothing applies it.</summary>
    public int? MaxBitRate { get; set; }

    /// <summary>A new account's roles where nobody named any: OpenSubsonic's <c>createUser</c> defaults.</summary>
    public const AccountRoles DefaultRoles = AccountRoles.Settings | AccountRoles.Stream;

    /// <summary>
    /// Every role naming something this server does: the first account's, and
    /// every account's that existed before roles did (the owner's choice).
    /// </summary>
    public const AccountRoles Everything =
        AccountRoles.Admin | AccountRoles.Settings | AccountRoles.Download | AccountRoles.Upload
        | AccountRoles.Playlist | AccountRoles.CoverArt | AccountRoles.Stream;
}

/// <summary>
/// OpenSubsonic's user roles, in its own order; each is <c>{name}Role</c> there.
/// </summary>
/// <remarks>
/// ADR 0018. Stored as one integer, so a new role is a new bit and no
/// migration. Five name nothing this server does — comments and ratings,
/// podcasts, jukebox, sharing, video — and are kept and reported for a client
/// to read but bind nothing. Playlist binds nothing either: "changing this
/// role has no effect", the protocol's own words, so playlists stay everyone's.
/// </remarks>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<AccountRoles>))]
public enum AccountRoles
{
    None = 0,

    /// <summary>Accounts, the passes, the file manager, Identify, follows, Qobuz: whatever changes the library.</summary>
    Admin = 1 << 0,

    /// <summary>Their own password and API keys.</summary>
    Settings = 1 << 1,

    /// <summary>A file as an attachment, here and over Subsonic's <c>download</c>.</summary>
    Download = 1 << 2,

    /// <summary>The file manager's upload.</summary>
    Upload = 1 << 3,

    Playlist = 1 << 4,

    /// <summary>Covers, artist pictures and a person's corrections to an album or artist.</summary>
    CoverArt = 1 << 5,

    Comment = 1 << 6,

    Podcast = 1 << 7,

    /// <summary>Playing a file, here and over Subsonic's <c>stream</c>.</summary>
    Stream = 1 << 8,

    Jukebox = 1 << 9,

    Share = 1 << 10,

    VideoConversion = 1 << 11,
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
