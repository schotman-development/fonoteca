namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// A movement's tags, read off the work tree: the iTunes shape of WORK,
/// MOVEMENTNAME, MOVEMENT and MOVEMENTTOTAL that Picard writes.
/// </summary>
public static class WorkTags
{
    /// <summary>
    /// "Allegro con brio" from "Symphony no. 5 in C minor, op. 67: I. Allegro con brio".
    /// </summary>
    /// <remarks>
    /// MusicBrainz titles a part with its whole's title, a colon and a roman
    /// numeral; both come off, since the tags carry the work and the number
    /// beside it. A title in any other shape is kept whole.
    /// </remarks>
    public static string MovementName(string title, string parent)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(parent);

        var name = title.StartsWith(parent + ":", StringComparison.Ordinal) ? title[(parent.Length + 1)..].TrimStart() : title;

        var numeral = 0;
        while (numeral < name.Length && "IVXLCDM".Contains(name[numeral], StringComparison.Ordinal)) numeral++;

        if (numeral > 0 && name.AsSpan(numeral).StartsWith(". ", StringComparison.Ordinal)) name = name[(numeral + 2)..].TrimStart();

        return name.Length == 0 ? title : name;
    }
}
