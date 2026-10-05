using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class AcoustIdDecidedAndIsrc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_AcoustIdPending",
                table: "MediaFiles");

            migrationBuilder.AddColumn<string>(
                name: "Isrc",
                table: "Recordings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AcoustIdDecidedUtc",
                table: "MediaFiles",
                type: "timestamp with time zone",
                nullable: true);

            // Every decision so far was a person's or an agent's, which answer both.
            migrationBuilder.Sql(
                """
                UPDATE "MediaFiles" SET "AcoustIdDecidedUtc" = "IdentityDecidedUtc"
                WHERE "IdentityDecidedUtc" IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_AcoustIdPending",
                table: "MediaFiles",
                column: "Id",
                filter: "\"AcoustIdCheckedUtc\" IS NULL AND \"AcoustIdDecidedUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_AcoustIdPending",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "Isrc",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "AcoustIdDecidedUtc",
                table: "MediaFiles");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_AcoustIdPending",
                table: "MediaFiles",
                column: "Id",
                filter: "\"AcoustIdCheckedUtc\" IS NULL AND \"IdentityDecidedUtc\" IS NULL");
        }
    }
}
