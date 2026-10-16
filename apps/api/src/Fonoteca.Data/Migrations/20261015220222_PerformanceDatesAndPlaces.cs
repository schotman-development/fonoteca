using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class PerformanceDatesAndPlaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ComposedBegin",
                table: "Works",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ComposedEnd",
                table: "Works",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Begin",
                table: "Contributions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "End",
                table: "Contributions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RecordingSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Mbid = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Begin = table.Column<string>(type: "text", nullable: true),
                    End = table.Column<string>(type: "text", nullable: true),
                    Position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordingSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecordingSessions_Recordings_RecordingId",
                        column: x => x.RecordingId,
                        principalTable: "Recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecordingSessions_RecordingId",
                table: "RecordingSessions",
                column: "RecordingId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecordingSessions");

            migrationBuilder.DropColumn(
                name: "ComposedBegin",
                table: "Works");

            migrationBuilder.DropColumn(
                name: "ComposedEnd",
                table: "Works");

            migrationBuilder.DropColumn(
                name: "Begin",
                table: "Contributions");

            migrationBuilder.DropColumn(
                name: "End",
                table: "Contributions");
        }
    }
}
