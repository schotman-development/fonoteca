using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class StoredAcoustIdEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AcoustIdMatchesJson",
                table: "MediaFiles",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AcoustIdMatchesUtc",
                table: "MediaFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecordingCandidatesJson",
                table: "MediaFiles",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RecordingCandidatesUtc",
                table: "MediaFiles",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcoustIdMatchesJson",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "AcoustIdMatchesUtc",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "RecordingCandidatesJson",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "RecordingCandidatesUtc",
                table: "MediaFiles");
        }
    }
}
