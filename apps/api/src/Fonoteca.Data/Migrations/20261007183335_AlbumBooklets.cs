using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlbumBooklets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlbumBooklets",
                columns: table => new
                {
                    ReleaseGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArchiveRelease = table.Column<Guid>(type: "uuid", nullable: true),
                    QobuzAlbumId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SavedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlbumBooklets", x => x.ReleaseGroupId);
                    table.ForeignKey(
                        name: "FK_AlbumBooklets_ReleaseGroups_ReleaseGroupId",
                        column: x => x.ReleaseGroupId,
                        principalTable: "ReleaseGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AlbumBookletFiles",
                columns: table => new
                {
                    ReleaseGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MediaType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Bytes = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlbumBookletFiles", x => new { x.ReleaseGroupId, x.Position });
                    table.ForeignKey(
                        name: "FK_AlbumBookletFiles_AlbumBooklets_ReleaseGroupId",
                        column: x => x.ReleaseGroupId,
                        principalTable: "AlbumBooklets",
                        principalColumn: "ReleaseGroupId",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlbumBookletFiles");

            migrationBuilder.DropTable(
                name: "AlbumBooklets");
        }
    }
}
