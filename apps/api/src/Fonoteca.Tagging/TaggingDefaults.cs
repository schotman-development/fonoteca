using System.Runtime.CompilerServices;

namespace Fonoteca.Tagging;

/// <summary>
/// The ATL settings this assembly depends on, applied before anything reads or
/// writes a tag.
/// </summary>
/// <remarks>
/// <b>ATL splits and rejoins field values on a separator character, and its
/// default is a semicolon.</b> Write
/// <c>"Dvořák, Ginastera, Sarasate; Hilary Hahn, …"</c> into a Vorbis comment
/// and it comes back as <c>"Dvořák, Ginastera, Sarasate;Hilary Hahn, …"</c> —
/// one space short, because the value was split into two and rejoined with a
/// bare <c>';'</c>. <see cref="TagWriter"/> reads that as a field it did not
/// intend to change, and aborts. Measured on Hilary Hahn's <i>Eclipse</i>: two
/// runs, nineteen files, nineteen failures, one reason.
///
/// The trap is in the data rather than the file. MusicBrainz uses <c>"; "</c> as
/// a join phrase to separate composers from performers, so it is a property of
/// classical billing lines: twenty releases and 489 files in the target library
/// carry one, and none of them could be tagged at all.
///
/// U+001F is the unit separator — it has no glyph, no keyboard, and cannot occur
/// in a credit line, so nothing this application writes is ever split. A file
/// that genuinely holds several values for one key still reads back as all of
/// them joined, which is what the setting is for; it just joins on a character
/// no real value contains.
///
/// A module initializer rather than startup wiring, because ATL is configured
/// through a mutable static: the setting has to be in place before the first
/// <c>Track</c> is constructed, and that happens in tests and in design-time
/// tools that never build a host. This runs once, on first use of anything in
/// this assembly, which is the only ordering guarantee that covers all of them.
/// </remarks>
internal static class TaggingDefaults
{
    /// <summary>The character ATL joins and splits multi-valued fields on.</summary>
    internal const char ValueSeparator = '\u001F';

#pragma warning disable CA2255 // ATL is configured through a mutable static; nothing else runs early enough.
    [ModuleInitializer]
    internal static void Apply() => ATL.Settings.DisplayValueSeparator = ValueSeparator;
#pragma warning restore CA2255
}
