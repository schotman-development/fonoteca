using System.Globalization;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Events;

namespace Fonoteca.Tagging;

/// <summary>
/// Writes an AcoustID into an audio file, or refuses to.
/// </summary>
/// <remarks>
/// <b>The sequence lives in <see cref="TagWriter"/> now; this is the one field
/// the identification pass writes, named.</b> It was the whole implementation
/// first, and generalising it rather than copying it was deliberate: the write
/// path is the only code here that can destroy something a rescan cannot
/// rebuild, and two copies of it would drift in exactly the way nobody notices
/// until a library is already wrong.
///
/// What stays here is what is specific to this one fact:
///
/// <list type="bullet">
/// <item><b>The diff is made against <see cref="TagSnapshot.AcoustId"/>, not
/// against the raw field</b>, and case-insensitively. That reading is normalised
/// — trimmed and lowercased — so the 227 files Picard tagged years ago in upper
/// case are recognised as already carrying the answer rather than rewritten to
/// say the same thing differently.</item>
/// <item><b>Its own event types.</b> Undoing an identification's tag write and
/// undoing a library-wide catalogue write are different acts, and one event type
/// covering both would make the query that finds one find both.</item>
/// </list>
/// </remarks>
public sealed class AcoustIdTagWriter(
    IAudioFileStore files,
    TagReader reader,
    IEventLog events,
    IClock clock,
    TagWriterOptions options)
{
    /// <summary>What the journal calls this kind of write.</summary>
    public const string EventPrefix = "tagging.acoustid";

    /// <summary>Event type for a committed write. Dotted, matching <see cref="Fonoteca.Domain.Events.DomainEvent.Type"/>.</summary>
    public const string WrittenEventType = EventPrefix + ".written";

    /// <summary>Event type for a write that was planned and refused.</summary>
    public const string RefusedEventType = EventPrefix + ".refused";

    /// <summary>Event type for a write abandoned after verification failed.</summary>
    public const string AbortedEventType = EventPrefix + ".aborted";

    /// <summary>The subject vocabulary the journal uses for a file.</summary>
    public const string FileSubject = TagWriter.FileSubject;

    /// <inheritdoc cref="TagWriter.ToleratedSizeDrift"/>
    public static long ToleratedSizeDrift(long originalBytes) =>
        TagWriter.ToleratedSizeDrift(originalBytes);

    private readonly TagReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    private readonly TagWriter _writer = new(files, reader, events, clock, options);

    /// <summary>Whether this writer would write anything at all.</summary>
    public bool MutationAllowed => _writer.MutationAllowed;

    /// <summary>
    /// What writing <paramref name="acoustId"/> into this file would change.
    /// </summary>
    /// <remarks>Opens the file for reading only. Safe to call with mutation disabled.</remarks>
    public async Task<TagWritePlan?> PlanAsync(
        LibraryPath path,
        Guid acoustId,
        CancellationToken cancellationToken = default)
    {
        var field = AcoustIdTagField.For(path);
        if (field is null) return null;

        var before = await _reader.ReadAsync(path, null, cancellationToken).ConfigureAwait(false);
        var desired = acoustId.ToString("D", CultureInfo.InvariantCulture);

        // Against the normalised reading rather than the raw field, and
        // case-insensitively — see the class remarks.
        var changes = string.Equals(before.AcoustId, desired, StringComparison.OrdinalIgnoreCase)
            ? Array.Empty<TagFieldChange>()
            : [new TagFieldChange(field, before.AcoustId, desired)];

        return new TagWritePlan(path, changes, before);
    }

    /// <inheritdoc cref="TagWriter.ApplyAsync"/>
    public Task<TagWriteResult> ApplyAsync(
        TagWritePlan? plan,
        string subjectId,
        string correlationId,
        string actorId,
        CancellationToken cancellationToken = default) =>
        _writer.ApplyAsync(plan, subjectId, correlationId, actorId, EventPrefix, cancellationToken);

    /// <summary>The AcoustID, as a catalogue tag write would spell it.</summary>
    /// <remarks>
    /// So the library-wide pass can carry the AcoustID along with everything
    /// else rather than needing a second write of the same file. Both paths
    /// resolve the field name through <see cref="AcoustIdTagField"/>, so they
    /// cannot disagree about where it goes.
    /// </remarks>
    public static string CanonicalField => CatalogueTags.AcoustIdField;
}
