using Fonoteca.Data;
using Fonoteca.Domain.Acquisition;
using Fonoteca.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Library;

/// <summary>
/// The artist a printed name stands for: linked on a unique match, else
/// minted once and reused for the rest of the run.
/// </summary>
/// <remarks>
/// <b>Linked only where exactly one answers to the name</b> — the owner's
/// choice: this library holds two Sonny Boy Williamsons. The first tier anyone
/// answers in decides: MusicBrainz's artists by name, then by alias, then one
/// minted earlier with no MBID. MusicBrainz before a minted row, or the "Royal
/// Concertgebouw Orchestra" one download minted would go on taking every later
/// download's credits from Koninklijk Concertgebouworkest.
///
/// <b>An MBID a file's own tag states goes before any name</b>, since it
/// cannot be wrong about who; it is believed only where the catalogue holds
/// that artist, and a name is matched otherwise.
/// </remarks>
internal sealed class ArtistsByName
{
    private readonly FonotecaDbContext _db;
    private readonly IReadOnlyList<Dictionary<string, List<Artist>>> _tiers;
    private readonly Dictionary<Mbid, Artist> _byMbid;
    private readonly Dictionary<string, Artist> _minted = new(StringComparer.Ordinal);

    private ArtistsByName(
        FonotecaDbContext db,
        IReadOnlyList<Dictionary<string, List<Artist>>> tiers,
        Dictionary<Mbid, Artist> byMbid)
    {
        _db = db;
        _tiers = tiers;
        _byMbid = byMbid;
    }

    /// <summary>Every artist by normalised name, by MusicBrainz alias and by MBID.</summary>
    public static async Task<ArtistsByName> LoadAsync(FonotecaDbContext db, CancellationToken cancellationToken)
    {
        var known = (await db.Artists
                .AsNoTracking()
                .Select(artist => new { artist.Id, artist.Name, artist.LatinName, artist.Type, artist.Mbid, artist.AliasesJson })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(artist => (
                Artist: new Artist { Id = artist.Id, Name = artist.Name, Type = artist.Type },
                artist.Mbid,
                Names: new[] { artist.Name, artist.LatinName }.OfType<string>(),
                Aliases: ArtistAliases.Read(artist.AliasesJson).Where(alias => alias.IsName).Select(alias => alias.Name)))
            .ToList();

        var identified = known.Where(artist => artist.Mbid is not null).ToList();

        return new ArtistsByName(
            db,
            [
                ByName(identified.Select(artist => (artist.Artist, artist.Names))),
                ByName(identified.Select(artist => (artist.Artist, artist.Aliases))),
                ByName(known.Where(artist => artist.Mbid is null).Select(artist => (artist.Artist, artist.Names))),
            ],
            identified
                .DistinctBy(artist => artist.Mbid)
                .ToDictionary(artist => artist.Mbid!.Value, artist => artist.Artist));
    }

    public Artist For(ProviderCredit credit, Mbid? stated = null)
    {
        ArgumentNullException.ThrowIfNull(credit);

        if (stated is { } mbid && _byMbid.TryGetValue(mbid, out var identified)) return identified;

        var name = ArtistNameMatch.Normalise(credit.Name);

        if (_minted.TryGetValue(name, out var mine)) return mine;

        if (name.Length > 0
            && _tiers.Select(tier => tier.GetValueOrDefault(name)).FirstOrDefault(found => found is not null) is [var only])
        {
            return only;
        }

        var artist = new Artist
        {
            Id = ArtistId.New(),
            Name = credit.Name,
            Type = credit.Role == CreditRole.Ensemble ? EnsembleType(credit.Name) : null,
        };

        _db.Artists.Add(artist);
        _minted[name] = artist;

        return artist;
    }

    private static Dictionary<string, List<Artist>> ByName(IEnumerable<(Artist Artist, IEnumerable<string> Names)> artists) =>
        artists
            .SelectMany(artist => artist.Names
                .Select(ArtistNameMatch.Normalise)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Select(name => (Name: name, artist.Artist)))
            .GroupBy(pair => pair.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Artist).ToList(), StringComparer.Ordinal);

    private static string EnsembleType(string name) =>
        name.Contains("choir", StringComparison.OrdinalIgnoreCase) || name.Contains("chor", StringComparison.OrdinalIgnoreCase)
            ? "Choir"
            : "Orchestra";
}
