using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class ArtistAndAlbumProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EditsJson",
                table: "Releases",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewLookupUtc",
                table: "ReleaseGroups",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewText",
                table: "ReleaseGroups",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewUrl",
                table: "ReleaseGroups",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BannerLookupUtc",
                table: "Artists",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BannerUrl",
                table: "Artists",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BiographyLookupUtc",
                table: "Artists",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BiographyText",
                table: "Artists",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BiographyUrl",
                table: "Artists",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EditsJson",
                table: "Artists",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EditsJson",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "ReviewLookupUtc",
                table: "ReleaseGroups");

            migrationBuilder.DropColumn(
                name: "ReviewText",
                table: "ReleaseGroups");

            migrationBuilder.DropColumn(
                name: "ReviewUrl",
                table: "ReleaseGroups");

            migrationBuilder.DropColumn(
                name: "BannerLookupUtc",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "BannerUrl",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "BiographyLookupUtc",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "BiographyText",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "BiographyUrl",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "EditsJson",
                table: "Artists");
        }
    }
}
