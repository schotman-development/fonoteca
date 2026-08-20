using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersonMadeReleaseDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_ReleasePending",
                table: "MediaFiles");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReleaseDecidedUtc",
                table: "MediaFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_ReleasePending",
                table: "MediaFiles",
                column: "Id",
                filter: "\"RecordingId\" IS NOT NULL AND \"ReleaseLookupUtc\" IS NULL AND \"ReleaseDecidedUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_ReleasePending",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "ReleaseDecidedUtc",
                table: "MediaFiles");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_ReleasePending",
                table: "MediaFiles",
                column: "Id",
                filter: "\"RecordingId\" IS NOT NULL AND \"ReleaseLookupUtc\" IS NULL");
        }
    }
}
