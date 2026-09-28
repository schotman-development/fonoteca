using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecheckEveryAlbum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR 0013, once: every pressing already in the catalogue was
            // claimed under the old rule — a mean drift, a stated tie-break, a
            // person's pick — and none of them was proved the way a pressing
            // now has to be. So every edition link goes now, the album each
            // file is under stays, and the pass is handed the folders back.
            //
            // <b>A person's pick keeps its album.</b> Outcomes 7 and 10 become
            // 15 and 16: the album somebody chose is standing intent the rule
            // may not overrule, and only which of its editions the files are is
            // asked again. `ReleaseDecidedUtc` stays, which is what keeps them
            // theirs.
            //
            // <b>A rule's claim is dropped to its album</b> (1 and 2 become 3,
            // "album known, pressing not"), because that is all it proved, and
            // re-asked. Its refusals (3 to 6) are re-asked too: a folder refused
            // for want of one release may well be one album. A person's "no
            // album" and "not from a release" (8, 9, 11, 12) are not edition
            // claims and are left exactly as they are.
            //
            // <b>The tag-write journal is deleted.</b> Its entries are the undo
            // record of tags written from the pressings this clears, and the
            // owner chose to leave those tags on the files and drop the record
            // (exported beforehand to ~/fonoteca-tag-journal-backup.csv).
            // Nothing in the application reads them.
            migrationBuilder.Sql(
                """
                UPDATE "MediaFiles" SET "AttributionOutcome" = 15, "ReleaseId" = NULL, "TrackId" = NULL,
                    "EditionAlternatives" = 0, "ReleaseLookupUtc" = NULL
                WHERE "AttributionOutcome" = 7;

                UPDATE "MediaFiles" SET "AttributionOutcome" = 16, "ReleaseId" = NULL, "TrackId" = NULL,
                    "EditionAlternatives" = 0, "ReleaseLookupUtc" = NULL
                WHERE "AttributionOutcome" = 10;

                UPDATE "MediaFiles" SET "AttributionOutcome" = 3, "ReleaseId" = NULL, "TrackId" = NULL,
                    "EditionAlternatives" = 0, "ReleaseLookupUtc" = NULL
                WHERE "ReleaseDecidedUtc" IS NULL AND "AttributionOutcome" IN (1, 2);

                UPDATE "MediaFiles" SET "ReleaseLookupUtc" = NULL
                WHERE "ReleaseDecidedUtc" IS NULL AND "AttributionOutcome" IN (3, 4, 5, 6);

                DELETE FROM "DomainEvents" WHERE "Type" = 'tagging.catalogue.written';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the links it cleared were not kept, and the
            // journal it deleted is in the export, not here.
        }
    }
}
