using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class Plays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ListenBrainzSeededUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ListenBrainzToken",
                table: "AspNetUsers",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ListenBrainzUser",
                table: "AspNetUsers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Plays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MediaFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReleaseGroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    ForwardedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Plays_AspNetUsers_AccountId",
                        column: x => x.AccountId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Plays_MediaFiles_MediaFileId",
                        column: x => x.MediaFileId,
                        principalTable: "MediaFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Plays_Recordings_RecordingId",
                        column: x => x.RecordingId,
                        principalTable: "Recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Plays_ReleaseGroups_ReleaseGroupId",
                        column: x => x.ReleaseGroupId,
                        principalTable: "ReleaseGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Plays_AccountId_PlayedUtc",
                table: "Plays",
                columns: new[] { "AccountId", "PlayedUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Plays_AccountId_Unforwarded",
                table: "Plays",
                column: "AccountId",
                filter: "\"ForwardedUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Plays_MediaFileId",
                table: "Plays",
                column: "MediaFileId");

            migrationBuilder.CreateIndex(
                name: "IX_Plays_RecordingId",
                table: "Plays",
                column: "RecordingId");

            migrationBuilder.CreateIndex(
                name: "IX_Plays_ReleaseGroupId",
                table: "Plays",
                column: "ReleaseGroupId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Plays");

            migrationBuilder.DropColumn(
                name: "ListenBrainzSeededUtc",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ListenBrainzToken",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ListenBrainzUser",
                table: "AspNetUsers");
        }
    }
}
