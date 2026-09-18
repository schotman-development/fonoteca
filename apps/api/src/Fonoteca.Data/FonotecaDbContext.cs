using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Data;

/// <summary>
/// The catalogue.
/// </summary>
/// <remarks>
/// Sized for 100k+ files from the start, which shows up in three places:
/// time-ordered v7 primary keys so inserts stay at the right edge of the index,
/// <c>pg_trgm</c> indexes for fuzzy title matching, and covering indexes on the
/// columns the scanner touches on every pass.
/// </remarks>
public sealed class FonotecaDbContext(DbContextOptions<FonotecaDbContext> options)
    : DbContext(options)
{
    public DbSet<Work> Works => Set<Work>();
    public DbSet<Recording> Recordings => Set<Recording>();
    public DbSet<ReleaseGroup> ReleaseGroups => Set<ReleaseGroup>();
    public DbSet<Release> Releases => Set<Release>();
    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();
    public DbSet<Artist> Artists => Set<Artist>();
    public DbSet<ArtistCredit> ArtistCredits => Set<ArtistCredit>();
    public DbSet<Relationship> Relationships => Set<Relationship>();
    public DbSet<DomainEvent> DomainEvents => Set<DomainEvent>();

    public DbSet<ReleaseCandidateSet> ReleaseCandidateSets => Set<ReleaseCandidateSet>();
    public DbSet<ReleaseCover> ReleaseCovers => Set<ReleaseCover>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<WorkId>().HaveConversion<WorkIdConverter>();
        configurationBuilder.Properties<RecordingId>().HaveConversion<RecordingIdConverter>();
        configurationBuilder.Properties<ReleaseGroupId>().HaveConversion<ReleaseGroupIdConverter>();
        configurationBuilder.Properties<ReleaseId>().HaveConversion<ReleaseIdConverter>();
        configurationBuilder.Properties<TrackId>().HaveConversion<TrackIdConverter>();
        configurationBuilder.Properties<MediaFileId>().HaveConversion<MediaFileIdConverter>();
        configurationBuilder.Properties<ArtistId>().HaveConversion<ArtistIdConverter>();
        configurationBuilder.Properties<Mbid>().HaveConversion<MbidConverter>();
        configurationBuilder.Properties<AcoustId>().HaveConversion<AcoustIdConverter>();

        base.ConfigureConventions(configurationBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Trigram matching. Titles arrive misspelled, differently punctuated and
        // differently transliterated; exact equality finds almost nothing, and
        // LIKE '%x%' cannot use a B-tree. pg_trgm is what makes fuzzy title
        // lookup usable at this row count.
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.Entity<Work>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Type).HasMaxLength(100);
            e.HasIndex(x => x.Mbid).IsUnique().HasFilter("\"Mbid\" IS NOT NULL");
            e.HasIndex(x => x.Title).HasMethod("gin").HasOperators("gin_trgm_ops");
        });

        modelBuilder.Entity<Recording>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(1000).IsRequired();
            e.HasIndex(x => x.Mbid).IsUnique().HasFilter("\"Mbid\" IS NOT NULL");
            e.HasIndex(x => x.Title).HasMethod("gin").HasOperators("gin_trgm_ops");
            e.HasOne(x => x.Work)
                .WithMany(w => w.Recordings)
                .HasForeignKey(x => x.WorkId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ReleaseGroup>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(1000).IsRequired();
            e.Property(x => x.PrimaryType).HasMaxLength(100);
            e.Property(x => x.SecondaryTypes).HasMaxLength(500);
            e.HasIndex(x => x.Mbid).IsUnique().HasFilter("\"Mbid\" IS NOT NULL");
            e.HasIndex(x => x.Title).HasMethod("gin").HasOperators("gin_trgm_ops");
        });

        modelBuilder.Entity<Release>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Country).HasMaxLength(10);
            e.Property(x => x.Label).HasMaxLength(500);
            e.Property(x => x.CatalogNumber).HasMaxLength(200);
            e.Property(x => x.Barcode).HasMaxLength(50);
            e.Property(x => x.Status).HasMaxLength(50);
            e.Property(x => x.Disambiguation).HasMaxLength(1000);
            e.Property(x => x.MediumFormats).HasMaxLength(200);
            e.HasIndex(x => x.Mbid).IsUnique().HasFilter("\"Mbid\" IS NOT NULL");
            e.HasIndex(x => x.Barcode);
            e.HasIndex(x => x.Title).HasMethod("gin").HasOperators("gin_trgm_ops");
            e.HasOne(x => x.ReleaseGroup)
                .WithMany(g => g.Releases)
                .HasForeignKey(x => x.ReleaseGroupId)
                .OnDelete(DeleteBehavior.SetNull);

            // Three columns rather than a date, because MusicBrainz dates plenty
            // of releases to a year alone and a `date` can hold none of them —
            // the old DateOnly column silently discarded every year-only release,
            // which in this library is about half of them. `Released` is the
            // view over the three and belongs to the domain, not the schema.
            e.Ignore(x => x.Released);
            e.HasIndex(x => x.ReleasedYear);
        });

        modelBuilder.Entity<Track>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(1000);
            e.Property(x => x.Number).HasMaxLength(50);
            e.HasOne(x => x.Release)
                .WithMany(r => r.Tracks)
                .HasForeignKey(x => x.ReleaseId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Recording)
                .WithMany(r => r.Tracks)
                .HasForeignKey(x => x.RecordingId)
                .OnDelete(DeleteBehavior.Cascade);

            // A release cannot have two tracks in the same slot.
            e.HasIndex(x => new { x.ReleaseId, x.DiscNumber, x.Position }).IsUnique();
        });

        modelBuilder.Entity<MediaFile>(e =>
        {
            e.HasKey(x => x.Id);

            // One row per file on disk, enforced by the database rather than by
            // the scanner remembering to check.
            e.Property(x => x.Path).HasMaxLength(4096).IsRequired();
            e.HasIndex(x => x.Path).IsUnique();

            e.Property(x => x.ContentHash).HasMaxLength(128);
            e.Property(x => x.AudioHash).HasMaxLength(128);

            // JSONB rather than text, for the reason DomainEvents.PayloadJson is:
            // the column is opaque to the application's queries today, and being
            // able to ask a question of it later — "which files did AcoustID name
            // this recording for" — should not need a migration to become
            // possible. It also compresses, which matters at a kilobyte a row.
            e.Property(x => x.AcoustIdMatchesJson).HasColumnType("jsonb");
            e.Property(x => x.RecordingCandidatesJson).HasColumnType("jsonb");

            // Dedupe reads these constantly, so both are indexed. AudioHash is
            // the exact-duplicate path (same decoded audio, different tags);
            // Fingerprint is the cross-encoding path.
            e.HasIndex(x => x.AudioHash).HasFilter("\"AudioHash\" IS NOT NULL");
            e.HasIndex(x => x.ContentHash).HasFilter("\"ContentHash\" IS NOT NULL");

            e.HasIndex(x => x.RecordingId);

            // The rescan predicate: "files not looked at since <time>".
            e.HasIndex(x => x.LastScannedUtc);
            e.HasIndex(x => x.Integrity);

            // The identification worklist, and it is partial on purpose. The
            // predicate is the entire selectivity, and — unlike every other index
            // here — this one is meant to shrink to nothing: once the library has
            // been identified, "what is left" is the empty set, and a full index
            // over a column that is non-null for 99% of 100,000 rows would be
            // 99% dead weight rewritten on every pass.
            //
            // Note the two-argument HasIndex: it declares a *named* index.
            // `HasIndex(x => x.Id).HasDatabaseName(...)` twice does not produce
            // two indexes — EF keys them by property list, so the second call
            // returns the first builder and quietly renames it and replaces its
            // filter. The worklist index then never reaches the migration, and
            // nothing says so.
            // The second half of the filter is the person guard. Clearing
            // AcoustIdCheckedUtc by hand is the documented way to re-ask a whole
            // library after a rule change, and without this that same UPDATE
            // would sweep up every file somebody had already answered and hand
            // it back to the rule that could not answer it.
            e.HasIndex(x => x.Id, "IX_MediaFiles_AcoustIdPending")
                .HasFilter("\"AcoustIdCheckedUtc\" IS NULL AND \"IdentityDecidedUtc\" IS NULL");

            // Identified, but the file itself does not say so yet — exactly what
            // a run with Fonoteca:AllowFileMutation off leaves behind. The run
            // after the flag is flipped finds its work through this, and so costs
            // no lookups at all.
            e.HasIndex(x => x.Id, "IX_MediaFiles_AcoustIdUntagged")
                .HasFilter("\"AcoustId\" IS NOT NULL AND \"AcoustIdTaggedUtc\" IS NULL");

            // Not unique, and that is the point: two encodings of one track
            // legitimately share a cluster. That is the dedupe signal, not a
            // conflict — it is the cross-encoding half Fingerprint hints at and
            // this answers exactly.
            e.HasIndex(x => x.AcoustId).HasFilter("\"AcoustId\" IS NOT NULL");

            // The enrichment worklist: identified, but nobody has asked yet what
            // recording that identity names. Partial and named for the same two
            // reasons as the pair above — it is meant to shrink to nothing, and a
            // third unnamed HasIndex on Id would silently replace one of them.
            e.HasIndex(x => x.Id, "IX_MediaFiles_RecordingPending")
                .HasFilter(
                    "\"AcoustId\" IS NOT NULL AND \"RecordingLookupUtc\" IS NULL "
                    + "AND \"IdentityDecidedUtc\" IS NULL");

            // The attribution worklist. Fourth named partial index on Id, and the
            // naming matters here for the same reason it did for the other three.
            e.HasIndex(x => x.Id, "IX_MediaFiles_ReleasePending")
                .HasFilter(
                    "\"RecordingId\" IS NOT NULL AND \"ReleaseLookupUtc\" IS NULL "
                    + "AND \"ReleaseDecidedUtc\" IS NULL");

            // "Everything on this album" has to be one indexed read whether or
            // not the pressing was decided, so both links are indexed.
            e.HasIndex(x => x.ReleaseId);
            e.HasIndex(x => x.ReleaseGroupId);

            e.HasOne(x => x.Recording)
                .WithMany(r => r.Files)
                .HasForeignKey(x => x.RecordingId)
                .OnDelete(DeleteBehavior.SetNull);

            // SetNull throughout: losing a release must not take the files with
            // it. They are still on disk, still identified, and still worth
            // re-attributing — a cascade here would delete somebody's library
            // because a MusicBrainz edit merged two pressings.
            e.HasOne(x => x.Release)
                .WithMany(r => r.Files)
                .HasForeignKey(x => x.ReleaseId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(x => x.ReleaseGroup)
                .WithMany()
                .HasForeignKey(x => x.ReleaseGroupId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(x => x.Track)
                .WithMany()
                .HasForeignKey(x => x.TrackId)
                .OnDelete(DeleteBehavior.SetNull);

            // Quality travels with the file and is never queried independently,
            // so it maps to columns on the same row rather than a join.
            e.ComplexProperty(x => x.Quality, q =>
            {
                q.IsRequired(false);
                q.Property(p => p.Codec).HasMaxLength(50);
            });
        });

        modelBuilder.Entity<Artist>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(1000).IsRequired();
            e.Property(x => x.SortName).HasMaxLength(1000);
            e.Property(x => x.LatinName).HasMaxLength(1000);
            e.Property(x => x.Type).HasMaxLength(100);
            e.Property(x => x.Disambiguation).HasMaxLength(1000);
            e.Property(x => x.Country).HasMaxLength(10);
            e.Property(x => x.Gender).HasMaxLength(100);
            e.Property(x => x.Genres).HasMaxLength(1000);

            // A Commons filename is capped at 240 bytes and arrives
            // percent-encoded, so three times that plus the Special:FilePath
            // prefix is the real bound. The provider drops anything longer
            // rather than let a URL nobody will ever click roll back the stamp
            // that stops it being asked for again — Genres' lesson.
            e.Property(x => x.PortraitUrl).HasMaxLength(1000);
            e.HasIndex(x => x.Mbid).IsUnique().HasFilter("\"Mbid\" IS NOT NULL");
            e.HasIndex(x => x.Name).HasMethod("gin").HasOperators("gin_trgm_ops");

            // The artist filter searches this beside Name, so it wants the same
            // index. Not partial, although it is null for 3,029 of 3,051 rows:
            // a trigram GIN index stores nothing for a null anyway, and the
            // filter would have to be repeated in the query verbatim for
            // PostgreSQL to use it.
            e.HasIndex(x => x.LatinName).HasMethod("gin").HasOperators("gin_trgm_ops");
            e.HasIndex(x => x.SortName);

            // The enrichment pass's third worklist: artists nobody has asked
            // MusicBrainz about. Partial, because it is only ever queried for
            // the nulls and the whole point is that it empties — a full index
            // would grow to every artist in the library to answer a question
            // that ends up returning nothing.
            // Both of these are partial indexes on the same column, and the
            // *named* overload is what makes them two indexes rather than one.
            // `HasIndex(x => x.Id)` returns the builder for the index on that
            // property set — call it twice and the second does not add
            // anything, it reconfigures the first. The migration generated
            // against the unnamed form opened with
            // `DropIndex("IX_Artists_Unasked")` and never recreated it: the
            // artist worklist would have quietly lost its index while the new
            // one looked like it had been added.
            e.HasIndex(x => x.Id, "IX_Artists_Unasked")
                .HasFilter("\"LookupUtc\" IS NULL AND \"Mbid\" IS NOT NULL");

            // The enrichment pass's fifth worklist: artists whose discography
            // has never been fetched, plus followed ones whose fetch has gone
            // stale.
            //
            // <b>`"Followed"` came out of the filter when the worklist widened
            // to the whole catalogue, and leaving it in would have been the
            // dead-index bug below a second time.</b> The predicate is now
            // satisfied by an unfollowed artist with a null stamp — precisely a
            // row the old filter excluded — so the index could not have served
            // the query it is named for. What stays in the filter is the one
            // clause still conjunct across both arms of the OR.
            //
            // <b>It pays nothing until the backlog is browsed, and that is the
            // state it is for.</b> While most of the catalogue is unstamped the
            // predicate matches almost every row, so PostgreSQL rightly seq
            // scans and this index is not chosen — measured on the real table at
            // 2,977 of 3,004 unstamped. Modelled at the steady state instead —
            // 27 unstamped, which is what the table looks like once the pass has
            // run — the planner picks it unforced: a `BitmapOr` of two `Bitmap
            // Index Scan`s with `Index Cond: ("DiscographyLookupUtc" IS NULL)`,
            // the indexed column a genuine search key. So the index is dormant
            // rather than dead, and the run that makes it worth having is the
            // one that fills the column.
            //
            // The filter excludes nothing today, every artist here having an
            // MBID. It is kept because it stays *true* of the worklist, costs
            // nothing, and an artist minted from a credit line with no MBID is
            // ordinary — the day one exists this index already declines it.
            //
            // <b>The stamp is the indexed column and only the conjuncts are in
            // the filter, and that is not cosmetic.</b> This worklist stopped
            // being "never asked" when monitoring arrived and became "never
            // asked OR asked too long ago" — and a partial index whose filter
            // says `"DiscographyLookupUtc" IS NULL` cannot serve that query at
            // all, because the OR admits exactly the rows the filter excludes.
            // Measured against the real database with `enable_seqscan = off`,
            // PostgreSQL refused to consider the old index even when forced and
            // fell back to a bitmap scan over `IX_Artists_Mbid` — an index whose
            // own comment claimed to serve a worklist it could not. Filtering on
            // what stayed conjunct and indexing what moved into the OR is what
            // makes it usable again.
            e.HasIndex(x => x.DiscographyLookupUtc, "IX_Artists_Unbrowsed")
                .HasFilter("\"Mbid\" IS NOT NULL");
        });

        modelBuilder.Entity<ArtistCredit>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.JoinPhrase).HasMaxLength(100);
            e.Property(x => x.CreditedAs).HasMaxLength(1000);
            e.HasOne(x => x.Artist)
                .WithMany(a => a.Credits)
                .HasForeignKey(x => x.ArtistId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.RecordingId);
            e.HasIndex(x => x.ReleaseId);
            e.HasIndex(x => x.ReleaseGroupId);
        });

        modelBuilder.Entity<Relationship>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SourceType).HasMaxLength(50).IsRequired();
            e.Property(x => x.TargetType).HasMaxLength(50).IsRequired();
            e.Property(x => x.Type).HasMaxLength(100).IsRequired();
            e.Property(x => x.Attribute).HasMaxLength(200);

            // "everything this engineer touched" walks this index.
            e.HasIndex(x => new { x.TargetType, x.TargetId, x.Type });
            e.HasIndex(x => new { x.SourceType, x.SourceId, x.Type });

            // The typed end of the same links, which is what a browse query can
            // actually join on. Cascade: a relationship describes a link between
            // two entities and means nothing once either is gone.
            e.HasOne<Artist>()
                .WithMany(a => a.Relationships)
                .HasForeignKey(x => x.ArtistId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.ArtistId);
        });

        modelBuilder.Entity<DomainEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(200).IsRequired();
            e.Property(x => x.SubjectType).HasMaxLength(50).IsRequired();
            e.Property(x => x.SubjectId).HasMaxLength(200).IsRequired();
            e.Property(x => x.ActorId).HasMaxLength(200).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(200);

            // JSONB, so a new event type never needs a migration and the payload
            // stays queryable.
            e.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();

            // Reading one subject's history — how the undo journal works.
            e.HasIndex(x => new { x.SubjectType, x.SubjectId, x.OccurredAtUtc });

            // Reversing a whole batch as a unit.
            e.HasIndex(x => x.CorrelationId).HasFilter("\"CorrelationId\" IS NOT NULL");
            e.HasIndex(x => x.OccurredAtUtc);
        });

        modelBuilder.Entity<ReleaseCandidateSet>(e =>
        {
            // The component's own stamp is the key. No surrogate: there is
            // exactly one candidate set per component, the component has no row
            // of its own anywhere, and a generated id would only add a second
            // way to name the same thing.
            e.HasKey(x => x.ComponentUtc);

            e.Property(x => x.DocumentJson).HasColumnType("jsonb").IsRequired();

            // No foreign key to MediaFiles, deliberately. The component is not a
            // row, and the files that make it up leave it — a scan clears their
            // stamp, a person answers half of it — so there is nothing stable to
            // point at. `Files` is what catches that instead; see the entity.
            e.HasIndex(x => x.GatheredUtc);
        });

        modelBuilder.Entity<ReleaseCover>(e =>
        {
            e.HasKey(x => x.ReleaseId);
            e.Property(x => x.MediaType).HasMaxLength(100);
            e.Property(x => x.QobuzAlbumId).HasMaxLength(64);
            e.HasOne<Release>()
                .WithOne()
                .HasForeignKey<ReleaseCover>(x => x.ReleaseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        base.OnModelCreating(modelBuilder);
    }
}
