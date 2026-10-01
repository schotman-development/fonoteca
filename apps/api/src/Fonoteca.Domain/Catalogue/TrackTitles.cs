namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// The track titles MusicBrainz uses for a position that holds no song.
/// </summary>
/// <remarks>
/// MusicBrainz's style guide gives these exact titles to placeholder tracks:
/// the run of short silent tracks before a hidden track, and the data session
/// on an enhanced CD. They are positions on the disc, not music anybody is
/// missing, so wherever a track list is held up against a folder they do not
/// count — Marc Broussard's <i>Carencro</i> prints eleven <c>[silence]</c>
/// tracks before track 23, and counted they made a whole rip read as 12 of 23.
///
/// <b>Unless a file holds one.</b> A rip that kept the silent tracks as files
/// has a file on every position, and leaving those positions out would leave
/// its files over and fail the very pressing it is. So everywhere below, a
/// placeholder counts exactly when the folder has a file of it.
///
/// Matched exactly, because MusicBrainz writes them exactly and a song really
/// called "Silence" is a song. The Identify screen has a twin in
/// <c>apps/web/src/pages/identify.ts</c>; change both.
/// </remarks>
public static class TrackTitles
{
    public const string Silence = "[silence]";

    public const string DataTrack = "[data track]";

    public static bool IsPlaceholder(string? title) => title is Silence or DataTrack;
}
