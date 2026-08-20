using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class StoredComponentCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReleaseCandidateSets",
                columns: table => new
                {
                    ComponentUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DocumentJson = table.Column<string>(type: "jsonb", nullable: false),
                    Files = table.Column<int>(type: "integer", nullable: false),
                    GatheredUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseCandidateSets", x => x.ComponentUtc);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseCandidateSets_GatheredUtc",
                table: "ReleaseCandidateSets",
                column: "GatheredUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReleaseCandidateSets");
        }
    }
}
