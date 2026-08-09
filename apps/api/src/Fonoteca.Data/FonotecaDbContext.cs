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
            e.HasIndex(x => x.Id, "IX_MediaFiles_AcoustIdPending")
                .HasFilter("\"AcoustIdCheckedUtc\" IS NULL");

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
                .HasFilter("\"AcoustId\" IS NOT NULL AND \"RecordingLookupUtc\" IS NULL");

            // The attribution worklist. Fourth named partial index on Id, and the
            // naming matters here for the same reason it did for the other three.
            e.HasIndex(x => x.Id, "IX_MediaFiles_ReleasePending")
                .HasFilter("\"RecordingId\" IS NOT NULL AND \"ReleaseLookupUtc\" IS NULL");

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
            e.Property(x => x.Type).HasMaxLength(100);
            e.Property(x => x.Disambiguation).HasMaxLength(1000);
            e.HasIndex(x => x.Mbid).IsUnique().HasFilter("\"Mbid\" IS NOT NULL");
            e.HasIndex(x => x.Name).HasMethod("gin").HasOperators("gin_trgm_ops");
            e.HasIndex(x => x.SortName);
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

        base.OnModelCreating(modelBuilder);
    }
}
