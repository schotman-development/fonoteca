using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlbumMotions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlbumMotions",
                columns: table => new
                {
                    ReleaseGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Square = table.Column<byte[]>(type: "bytea", nullable: true),
                    Tall = table.Column<byte[]>(type: "bytea", nullable: true),
                    AppleAlbumId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Storefront = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    MatchedBy = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    SavedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlbumMotions", x => x.ReleaseGroupId);
                    table.ForeignKey(
                        name: "FK_AlbumMotions_ReleaseGroups_ReleaseGroupId",
                        column: x => x.ReleaseGroupId,
                        principalTable: "ReleaseGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlbumMotions");
        }
    }
}
