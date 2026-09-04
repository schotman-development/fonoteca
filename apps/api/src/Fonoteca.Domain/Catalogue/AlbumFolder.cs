namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// Which files a library groups together as one album.
/// </summary>
/// <remarks>
/// The boundary, not the name. <see cref="Identification.ReleaseAttribution"/>
/// refuses to read a folder's <i>name</i> for good reason — it dates <i>Off the
/// Wall</i> to 1979 when the audio present is the 2015 remaster — but that is an
/// objection to believing a claim about <i>which release</i>. Believing a claim
/// about <i>which files belong together</i> is a different bet, and a much better
/// one: the name was typed by whatever wrote the files, while the grouping is the
/// one thing about a library that is nearly always right.
///
/// It is what stops separate albums being glued into a box set. A compilation
/// holding one track from two different albums pulls both albums' files into one
/// component under an expanding gather, and the box set is then the honest best
/// answer for the merged set. Cut the set at the folder and it cannot happen.
///
/// <b>Two directories deep, and that is measured rather than assumed.</b> Every
/// path in the target library is <c>Artist/Album/file</c> or one below it; there
/// is no depth four, and all 99 of the depth-three folders are discs of their
/// parent. A <c>CD 01</c>-style rule was the obvious alternative and it is the
/// worse one on this data: the disc folders are spelled <c>CD 01</c>,
/// <c>Disc 1</c> <i>and</i> <c>Digital Media 01</c> — the last being a
/// MusicBrainz <i>medium format</i>, so the pattern would have to know
/// <c>HDCD</c>, <c>12" Vinyl</c> and <c>Hybrid SACD</c> too, and would silently
/// split 453 files across nine albums the day it did not.
///
/// A genre-first or composer-first tree would want three. There is no way to
/// tell which from a path, and guessing is worse than one edit here.
///
/// A rule, not I/O — a string in, a string out, testable without a filesystem.
/// </remarks>
public static class AlbumFolder
{
    /// <summary>How many directories deep an album sits.</summary>
    /// <remarks>
    /// The same depth <c>seating.ts</c> cuts the by-hand matching screen at, and
    /// the same one <see cref="Acquisition.UpgradeScan.AlbumFolder"/> groups the
    /// upgrade worklist by — which is why that method now defers to this one
    /// rather than carrying its own copy of the arithmetic. Three screens
    /// disagreeing about where an album stops is a bug nobody would ever see
    /// reported as one.
    /// </remarks>
    public const int Depth = 2;

    /// <summary>
    /// The album a library-relative file path belongs to.
    /// </summary>
    /// <param name="path">Library-relative, forward slashes, e.g.
    /// <c>Michael Jackson/Off the Wall/CD 01/03 Off the Wall.flac</c>.</param>
    /// <returns>
    /// The folder every file of that album shares, without a trailing slash — the
    /// component's identity, and the key its open question is stored under.
    /// A file loose at the library root belongs to the root, which is the empty
    /// string: one such file exists and it is a question about the library's
    /// layout rather than about the music.
    /// </returns>
    public static string Of(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // Library paths are stored with forward slashes and the live catalogue
        // holds no backslash at all, but the upgrade worklist normalised before
        // cutting and unifying the two rules must not quietly drop that.
        path = path.Replace('\\', '/');

        // Shallower than the cut is kept whole: a file directly under an artist
        // has no album folder, and inventing one would put it with the artist's
        // real albums.
        var cut = 0;

        for (var taken = 0; taken < Depth; taken++)
        {
            var next = path.IndexOf('/', cut);
            if (next < 0) break;

            cut = next + 1;
        }

        return cut == 0 ? string.Empty : path[..(cut - 1)];
    }
}
