using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class TempoKeyAndPicardTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DescribedUtc",
                table: "Works",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Languages",
                table: "Works",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PartCount",
                table: "Works",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Position",
                table: "Works",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "Mbid",
                table: "Tracks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediumFormat",
                table: "Tracks",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediumTitle",
                table: "Tracks",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Asin",
                table: "Releases",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DescribedUtc",
                table: "Releases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Script",
                table: "Releases",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TempoAndKeyMeasuredUtc",
                table: "MediaFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TempoAndKey_Bpm",
                table: "MediaFiles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TempoAndKey_BpmConfidence",
                table: "MediaFiles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TempoAndKey_Key",
                table: "MediaFiles",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TempoAndKey_KeyStrength",
                table: "MediaFiles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TempoAndKey_Scale",
                table: "MediaFiles",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Works_DescribedUtc",
                table: "Works",
                column: "DescribedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Releases_DescribedUtc",
                table: "Releases",
                column: "DescribedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_TempoAndKeyMeasuredUtc",
                table: "MediaFiles",
                column: "TempoAndKeyMeasuredUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Works_DescribedUtc",
                table: "Works");

            migrationBuilder.DropIndex(
                name: "IX_Releases_DescribedUtc",
                table: "Releases");

            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_TempoAndKeyMeasuredUtc",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "DescribedUtc",
                table: "Works");

            migrationBuilder.DropColumn(
                name: "Languages",
                table: "Works");

            migrationBuilder.DropColumn(
                name: "PartCount",
                table: "Works");

            migrationBuilder.DropColumn(
                name: "Position",
                table: "Works");

            migrationBuilder.DropColumn(
                name: "Mbid",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "MediumFormat",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "MediumTitle",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "Asin",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "DescribedUtc",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "Script",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "TempoAndKeyMeasuredUtc",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "TempoAndKey_Bpm",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "TempoAndKey_BpmConfidence",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "TempoAndKey_Key",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "TempoAndKey_KeyStrength",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "TempoAndKey_Scale",
                table: "MediaFiles");
        }
    }
}
