using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class FollowedArtists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DiscographyLookupUtc",
                table: "Artists",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Followed",
                table: "Artists",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Artists_Unbrowsed",
                table: "Artists",
                column: "Id",
                filter: "\"Followed\" AND \"DiscographyLookupUtc\" IS NULL AND \"Mbid\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Artists_Unbrowsed",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "DiscographyLookupUtc",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "Followed",
                table: "Artists");
        }
    }
}
