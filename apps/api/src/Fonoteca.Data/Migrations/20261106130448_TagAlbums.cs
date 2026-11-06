using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class TagAlbums : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TagFolder",
                table: "ReleaseGroups",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseGroups_TagFolder",
                table: "ReleaseGroups",
                column: "TagFolder",
                unique: true,
                filter: "\"TagFolder\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReleaseGroups_TagFolder",
                table: "ReleaseGroups");

            migrationBuilder.DropColumn(
                name: "TagFolder",
                table: "ReleaseGroups");
        }
    }
}
