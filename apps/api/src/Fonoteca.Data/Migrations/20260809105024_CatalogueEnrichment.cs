using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class CatalogueEnrichment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EnrichmentOutcome",
                table: "MediaFiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RecordingLookupUtc",
                table: "MediaFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_RecordingPending",
                table: "MediaFiles",
                column: "Id",
                filter: "\"AcoustId\" IS NOT NULL AND \"RecordingLookupUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_RecordingPending",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "EnrichmentOutcome",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "RecordingLookupUtc",
                table: "MediaFiles");
        }
    }
}
