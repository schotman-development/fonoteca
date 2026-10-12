using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <summary>
    /// Ask about every artist again, now that the answer carries their links.
    /// </summary>
    /// <remarks>
    /// No schema change: this clears <c>LookupUtc</c> so every artist goes back
    /// on the artist stage once, which now asks for URL relationships beside
    /// the genres, bands and aliases. Without it the stamp keeps every artist
    /// described before the change from ever gaining a link. About 3,000 gated
    /// requests on this library, an hour and a quarter at 1.5 seconds each.
    ///
    /// <c>Down</c> is empty, for <c>ReaskForArtistPortraits</c>'s reason: the
    /// inverse of "ask again" is not "un-ask".
    /// </remarks>
    public partial class ReaskArtistsForLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(
                """UPDATE "Artists" SET "LookupUtc" = NULL;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
