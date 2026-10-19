using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class Loves : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Loves",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: true),
                    MediaFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReleaseGroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArtistId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkId = table.Column<Guid>(type: "uuid", nullable: true),
                    LovedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Loves", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Loves_Artists_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "Artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Loves_MediaFiles_MediaFileId",
                        column: x => x.MediaFileId,
                        principalTable: "MediaFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Loves_Recordings_RecordingId",
                        column: x => x.RecordingId,
                        principalTable: "Recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Loves_ReleaseGroups_ReleaseGroupId",
                        column: x => x.ReleaseGroupId,
                        principalTable: "ReleaseGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Loves_Works_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Works",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Loves_ArtistId",
                table: "Loves",
                column: "ArtistId",
                unique: true,
                filter: "\"ArtistId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Loves_MediaFileId",
                table: "Loves",
                column: "MediaFileId",
                unique: true,
                filter: "\"MediaFileId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Loves_RecordingId",
                table: "Loves",
                column: "RecordingId",
                unique: true,
                filter: "\"RecordingId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Loves_ReleaseGroupId",
                table: "Loves",
                column: "ReleaseGroupId",
                unique: true,
                filter: "\"ReleaseGroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Loves_WorkId",
                table: "Loves",
                column: "WorkId",
                unique: true,
                filter: "\"WorkId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Loves");
        }
    }
}
