using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <summary>
    /// Ask about every artist again, now that their aliases are kept.
    /// </summary>
    /// <remarks>
    /// No schema change: this clears <c>LookupUtc</c> on every artist the
    /// artist stage can ask about, so each gains <c>AliasesJson</c> once.
    /// Without it the stamp keeps every artist described before the column
    /// from ever gaining one, and a download billing an artist by an alias
    /// mints them again. About 3,200 gated requests on this library.
    ///
    /// <c>Down</c> is empty, for <c>ReaskForArtistPortraits</c>'s reason: the
    /// inverse of "ask again" is not "un-ask".
    /// </remarks>
    public partial class ReaskArtistsForAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(
                """UPDATE "Artists" SET "LookupUtc" = NULL WHERE "Mbid" IS NOT NULL;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
