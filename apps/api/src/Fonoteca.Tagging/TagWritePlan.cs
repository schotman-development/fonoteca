using Fonoteca.Domain.Abstractions;

namespace Fonoteca.Tagging;

/// <summary>
/// What a write would do, computed before anything is opened for writing.
/// </summary>
/// <remarks>
/// ADR 0002's dry run, as a value. Nothing writes without one, and the plan is
/// also what the write is checked against afterwards: if the file's current
/// value is no longer <see cref="TagFieldChange.From"/> by the time the write
/// runs, something else edited the file in between and the operation is
/// abandoned rather than made to win.
/// </remarks>
public sealed record TagWritePlan(
    LibraryPath Path,
    IReadOnlyList<TagFieldChange> Changes,
    TagSnapshot Before)
{
    /// <summary>Nothing to do — the file already says what we were going to write.</summary>
    public bool IsNoOp => Changes.Count == 0;
}

/// <summary>One field, and what it would become.</summary>
/// <remarks>
/// <see cref="From"/> is nullable and that distinction is load-bearing for undo:
/// null means the field was <b>absent</b>, so reversing the write means removing
/// it, while an empty string would mean the field existed and was blank, so
/// reversing means restoring a blank. A non-nullable string gets this wrong
/// permanently and silently.
/// </remarks>
public sealed record TagFieldChange(string Field, string? From, string? To);

/// <summary>How a write ended.</summary>
public enum TagWriteStatus
{
    /// <summary>Written, verified by both libraries, and committed.</summary>
    Written = 0,

    /// <summary>The file already carried this value. Nothing was opened.</summary>
    NothingToDo = 1,

    /// <summary>
    /// <c>Fonoteca:AllowFileMutation</c> is off. The plan is real; the write was
    /// not attempted.
    /// </summary>
    Refused = 2,

    /// <summary>
    /// A check failed after the staged file was written. The original is
    /// untouched and the staged copy is gone.
    /// </summary>
    VerificationFailed = 3,

    /// <summary>This container cannot carry an AcoustID.</summary>
    Unsupported = 4,

    /// <summary>The write itself failed — disk, permissions, a refusal from ATL.</summary>
    Failed = 5,
}

/// <summary>What a write did, and why.</summary>
public sealed record TagWriteResult(
    TagWritePlan? Plan,
    TagWriteStatus Status,
    string? Detail = null,

    /// <summary>The undo journal entry, when one was written.</summary>
    Guid? UndoEventId = null,

    /// <summary>
    /// The file's size and timestamp after the swap.
    /// </summary>
    /// <remarks>
    /// Returned rather than left for the caller to re-stat, because the caller
    /// <b>must</b> store these: a tag write changes the bytes, and a catalogue
    /// that still remembers the old size and mtime will see the file as modified
    /// on the next scan and discard everything derived from it — including the
    /// AcoustID just written. That loop is the sharpest edge in this feature.
    /// </remarks>
    FileFacts? Committed = null)
{
    public bool Wrote => Status == TagWriteStatus.Written;
}

/// <summary>Whether this process may modify audio files at all.</summary>
/// <remarks>
/// A dedicated options type rather than a bool parameter so the decision has a
/// name at the call site, and so <c>Fonoteca.Tagging</c> never learns what
/// <c>FonotecaOptions</c> is. The default is the safe one, deliberately: a
/// caller that forgets to configure this writes nothing.
/// </remarks>
public sealed record TagWriterOptions
{
    public bool AllowFileMutation { get; init; }
}
