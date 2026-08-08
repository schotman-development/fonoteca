using System.Diagnostics.CodeAnalysis;

namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// An external service could not answer.
/// </summary>
/// <remarks>
/// Declared in the domain rather than in Fonoteca.Providers because it is part
/// of the contract: a caller that only knows <see cref="IAcoustIdLookup"/>
/// still has to be able to catch what it throws.
///
/// There are exactly two subclasses, because there are exactly two things a
/// caller does differently. <see cref="ProviderUnavailableException"/> is worth
/// trying again later; <see cref="ProviderRejectedException"/> is not, until a
/// human changes something. Which HTTP status, which vendor error number, which
/// retry attempt — that belongs in the message and the log, not in more types.
///
/// Note what is deliberately <i>not</i> an exception here: "no match". A
/// fingerprint that AcoustID has never seen is the ordinary outcome for a
/// bootleg, a field recording or a DJ mix, and a catalogue of 100,000 files
/// will contain thousands of them. Lookups return an empty list for that; only
/// a failure to ask the question at all throws.
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification =
        "The standard constructors exist to allow an exception with no context. That is exactly " +
        "what must not be possible here: an entry in a six-hour identification run's log that " +
        "cannot say which of four external services failed is not worth writing down.")]
public abstract class ProviderException : Exception
{
    protected ProviderException(string provider, string message)
        : base(message) => Provider = provider;

    protected ProviderException(string provider, string message, Exception innerException)
        : base(message, innerException) => Provider = provider;

    /// <summary>Which service failed — <c>AcoustID</c>, <c>MusicBrainz</c>.</summary>
    public string Provider { get; }
}

/// <summary>
/// The service is reachable in principle but did not answer this time: a
/// timeout, a 5xx, a rate limit that outlived the retries, a DNS failure.
/// </summary>
/// <remarks>
/// By the time this escapes, the resilience pipeline has already retried. It
/// means "come back later", which for a batch identifying 100,000 files means
/// leave this file unidentified and keep going — not abort the run.
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "See ProviderException.")]
public sealed class ProviderUnavailableException : ProviderException
{
    public ProviderUnavailableException(string provider, string message)
        : base(provider, message) { }

    public ProviderUnavailableException(string provider, string message, Exception innerException)
        : base(provider, message, innerException) { }
}

/// <summary>
/// The service understood the request and refused it: a missing or invalid API
/// key, a malformed fingerprint, an identifier it will not accept.
/// </summary>
/// <remarks>
/// Retrying this is pure waste — every attempt fails identically and counts
/// against the rate limit. It is a configuration or a caller bug, and the
/// message is written to be read by whoever has to fix it.
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "See ProviderException.")]
public sealed class ProviderRejectedException : ProviderException
{
    public ProviderRejectedException(string provider, string message)
        : base(provider, message) { }

    public ProviderRejectedException(string provider, string message, Exception innerException)
        : base(provider, message, innerException) { }
}
