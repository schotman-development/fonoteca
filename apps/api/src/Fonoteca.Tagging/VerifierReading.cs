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
internal static partial class VerifierReading
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
            RecordedDate = ReadDate(file),
        };
    }

    /// <summary>
    /// The date as stored, from whichever tag system this container uses: the
    /// Vorbis <c>DATE</c>, ID3v2's <c>TDRC</c> (ID3v2.3's <c>TYER</c> and <c>TDAT</c>
    /// as TagLib# folds them), MP4's <c>©day</c>, APE's <c>Year</c>. Cut to its
    /// ISO prefix, so a timestamp's time or a spelling ATL merely reformats does
    /// not read as a lost date; anything that is not ISO is compared whole.
    /// </summary>
    private static string? ReadDate(File file)
    {
        if (file.GetTag(TagTypes.Xiph, create: false) is XiphComment xiph)
        {
            var values = xiph.GetField("DATE");
            if (values.Length > 0) return Dated(values[0]);
        }

        if (file.GetTag(TagTypes.Id3v2, create: false) is TagLib.Id3v2.Tag id3
            && Text(id3, "TDRC") is { } recorded)
        {
            // TagLib# folds ID3v2.3's TYER and TDAT into one TDRC as it reads, and
            // takes TDAT — DDMM by the specification — as month then day: a file
            // dated 17 August reads "1959-17-08". Put back for 2.3 tags only. A
            // 2.3 tag carrying a genuine TDRC is swapped too, which can only make
            // a write refused that could have gone ahead, never the reverse.
            return id3.Version == 3 && SwappedDay().Match(recorded) is { Success: true } swapped
                ? $"{swapped.Groups[1].Value}-{swapped.Groups[3].Value}-{swapped.Groups[2].Value}"
                : Dated(recorded);
        }

        if (file.GetTag(TagTypes.Apple, create: false) is AppleTag apple
            && apple.GetText(ByteVector.FromString("\u00a9day", StringType.Latin1)) is { Length: > 0 } days)
        {
            return Dated(days[0]);
        }

        if (file.GetTag(TagTypes.Ape, create: false) is TagLib.Ape.Tag ape
            && ape.GetItem("Year")?.ToString() is { Length: > 0 } apeYear)
        {
            return Dated(apeYear);
        }

        return null;
    }

    private static string? Text(TagLib.Id3v2.Tag tag, string frame) =>
        TagLib.Id3v2.TextInformationFrame.Get(tag, ByteVector.FromString(frame, StringType.Latin1), false)?.Text
            is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text[0])
            ? text[0]
            : null;

    private static string Dated(string value)
    {
        var iso = IsoPrefix().Match(value);
        return iso.Success ? iso.Value : value.Trim();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{4}(-\d{2}(-\d{2})?)?(?![\d-])")]
    private static partial System.Text.RegularExpressions.Regex IsoPrefix();

    [System.Text.RegularExpressions.GeneratedRegex(@"^(\d{4})-(\d{2})-(\d{2})")]
    private static partial System.Text.RegularExpressions.Regex SwappedDay();

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
