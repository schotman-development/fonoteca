using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReaskForFullArticles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Wikipedia was asked for the lead section only, so what is stored
            // is a definition rather than a biography — measured here, 2,178
            // articles averaging 860 characters, and Janine Jansen's is one
            // sentence against 6,700 characters of article below it.
            //
            // <b>Rule 1's corollary, and the whole reason this file exists.</b>
            // Widening a lookup is invisible to everything already stamped:
            // every artist with an MBID carries `BiographyLookupUtc`, so
            // without this the new code would never ask about any of them
            // again and the fix would reach only artists added later.
            //
            // <b>Narrowed to the rows that gain something.</b> A null text is
            // "Wikidata knows no English article about them", which this change
            // does not touch — the sitelink query is the same one. Re-asking
            // those would spend a gated request each on an answer that cannot
            // have moved. Rows with text are exactly the rows holding a lead.
            //
            // Nothing a person wrote is at risk. `PersonEdits.Diff` records
            // which fields a person changed, but stores each one's whole text,
            // not a delta against the provider's; and the read path checks
            // `EditsJson` first and returns the person's text whenever the key
            // is there. So refreshing the provider's half leaves the edit
            // standing over it, and this pass writes only the three
            // `Biography*` columns — never `EditsJson`.
            //
            // Stated consequence, not fixed: an artist whose short lead was
            // lightly edited keeps that short version, and nothing signals that
            // a fuller article has since arrived underneath it.
            migrationBuilder.Sql(
                """
                UPDATE "Artists" SET "BiographyLookupUtc" = NULL
                WHERE "BiographyText" IS NOT NULL;

                UPDATE "ReleaseGroups" SET "ReviewLookupUtc" = NULL
                WHERE "ReviewText" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo. Clearing a stamp is a worklist edit, not a
            // schema change, and the rows it re-queued have been answered again
            // by now — putting the old stamps back is neither possible (they
            // were not kept) nor meaningful.
        }
    }
}
