using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class DiscographyWorklistIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Artists_Unbrowsed",
                table: "Artists");

            migrationBuilder.CreateIndex(
                name: "IX_Artists_Unbrowsed",
                table: "Artists",
                column: "DiscographyLookupUtc",
                filter: "\"Followed\" AND \"Mbid\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Artists_Unbrowsed",
                table: "Artists");

            migrationBuilder.CreateIndex(
                name: "IX_Artists_Unbrowsed",
                table: "Artists",
                column: "Id",
                filter: "\"Followed\" AND \"DiscographyLookupUtc\" IS NULL AND \"Mbid\" IS NOT NULL");
        }
    }
}
