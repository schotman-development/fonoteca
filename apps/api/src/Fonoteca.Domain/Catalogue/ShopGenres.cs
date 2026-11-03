namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// A shop's genre for an album, stated the way MusicBrainz states one.
/// </summary>
/// <remarks>
/// <b>Stated, not folded</b>, so a shop's answer goes into
/// <see cref="ReleaseGroup.Genres"/> beside MusicBrainz's and every reader —
/// <see cref="Genres.Carried"/>, <see cref="ClassicalWorks"/>, the tag write —
/// takes it exactly as it takes MusicBrainz's. Each name is a MusicBrainz
/// genre or one of the tree's own, which <see cref="Genres.Of"/> folds; a
/// shop genre with no counterpart ("Music", Qobuz's "Pop/Rock") is no answer,
/// since <see cref="Genres.Other"/> says nothing a missing genre does not.
///
/// <b>Apple Music by name, Qobuz by id.</b> Apple answers in English, and most
/// of its names are MusicBrainz's once lowercased; the rest are spelled out
/// here. Qobuz answers in the account's language (Dutch here), so its genre is
/// read by its id and the ids above it, broadest first: "Symphonies" is
/// classical and orchestral. Its ids were read off <c>genre/list</c> on
/// 2026-11-03.
/// </remarks>
public static class ShopGenres
{
    /// <summary>Apple's names whose lowercase is not already one <see cref="Genres.Of"/> folds.</summary>
    public static IReadOnlyDictionary<string, string[]> AppleMusicNames { get; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Alternative"] = ["alternative rock"],
        ["Baroque Era"] = ["classical", "baroque"],
        ["Blues-Rock"] = ["blues rock"],
        ["Christian & Gospel"] = ["religious"],
        ["Christmas"] = ["christmas music"],
        ["Contemporary Blues"] = ["blues"],
        ["Contemporary Era"] = ["classical", "contemporary classical"],
        ["Contemporary Singer/Songwriter"] = ["singer-songwriter"],
        ["Doo Wop"] = ["doo-wop"],
        ["Folk-Rock"] = ["folk rock"],
        ["Foreign Cinema"] = ["soundtracks"],
        ["Fusion"] = ["jazz fusion"],
        ["Hair Metal"] = ["glam metal"],
        ["High Classical"] = ["classical", "classical period"],
        ["Hip-Hop"] = ["hip hop"],
        ["Hip-Hop/Rap"] = ["hip hop"],
        ["Impressionist"] = ["classical", "impressionism"],
        ["Latino"] = ["latin"],
        ["Mainstream Jazz"] = ["jazz"],
        ["Medieval Era"] = ["classical", "medieval"],
        ["Modern Era"] = ["classical", "modern classical"],
        ["Neo-Soul"] = ["neo soul"],
        ["New Acoustic"] = ["contemporary folk"],
        ["Original Score"] = ["soundtracks"],
        ["Pop/Rock"] = ["pop rock"],
        ["Prog-Rock/Art Rock"] = ["progressive rock"],
        ["R&B/Soul"] = ["r&b"],
        ["Rap"] = ["hip hop"],
        ["Romantic Era"] = ["classical", "romantic classical"],
        ["Singer/Songwriter"] = ["singer-songwriter"],
        ["Soundtrack"] = ["soundtracks"],
        ["Standards"] = ["traditional pop"],
        ["Trad Jazz"] = ["dixieland"],
        ["TV Soundtrack"] = ["soundtracks"],
        ["Video Game"] = ["soundtracks"],
        ["Vocal Pop"] = ["traditional pop"],
        ["World"] = ["international"],
    };

    /// <summary>
    /// Qobuz's genre ids, as <c>genre/list</c> names them, to what they state.
    /// An id not here states nothing of its own; the ids above it still may.
    /// </summary>
    public static IReadOnlyDictionary<int, string> QobuzIds { get; } = new Dictionary<int, string>
    {
        [3] = "blues",
        [4] = "country",
        [5] = "folk",
        [10] = "classical",
        [11] = "modern classical",
        [14] = "minimalism",
        [15] = "art song",
        [22] = "concerto",
        [27] = "chamber music",
        [37] = "orchestral",
        [38] = "ballet",
        [40] = "cinematic classical",
        [45] = "cantata",
        [46] = "choral",
        [48] = "oratorio",
        [51] = "cantata",
        [52] = "choral",
        [53] = "choral",
        [54] = "oratorio",
        [55] = "opera",
        [58] = "operetta",
        [64] = "electronic",
        [65] = "ambient",
        [66] = "downtempo",
        [68] = "house",
        [69] = "lounge",
        [70] = "techno",
        [71] = "trance",
        [80] = "jazz",
        [81] = "bebop",
        [82] = "cool jazz",
        [83] = "free jazz",
        [84] = "gospel",
        [85] = "contemporary jazz",
        [86] = "jazz fusion",
        [88] = "dixieland",
        [89] = "vocal jazz",
        [90] = "latin jazz",
        [91] = "soundtracks",
        [93] = "musicals",
        [94] = "international",
        [98] = "brazilian",
        [99] = "celtic",
        [114] = "traditional pop",
        [113] = "alternative rock",
        [115] = "hard rock",
        [116] = "metal",
        [117] = "pop",
        [118] = "punk",
        [119] = "rock",
        [120] = "progressive rock",
        [121] = "rockabilly",
        [122] = "pop",
        [123] = "reggae",
        [126] = "ska",
        [127] = "r&b",
        [128] = "acid jazz",
        [130] = "disco",
        [131] = "funk",
        [133] = "hip hop",
        [134] = "soul",
        [141] = "new age",
        [145] = "dixieland",
        [146] = "ragtime",
        [149] = "latin",
        [157] = "indie pop",
        [167] = "children's music",
    };

    /// <summary>What Apple's primary genre for a record states, or nothing.</summary>
    public static IReadOnlyList<string> FromAppleMusic(string? primaryGenre)
    {
        if (string.IsNullOrWhiteSpace(primaryGenre)) return [];

        string[] stated = AppleMusicNames.TryGetValue(primaryGenre.Trim(), out var named)
            ? named
            : [primaryGenre.Trim().ToLowerInvariant()];

        return Folding(stated);
    }

    /// <summary>What a Qobuz album's genre states, from the ids on its path and its own.</summary>
    /// <param name="path">The ids above it and its own, broadest first, as an album carries them.</param>
    public static IReadOnlyList<string> FromQobuz(IEnumerable<int> path) =>
        Folding(path.Select(id => QobuzIds.GetValueOrDefault(id)).OfType<string>());

    /// <summary>The names <see cref="Genres.Of"/> folds into a genre, once each; the rest are no answer.</summary>
    private static string[] Folding(IEnumerable<string> stated) =>
        [.. stated.Where(name => Genres.Of(name) != Genres.Other).Distinct(StringComparer.Ordinal)];
}
