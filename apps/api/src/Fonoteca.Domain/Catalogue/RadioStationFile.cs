using System.Text;
using System.Xml;
using System.Xml.Linq;
using Fonoteca.Domain.Acquisition;

namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// Where an internet radio station's file lives and what it says.
/// </summary>
/// <remarks>
/// <b>A station is its file</b> (the owner's choice, over a table): an XSPF
/// playlist at the top of <see cref="Folder"/>, so the stations a library saved
/// go wherever the library goes. XSPF because it is the one playlist format
/// with a written spec and a field for each thing a station has — the stream
/// in <c>location</c>, the name in <c>title</c>, the home page in <c>info</c>,
/// the logo in <c>image</c> — and radio-browser.info exports stations as it.
///
/// Whatever wrote the file is believed, so a station is read leniently — the
/// playlist's title, else its first track's, else the file's name — and
/// written back with only those fields restated: every other element, an
/// <c>identifier</c> or an <c>annotation</c>, goes back as it came.
///
/// <b>Every address is an absolute http or https URL</b>, or it is not the
/// station's: a client plays the stream and shows the home page and the logo,
/// so a file naming anything else lists no such field, and a file whose stream
/// is not one is no station.
/// </remarks>
public static class RadioStationFile
{
    public const string Folder = "Radio";

    public const string Extension = ".xspf";

    public const int NameLength = 200;

    public const int UrlLength = 2000;

    private static readonly XNamespace Xspf = "http://xspf.org/ns/0/";

    /// <summary>A track's children in the spec's order, which a field added to a track keeps.</summary>
    private static readonly string[] TrackOrder =
        ["location", "identifier", "title", "creator", "annotation", "info", "image"];

    /// <summary>The library-relative path a station of this name is written to.</summary>
    public static string PathFor(string name) =>
        $"{Folder}/{StagedFileName.Segment(name)}{Extension}";

    /// <summary>Whether an address is one a client may be handed: absolute, http or https, not overlong, on one line.</summary>
    public static bool WebAddress(string? url) =>
        url is { Length: <= UrlLength }
        && !url.Any(character => char.IsControl(character) || character is '\uFFFE' or '\uFFFF')
        && Uri.TryCreate(url, UriKind.Absolute, out var address)
        && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps);

    /// <summary>The station a file describes, or null where it is not XSPF or names no web stream.</summary>
    /// <param name="path">The file's own path, whose name is the station's where the file gives none.</param>
    public static RadioStation? Read(string text, string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (Parse(text)?.Root is not { } playlist || playlist.Name.LocalName != "playlist") return null;

        var track = Child(Child(playlist, "trackList"), "track");

        if (Values(track, "location").FirstOrDefault(WebAddress) is not { } stream) return null;

        var name = Values(playlist, "title").Concat(Values(track, "title")).FirstOrDefault(title => title.Length > 0)
            ?? Path.GetFileNameWithoutExtension(path);

        return new RadioStation(
            name,
            stream,
            Values(track, "info").FirstOrDefault(WebAddress),
            Values(track, "image").FirstOrDefault(WebAddress));
    }

    /// <summary>
    /// The file's text with this station in it: <paramref name="text"/> with
    /// its title, stream, home page and logo restated, or a new file where
    /// there is none.
    /// </summary>
    /// <remarks>
    /// A field the station has not got is taken out of the track, so the file
    /// reads back as the station written; one in the playlist itself is left,
    /// since nothing reads it there.
    /// </remarks>
    public static string Write(string? text, RadioStation station)
    {
        ArgumentNullException.ThrowIfNull(station);

        var document = (text is null ? null : Parse(text)) is { Root.Name.LocalName: "playlist" } read
            ? read
            : new XDocument(new XElement(Xspf + "playlist", new XAttribute("version", "1")));

        var playlist = document.Root!;
        var ns = playlist.Name.Namespace;

        Set(playlist, ns + "title", station.Name, first: true);

        var list = Child(playlist, "trackList") ?? Added(playlist, new XElement(ns + "trackList"));
        var track = Child(list, "track") ?? Added(list, new XElement(ns + "track"));

        Set(track, ns + "location", station.StreamUrl);
        Set(track, ns + "title", station.Name);
        Set(track, ns + "info", station.HomePageUrl);
        Set(track, ns + "image", station.ImageUrl);

        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, IndentChars = "  " };
        using var bytes = new MemoryStream();

        using (var writer = XmlWriter.Create(bytes, settings)) document.Save(writer);

        return Encoding.UTF8.GetString(bytes.ToArray()) + "\n";
    }

    private static XDocument? Parse(string text)
    {
        // No DTD: a station has no use for one, and entity expansion is the one
        // way a small file becomes a large read.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true };

        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static XElement? Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(element => element.Name.LocalName == name);

    private static IEnumerable<string> Values(XElement? parent, string name) =>
        parent?.Elements().Where(element => element.Name.LocalName == name).Select(element => element.Value.Trim()) ?? [];

    private static XElement Added(XElement parent, XElement child)
    {
        parent.Add(child);
        return child;
    }

    /// <summary>The first such child set to the value, or every one taken out where there is none; added in the spec's place where missing.</summary>
    private static void Set(XElement parent, XName name, string? value, bool first = false)
    {
        if (value is null)
        {
            parent.Elements().Where(element => element.Name.LocalName == name.LocalName).Remove();
            return;
        }

        var existing = Child(parent, name.LocalName);

        if (existing is not null)
        {
            existing.Value = value;
            return;
        }

        var added = new XElement(name, value);

        if (first)
        {
            parent.AddFirst(added);
            return;
        }

        var before = Array.IndexOf(TrackOrder, name.LocalName);
        var after = parent.Elements().LastOrDefault(element => Array.IndexOf(TrackOrder, element.Name.LocalName) is >= 0 and var at && at < before);

        if (after is null) parent.AddFirst(added);
        else after.AddAfterSelf(added);
    }
}

/// <param name="Name">What the station is called.</param>
/// <param name="StreamUrl">What a client plays.</param>
/// <param name="HomePageUrl">The station's own web page, or null.</param>
/// <param name="ImageUrl">Its logo, or null.</param>
public sealed record RadioStation(string Name, string StreamUrl, string? HomePageUrl, string? ImageUrl);
