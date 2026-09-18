using System.Text;
using Fonoteca.Domain.Abstractions;

namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// Which name a screen prints, when the artist's own is not in Latin script.
/// </summary>
/// <remarks>
/// MusicBrainz records an artist under the name they use, in the script they
/// use it in — <c>Пётр Ильич Чайковский</c>, <c>内田光子</c>, <c>ጌታቸው፡መኩሪያ</c> — and
/// that is the right thing for it to hold. It is not the right thing for a
/// browse list somebody reads, where a handful of Cyrillic and CJK rows sort
/// past Z and are unsearchable by anyone who knows the artist as Tchaikovsky.
///
/// <b>The answer is already MusicBrainz's own and costs no extra request.</b>
/// Aliases arrive on the artist lookup behind one more <c>Include</c>, and the
/// English primary alias is exactly the transliteration wanted: measured across
/// this library's 22 non-Latin artists, 20 have one and it is right in all 20 —
/// <c>Pyotr Ilyich Tchaikovsky</c>, <c>Dmitri Shostakovich</c>,
/// <c>Mitsuko Uchida</c>. The remaining two carry a Latin alias with no locale
/// at all (<c>Long Yu</c>, <c>Kaori Muraji</c>), which is what the third rung
/// exists for.
///
/// <b>There is deliberately no fourth rung.</b> The obvious one — un-invert
/// <see cref="Artist.SortName"/>, which is Latin for every one of the 22 — was
/// written and removed. It is right on a person and wrong on an ensemble:
/// "Jenkins, Gordon, Orchestra and Choir" un-inverts to "Orchestra and Choir
/// Jenkins, Gordon", and nothing in the sort name says which kind it is. An
/// artist MusicBrainz holds no Latin alias for keeps their own name, which is
/// honest; a guess would be a name nobody has ever used, printed as fact.
/// </remarks>
public static class LatinNames
{
    /// <summary>
    /// Whether every <i>letter</i> in the text is Latin script.
    /// </summary>
    /// <remarks>
    /// Letters only, and that is the whole reason this is a method rather than a
    /// regex somebody writes inline. A first attempt at "is this Latin" over
    /// whole strings flagged <b>81</b> of this library's 3,051 artists; only 22
    /// were non-Latin. The other 59 were ordinary Latin names carrying
    /// typography — <c>Johnny “Guitar” Watson</c> (curly quotes),
    /// <c>T‐Bone Walker</c> and <c>Camille Saint‐Saëns</c> (U+2010 hyphen),
    /// <c>Gordon Jenkins’ Orchestra</c> (U+2019 apostrophe). Rewriting those to
    /// fix the 22 damages 59 names that were already right.
    ///
    /// Skipping non-letters drops all of them, and with them the punctuation
    /// that made titles look non-Latin for the same bad reason: <c>…</c>,
    /// <c>№</c>, <c>♩</c>, and the Roman numeral <c>Ⅱ</c>, which is a
    /// <c>LetterNumber</c> rather than a letter.
    ///
    /// Runes rather than chars, so a name outside the BMP is one test rather
    /// than two halves of a surrogate pair, each of which is a letter in
    /// neither script.
    /// </remarks>
    public static bool IsLatin(string? text)
    {
        if (string.IsNullOrEmpty(text)) return true;

        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetter(rune) && !IsLatinLetter(rune)) return false;
        }

        return true;
    }

    /// <summary>
    /// The Latin name to print for an artist, or null when their own will do.
    /// </summary>
    /// <remarks>
    /// Null for the 3,029 artists whose name is already Latin, which is the
    /// answer that matters: this is stored in a column and read back as
    /// <c>LatinName ?? Name</c>, so a null is "nothing to say" rather than a
    /// gap. It is also what keeps the blast radius at the 22 rows that asked
    /// for it — an English alias exists for plenty of Latin-named artists too,
    /// and substituting those would rename bands nobody asked to rename.
    ///
    /// A non-Latin alias can never win, however well it is labelled: the
    /// Russian primary alias of a Russian artist is <c>primary="true"</c> and
    /// <c>locale="ru"</c>, and a rule reading only the flag would answer
    /// <c>Пётр Чайковский</c>.
    ///
    /// <b>A search hint is not a name.</b> MusicBrainz files misspellings and
    /// punctuation variants under that alias type so its own search box matches
    /// them; on the last rung, which takes any Latin alias, one could otherwise
    /// become the name printed on the page.
    ///
    /// The last rung orders by name so a rerun cannot change its mind — the
    /// same reason <c>RecordingCandidates</c> breaks its ties by id. MusicBrainz
    /// serves aliases in its own order and a display name that moves between
    /// two equally good answers is a catalogue that looks unstable.
    /// </remarks>
    public static string? Of(string name, IReadOnlyList<MusicBrainzAlias>? aliases)
    {
        if (IsLatin(name)) return null;

        var latin = (aliases ?? [])
            .Where(alias =>
                !string.IsNullOrWhiteSpace(alias.Name) && alias.IsName && IsLatin(alias.Name))
            .OrderBy(alias => alias.Name, StringComparer.Ordinal)
            .ToList();

        return latin.FirstOrDefault(alias => alias.IsEnglish && alias.Primary)?.Name
            ?? latin.FirstOrDefault(alias => alias.IsEnglish)?.Name
            ?? latin.FirstOrDefault()?.Name;
    }

    /// <summary>
    /// What a release printed for this artist, when that differs from their name.
    /// </summary>
    /// <remarks>
    /// The rule three writers held a copy of, plus the one clause this file
    /// exists for. A billing line is only worth storing when it says something
    /// the artist row does not, and a name in the artist's own script says
    /// nothing: the six rows in this library reading <c>内田光子</c> are a
    /// Japanese pressing spelling Mitsuko Uchida the way her own row already
    /// does. Stored, they outrank <see cref="Of"/> everywhere a credit line is
    /// built — every reader resolves <c>CreditedAs ?? Name</c> — so the artist
    /// page would say Mitsuko Uchida and the release page beside it would not.
    ///
    /// <paramref name="artistName"/> is nullable because one of the three
    /// writers reaches this holding an <c>ArtistId</c> out of a dictionary and
    /// not the row — it has never been able to apply the "same as their name"
    /// half, and passing null keeps that exactly as it was rather than making
    /// it look like a rule it does not follow.
    /// </remarks>
    public static string? CreditedAs(string creditName, string? artistName) =>
        creditName == artistName || !IsLatin(creditName) ? null : creditName;

    /// <summary>
    /// Basic Latin through the spacing modifiers, plus Latin Extended Additional.
    /// </summary>
    /// <remarks>
    /// Stops at U+02FF rather than U+024F, and the 176 code points between
    /// them are not padding. The <c>ʻokina</c> (U+02BB) and the modifier
    /// apostrophe (U+02BC) are category <c>Lm</c>, so <see cref="Rune.IsLetter"/>
    /// calls them letters — and at the tighter bound <c>Israel Kamakawiwoʻole</c>
    /// read as non-Latin and had an English alias written over a name that was
    /// already right, which is this rule failing in the one direction that does
    /// damage. MusicBrainz spells most Hawaiian artists with U+02BB. Nothing in
    /// this library does today; it was found by testing the boundary rather
    /// than the library.
    ///
    /// Everything skipped over is Latin anyway — IPA Extensions and spacing
    /// modifiers — and Greek, the first script with letters that merely look
    /// Latin, does not start until U+0370. The second range is what keeps
    /// Vietnamese and the heavily-accented transliterations in this library —
    /// <c>Gétatchèw Mèkurya</c>, <c>Ryūichi Sakamoto</c>, <c>Lǐ Bái</c> — on the
    /// Latin side of the line.
    /// </remarks>
    private static bool IsLatinLetter(Rune rune) => rune.Value is
        <= 0x02FF or (>= 0x1E00 and <= 0x1EFF);
}
