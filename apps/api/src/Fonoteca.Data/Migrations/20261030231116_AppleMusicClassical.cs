using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class AppleMusicClassical : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppleWorkId",
                table: "Works",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppleClassicalAlbumId",
                table: "ReleaseGroups",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClassicalLookupUtc",
                table: "ReleaseGroups",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Contributions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Works_AppleWorkId",
                table: "Works",
                column: "AppleWorkId",
                unique: true,
                filter: "\"AppleWorkId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Works_AppleWorkId",
                table: "Works");

            migrationBuilder.DropColumn(
                name: "AppleWorkId",
                table: "Works");

            migrationBuilder.DropColumn(
                name: "AppleClassicalAlbumId",
                table: "ReleaseGroups");

            migrationBuilder.DropColumn(
                name: "ClassicalLookupUtc",
                table: "ReleaseGroups");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Contributions");
        }
    }
}
