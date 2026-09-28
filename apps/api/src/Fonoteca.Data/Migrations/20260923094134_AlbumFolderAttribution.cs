using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlbumFolderAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_ReleasePending",
                table: "MediaFiles");

            migrationBuilder.AddColumn<int>(
                name: "FolderPosition",
                table: "MediaFiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OrderOutcome",
                table: "MediaFiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TagDiscNumber",
                table: "MediaFiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TagTrackNumber",
                table: "MediaFiles",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_ReleasePending",
                table: "MediaFiles",
                column: "Id",
                filter: "\"RecordingId\" IS NOT NULL AND \"ReleaseLookupUtc\" IS NULL AND (\"ReleaseDecidedUtc\" IS NULL OR \"AttributionOutcome\" IN (15, 16))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_ReleasePending",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "FolderPosition",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "OrderOutcome",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "TagDiscNumber",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "TagTrackNumber",
                table: "MediaFiles");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_ReleasePending",
                table: "MediaFiles",
                column: "Id",
                filter: "\"RecordingId\" IS NOT NULL AND \"ReleaseLookupUtc\" IS NULL AND \"ReleaseDecidedUtc\" IS NULL");
        }
    }
}
