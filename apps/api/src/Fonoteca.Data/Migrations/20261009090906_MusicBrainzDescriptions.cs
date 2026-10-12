using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class MusicBrainzDescriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentId",
                table: "Works",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Genres",
                table: "ReleaseGroups",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "GenresLookupUtc",
                table: "ReleaseGroups",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DescribedUtc",
                table: "Recordings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Genres",
                table: "Recordings",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Isrcs",
                table: "Recordings",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinksJson",
                table: "Artists",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Contributions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Attributes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ArtistMbid = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contributions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contributions_Recordings_RecordingId",
                        column: x => x.RecordingId,
                        principalTable: "Recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Contributions_Works_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Works",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Works_ParentId",
                table: "Works",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Recordings_DescribedUtc",
                table: "Recordings",
                column: "DescribedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Contributions_RecordingId",
                table: "Contributions",
                column: "RecordingId");

            migrationBuilder.CreateIndex(
                name: "IX_Contributions_WorkId",
                table: "Contributions",
                column: "WorkId");

            migrationBuilder.AddForeignKey(
                name: "FK_Works_Works_ParentId",
                table: "Works",
                column: "ParentId",
                principalTable: "Works",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Works_Works_ParentId",
                table: "Works");

            migrationBuilder.DropTable(
                name: "Contributions");

            migrationBuilder.DropIndex(
                name: "IX_Works_ParentId",
                table: "Works");

            migrationBuilder.DropIndex(
                name: "IX_Recordings_DescribedUtc",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "Works");

            migrationBuilder.DropColumn(
                name: "Genres",
                table: "ReleaseGroups");

            migrationBuilder.DropColumn(
                name: "GenresLookupUtc",
                table: "ReleaseGroups");

            migrationBuilder.DropColumn(
                name: "DescribedUtc",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "Genres",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "Isrcs",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "LinksJson",
                table: "Artists");
        }
    }
}
