using TagLib;
using TagLib.Mpeg4;
using TagLib.Ogg;
using File = TagLib.File;

namespace Fonoteca.Tagging;

/// <summary>
/// The second opinion: what TagLib# sees in a file.
/// </summary>
/// <remarks>
/// TagLib# is never asked to write anything, here or anywhere. Its whole job is
/// to disagree — a write is only committed when two libraries built by different
/// people, parsing the same bytes independently, say the same thing about them.
///
/// The AcoustID is read through each format's <b>native</b> accessor rather than
/// a generic field map. That distinction is the point of the exercise: it must
/// confirm the value landed in the frame the format specifies, not merely that
/// it is somewhere in the file under a name we picked. A Vorbis comment called
/// <c>ACOUSTID ID</c> would satisfy a generic lookup and satisfy nobody's tag
/// reader.
/// </remarks>
internal static class VerifierReading
{
    private const string ITunesMean = "com.apple.iTunes";

    public static TagSnapshot Describe(File file)
    {
        var pictures = file.Tag.Pictures ?? [];

        return new TagSnapshot
        {
            AcoustId = TagReader.Normalise(ReadAcoustId(file)),
            Fields = ReadFields(file),
            PictureCount = pictures.Length,
            PictureDigests = [.. pictures.Select(p => TagSnapshot.Digest(p.Data.Data))],
            DurationSeconds = file.Properties?.Duration.TotalSeconds ?? 0,
            BitrateKbps = file.Properties?.AudioBitrate ?? 0,
        };
    }

    /// <summary>The AcoustID, from whichever tag system this container actually uses.</summary>
    private static string? ReadAcoustId(File file)
    {
        if (file.GetTag(TagTypes.Xiph, create: false) is XiphComment xiph)
        {
            var values = xiph.GetField(AcoustIdTagField.Uppercase);
            if (values.Length > 0) return values[0];
        }

        if (file.GetTag(TagTypes.Id3v2, create: false) is TagLib.Id3v2.Tag id3)
        {
            foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
            {
                if (!string.Equals(frame.Description, AcoustIdTagField.TitleCase, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (frame.Text.Length > 0) return frame.Text[0];
            }
        }

        if (file.GetTag(TagTypes.Apple, create: false) is AppleTag apple)
        {
            // The freeform atom, spelled out: ---- / mean=com.apple.iTunes /
            // name=Acoustid Id. ATL writes the prefix itself, so this is where we
            // confirm it actually did.
            var dash = apple.GetDashBox(ITunesMean, AcoustIdTagField.TitleCase);
            if (!string.IsNullOrEmpty(dash)) return dash;
        }

        if (file.GetTag(TagTypes.Ape, create: false) is TagLib.Ape.Tag ape)
        {
            var item = ape.GetItem(AcoustIdTagField.Uppercase);
            if (item is not null && item.ToStringArray().Length > 0) return item.ToStringArray()[0];
        }

        return null;
    }

    /// <summary>
    /// The common fields, under the same names <see cref="TagReader.Describe"/>
    /// uses, so a lost-field comparison across the two libraries is meaningful.
    /// </summary>
    private static SortedDictionary<string, string> ReadFields(File file)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var tag = file.Tag;

        Add(fields, "TITLE", tag.Title);
        Add(fields, "ARTIST", tag.FirstPerformer);
        Add(fields, "ALBUM", tag.Album);
        Add(fields, "ALBUMARTIST", tag.FirstAlbumArtist);
        Add(fields, "GENRE", tag.FirstGenre);
        Add(fields, "COMPOSER", tag.FirstComposer);
        Add(fields, "COMMENT", tag.Comment);

        return fields;
    }

    private static void Add(SortedDictionary<string, string> fields, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value)) fields[key] = value;
    }
}
