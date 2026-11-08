using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// A listener's ListenBrainz account: who a token belongs to, what they have
/// listened to, and what to add to it.
/// </summary>
/// <remarks>
/// A refusal of the token by <see cref="UserOfAsync"/> is an answer; a
/// refusal by <see cref="SubmitAsync"/> is a
/// <see cref="ProviderRejectedException"/>, of the token or of the listens,
/// which the caller tells apart by asking <see cref="UserOfAsync"/> again.
/// Anything else that keeps an answer from arriving is a
/// <see cref="ProviderUnavailableException"/>.
/// </remarks>
public interface IListenBrainz
{
    /// <summary>The user name the token signs in as, or null when ListenBrainz refuses it.</summary>
    Task<string?> UserOfAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Up to a page of the user's listens, newest first, all from before
    /// <paramref name="before"/> where it is given; an empty page is the end.
    /// </summary>
    Task<IReadOnlyList<HeardListen>> ListensAsync(string user, DateTimeOffset? before, CancellationToken cancellationToken = default);

    /// <summary>Adds these listens, or says what is playing now where <paramref name="listens"/> is one with no time.</summary>
    Task SubmitAsync(string token, IReadOnlyList<ListenToSubmit> listens, CancellationToken cancellationToken = default);
}

/// <summary>One listen ListenBrainz holds, as its client submitted it and as ListenBrainz mapped it.</summary>
/// <param name="ListenedAt">When it began, in whole seconds.</param>
/// <param name="Recording">The recording MBID: ListenBrainz's own mapping, else the one the client sent.</param>
public sealed record HeardListen(
    DateTimeOffset ListenedAt,
    string Artist,
    string Track,
    string? Release,
    Mbid? Recording);

/// <summary>A play to add to ListenBrainz.</summary>
/// <param name="ListenedAt">When it began, in whole seconds; null for what is playing now.</param>
public sealed record ListenToSubmit(
    DateTimeOffset? ListenedAt,
    string Artist,
    string Track,
    string? Release,
    Mbid? Recording,
    int? TrackNumber,
    int? DurationSeconds);
