using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class AcoustIdIdentification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AcoustId",
                table: "MediaFiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AcoustIdCheckedUtc",
                table: "MediaFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AcoustIdOutcome",
                table: "MediaFiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AcoustIdTaggedUtc",
                table: "MediaFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "FingerprintDuration",
                table: "MediaFiles",
                type: "interval",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_AcoustId",
                table: "MediaFiles",
                column: "AcoustId",
                filter: "\"AcoustId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_AcoustIdPending",
                table: "MediaFiles",
                column: "Id",
                filter: "\"AcoustIdCheckedUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_AcoustIdUntagged",
                table: "MediaFiles",
                column: "Id",
                filter: "\"AcoustId\" IS NOT NULL AND \"AcoustIdTaggedUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_AcoustId",
                table: "MediaFiles");

            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_AcoustIdPending",
                table: "MediaFiles");

            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_AcoustIdUntagged",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "AcoustId",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "AcoustIdCheckedUtc",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "AcoustIdOutcome",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "AcoustIdTaggedUtc",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "FingerprintDuration",
                table: "MediaFiles");
        }
    }
}
