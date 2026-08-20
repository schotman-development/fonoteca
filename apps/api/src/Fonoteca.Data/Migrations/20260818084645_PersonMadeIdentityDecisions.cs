using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersonMadeIdentityDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_AcoustIdPending",
                table: "MediaFiles");

            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_RecordingPending",
                table: "MediaFiles");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "IdentityDecidedUtc",
                table: "MediaFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_AcoustIdPending",
                table: "MediaFiles",
                column: "Id",
                filter: "\"AcoustIdCheckedUtc\" IS NULL AND \"IdentityDecidedUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_RecordingPending",
                table: "MediaFiles",
                column: "Id",
                filter: "\"AcoustId\" IS NOT NULL AND \"RecordingLookupUtc\" IS NULL AND \"IdentityDecidedUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_AcoustIdPending",
                table: "MediaFiles");

            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_RecordingPending",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "IdentityDecidedUtc",
                table: "MediaFiles");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_AcoustIdPending",
                table: "MediaFiles",
                column: "Id",
                filter: "\"AcoustIdCheckedUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_RecordingPending",
                table: "MediaFiles",
                column: "Id",
                filter: "\"AcoustId\" IS NOT NULL AND \"RecordingLookupUtc\" IS NULL");
        }
    }
}
