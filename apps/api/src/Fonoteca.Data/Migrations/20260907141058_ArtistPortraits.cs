using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class ArtistPortraits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PortraitLookupUtc",
                table: "Artists",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PortraitUrl",
                table: "Artists",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PortraitLookupUtc",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "PortraitUrl",
                table: "Artists");
        }
    }
}
