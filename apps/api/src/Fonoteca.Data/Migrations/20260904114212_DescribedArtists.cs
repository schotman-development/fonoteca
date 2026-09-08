using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class DescribedArtists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BeganYear",
                table: "Artists",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "Artists",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Ended",
                table: "Artists",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "EndedYear",
                table: "Artists",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Gender",
                table: "Artists",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Genres",
                table: "Artists",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LookupUtc",
                table: "Artists",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Artists_Unasked",
                table: "Artists",
                column: "Id",
                filter: "\"LookupUtc\" IS NULL AND \"Mbid\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Artists_Unasked",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "BeganYear",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "Country",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "Ended",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "EndedYear",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "Gender",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "Genres",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "LookupUtc",
                table: "Artists");
        }
    }
}
