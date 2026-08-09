using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReleasedOn",
                table: "Releases");

            migrationBuilder.AddColumn<TimeSpan>(
                name: "Length",
                table: "Tracks",
                type: "interval",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Number",
                table: "Tracks",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Disambiguation",
                table: "Releases",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DiscCount",
                table: "Releases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediumFormats",
                table: "Releases",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReleasedDay",
                table: "Releases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReleasedMonth",
                table: "Releases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReleasedYear",
                table: "Releases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Releases",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecondaryTypes",
                table: "ReleaseGroups",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttributionOutcome",
                table: "MediaFiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EditionAlternatives",
                table: "MediaFiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ReleaseGroupId",
                table: "MediaFiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReleaseId",
                table: "MediaFiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReleaseLookupUtc",
                table: "MediaFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TrackId",
                table: "MediaFiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Releases_ReleasedYear",
                table: "Releases",
                column: "ReleasedYear");

            migrationBuilder.CreateIndex(
                name: "IX_Releases_Title",
                table: "Releases",
                column: "Title")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_ReleaseGroupId",
                table: "MediaFiles",
                column: "ReleaseGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_ReleaseId",
                table: "MediaFiles",
                column: "ReleaseId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_ReleasePending",
                table: "MediaFiles",
                column: "Id",
                filter: "\"RecordingId\" IS NOT NULL AND \"ReleaseLookupUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_TrackId",
                table: "MediaFiles",
                column: "TrackId");

            migrationBuilder.AddForeignKey(
                name: "FK_MediaFiles_ReleaseGroups_ReleaseGroupId",
                table: "MediaFiles",
                column: "ReleaseGroupId",
                principalTable: "ReleaseGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_MediaFiles_Releases_ReleaseId",
                table: "MediaFiles",
                column: "ReleaseId",
                principalTable: "Releases",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_MediaFiles_Tracks_TrackId",
                table: "MediaFiles",
                column: "TrackId",
                principalTable: "Tracks",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MediaFiles_ReleaseGroups_ReleaseGroupId",
                table: "MediaFiles");

            migrationBuilder.DropForeignKey(
                name: "FK_MediaFiles_Releases_ReleaseId",
                table: "MediaFiles");

            migrationBuilder.DropForeignKey(
                name: "FK_MediaFiles_Tracks_TrackId",
                table: "MediaFiles");

            migrationBuilder.DropIndex(
                name: "IX_Releases_ReleasedYear",
                table: "Releases");

            migrationBuilder.DropIndex(
                name: "IX_Releases_Title",
                table: "Releases");

            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_ReleaseGroupId",
                table: "MediaFiles");

            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_ReleaseId",
                table: "MediaFiles");

            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_ReleasePending",
                table: "MediaFiles");

            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_TrackId",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "Length",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "Number",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "Disambiguation",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "DiscCount",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "MediumFormats",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "ReleasedDay",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "ReleasedMonth",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "ReleasedYear",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "SecondaryTypes",
                table: "ReleaseGroups");

            migrationBuilder.DropColumn(
                name: "AttributionOutcome",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "EditionAlternatives",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "ReleaseGroupId",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "ReleaseId",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "ReleaseLookupUtc",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "TrackId",
                table: "MediaFiles");

            migrationBuilder.AddColumn<DateOnly>(
                name: "ReleasedOn",
                table: "Releases",
                type: "date",
                nullable: true);
        }
    }
}
