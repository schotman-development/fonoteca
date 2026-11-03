using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class ShopGenres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GenresSource",
                table: "ReleaseGroups",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ShopGenresLookupUtc",
                table: "ReleaseGroups",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GenresSource",
                table: "ReleaseGroups");

            migrationBuilder.DropColumn(
                name: "ShopGenresLookupUtc",
                table: "ReleaseGroups");
        }
    }
}
