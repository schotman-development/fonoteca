using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class LyricsFromLrclib : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecordingLyrics",
                columns: table => new
                {
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Synced = table.Column<string>(type: "text", nullable: true),
                    Plain = table.Column<string>(type: "text", nullable: true),
                    Instrumental = table.Column<bool>(type: "boolean", nullable: false),
                    LrclibId = table.Column<long>(type: "bigint", nullable: true),
                    MatchedBy = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    SavedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordingLyrics", x => x.RecordingId);
                    table.ForeignKey(
                        name: "FK_RecordingLyrics_Recordings_RecordingId",
                        column: x => x.RecordingId,
                        principalTable: "Recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecordingLyrics");
        }
    }
}
