using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlaylistFoldersAndTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlaylistFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaylistFolders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaylistFolders_AspNetUsers_AccountId",
                        column: x => x.AccountId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlaylistFolders_PlaylistFolders_ParentId",
                        column: x => x.ParentId,
                        principalTable: "PlaylistFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tags_AspNetUsers_AccountId",
                        column: x => x.AccountId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Tags_Tags_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlaylistPlacements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Playlist = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    FolderId = table.Column<Guid>(type: "uuid", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaylistPlacements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaylistPlacements_AspNetUsers_AccountId",
                        column: x => x.AccountId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlaylistPlacements_PlaylistFolders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "PlaylistFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SongTags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TagId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: true),
                    MediaFileId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SongTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SongTags_MediaFiles_MediaFileId",
                        column: x => x.MediaFileId,
                        principalTable: "MediaFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SongTags_Recordings_RecordingId",
                        column: x => x.RecordingId,
                        principalTable: "Recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SongTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistFolders_AccountId",
                table: "PlaylistFolders",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistFolders_ParentId",
                table: "PlaylistFolders",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistPlacements_AccountId_Playlist",
                table: "PlaylistPlacements",
                columns: new[] { "AccountId", "Playlist" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistPlacements_FolderId",
                table: "PlaylistPlacements",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistPlacements_Playlist",
                table: "PlaylistPlacements",
                column: "Playlist");

            migrationBuilder.CreateIndex(
                name: "IX_SongTags_MediaFileId",
                table: "SongTags",
                column: "MediaFileId");

            migrationBuilder.CreateIndex(
                name: "IX_SongTags_RecordingId",
                table: "SongTags",
                column: "RecordingId");

            migrationBuilder.CreateIndex(
                name: "IX_SongTags_TagId_MediaFileId",
                table: "SongTags",
                columns: new[] { "TagId", "MediaFileId" },
                unique: true,
                filter: "\"MediaFileId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SongTags_TagId_RecordingId",
                table: "SongTags",
                columns: new[] { "TagId", "RecordingId" },
                unique: true,
                filter: "\"RecordingId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_AccountId_ParentId_Name",
                table: "Tags",
                columns: new[] { "AccountId", "ParentId", "Name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_Tags_ParentId",
                table: "Tags",
                column: "ParentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaylistPlacements");

            migrationBuilder.DropTable(
                name: "SongTags");

            migrationBuilder.DropTable(
                name: "PlaylistFolders");

            migrationBuilder.DropTable(
                name: "Tags");
        }
    }
}
