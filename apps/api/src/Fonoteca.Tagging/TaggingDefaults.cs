using System.Reflection;
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

    /// <summary>Whether ATL now writes an ID3v2 <c>UFID</c> frame as the format defines it.</summary>
    /// <remarks>
    /// <b>ATL 7.16–7.18 writes a text-encoding byte into every <c>UFID</c>
    /// frame</b>, a frame that has none: its owner comes out as
    /// <c>"\x03http://musicbrainz.org"</c>, and TagLib#, Picard and anything else
    /// looking for MusicBrainz's owner no longer find the recording id. Every MP3
    /// Picard tagged carries one, and every save rewrites it — 748 files here
    /// before this was seen. ATL keeps the frames that carry no encoding byte in
    /// a private set, <c>noTextEncodingFields</c>, and <c>UFID</c> belonging in
    /// it is the whole upstream fix, so this adds it.
    ///
    /// Reflection into a private field is the fragile part, which is why this
    /// is a flag rather than an assumption: a test fails the day an upgrade moves
    /// the field, and <see cref="TagWriter"/> refuses any write that would change
    /// a <c>UFID</c> in a way it did not plan, whether this held or not.
    /// </remarks>
    internal static bool UfidIsBinary { get; private set; }

#pragma warning disable CA2255 // ATL is configured through a mutable static; nothing else runs early enough.
    [ModuleInitializer]
    internal static void Apply()
    {
        ATL.Settings.DisplayValueSeparator = ValueSeparator;

        if (typeof(ATL.Track).Assembly
                .GetType("ATL.AudioData.IO.ID3v2")
                ?.GetField("noTextEncodingFields", BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null) is ISet<string> frames)
        {
            frames.Add("UFID");
            UfidIsBinary = true;
        }
    }
#pragma warning restore CA2255
}
