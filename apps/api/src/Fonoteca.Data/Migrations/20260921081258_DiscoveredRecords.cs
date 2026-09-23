using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class DiscoveredRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DiscoveredRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtistId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: true),
                    Barcode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CoverUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    TrackCount = table.Column<int>(type: "integer", nullable: true),
                    Monitored = table.Column<bool>(type: "boolean", nullable: false),
                    FoundUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SeenUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveredRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveredRecords_Artists_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "Artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredRecords_ArtistId_Source_SourceId",
                table: "DiscoveredRecords",
                columns: new[] { "ArtistId", "Source", "SourceId" },
                unique: true);

            // The release groups the old discovery path minted, and the credits
            // that put them on an artist's discography.
            //
            // <b>Not carried across.</b> Those rows were written without the
            // shop's own id, so there is no key to write them under here and no
            // way to recognise one again — which is the fault this table exists
            // to fix, and it cannot be repaired retroactively. The pass re-asks
            // each shop and writes them properly.
            //
            // <b>Nothing a person answered is lost, and that is enforced rather
            // than asserted.</b> `Monitored` is one of the facts in this
            // catalogue nothing can recompute. It was measured before this was
            // written — 38,487 rows match and none of them is monitored — but a
            // measurement taken today is not a constraint: somebody can mark one
            // on the artist page between now and this running, and the row would
            // go silently. So `NOT "Monitored"` is in the predicate. A monitored
            // orphan survives as a stranded row rather than as a deleted
            // intention, which is the right way round for the one column here
            // that cannot be recovered from anything.
            //
            // <b>The predicate is exact, and that was measured too.</b> Every
            // one of those 38,487 carries the discovery signature whole — no
            // MBID, no type, no secondary types, one credit at position 0 — and
            // they are *all* of the null-MBID groups in the database. The
            // attribution pass can also mint a group with no MBID, which is why
            // the releases and files clauses are load-bearing rather than tidy:
            // such a group has one or the other, and none of these has either.
            //
            // <b>The stamps go first and only for the artists this touches.</b>
            // An artist whose shop rows are removed here has been asked and no
            // longer has the answer, so leaving the stamp would be the browse
            // saying "we asked" about rows that no longer exist — rule 1's
            // corollary, and the whole point of this change is that an earlier
            // pass must not decide what a later one may learn. Narrowed to the
            // artists that actually lose something, because clearing every stamp
            // would spend a gated MusicBrainz request on thousands of artists
            // whose catalogue half is perfectly good.
            migrationBuilder.Sql(
                """
                UPDATE "Artists" a SET "DiscographyLookupUtc" = NULL
                WHERE EXISTS (
                    SELECT 1
                    FROM "ArtistCredits" c
                    JOIN "ReleaseGroups" g ON g."Id" = c."ReleaseGroupId"
                    WHERE c."ArtistId" = a."Id"
                      AND g."Mbid" IS NULL
                      AND NOT g."Monitored"
                      AND NOT EXISTS (
                          SELECT 1 FROM "Releases" r WHERE r."ReleaseGroupId" = g."Id")
                      AND NOT EXISTS (
                          SELECT 1 FROM "MediaFiles" m WHERE m."ReleaseGroupId" = g."Id"));

                DELETE FROM "ArtistCredits" c
                USING "ReleaseGroups" g
                WHERE c."ReleaseGroupId" = g."Id"
                  AND g."Mbid" IS NULL
                  AND NOT g."Monitored"
                  AND NOT EXISTS (
                      SELECT 1 FROM "Releases" r WHERE r."ReleaseGroupId" = g."Id")
                  AND NOT EXISTS (
                      SELECT 1 FROM "MediaFiles" m WHERE m."ReleaseGroupId" = g."Id");

                DELETE FROM "ReleaseGroups" g
                WHERE g."Mbid" IS NULL
                  AND NOT g."Monitored"
                  AND NOT EXISTS (
                      SELECT 1 FROM "Releases" r WHERE r."ReleaseGroupId" = g."Id")
                  AND NOT EXISTS (
                      SELECT 1 FROM "MediaFiles" m WHERE m."ReleaseGroupId" = g."Id");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscoveredRecords");
        }
    }
}
