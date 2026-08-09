using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class RelationshipArtistLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ArtistId",
                table: "Relationships",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Relationships_ArtistId",
                table: "Relationships",
                column: "ArtistId");

            migrationBuilder.AddForeignKey(
                name: "FK_Relationships_Artists_ArtistId",
                table: "Relationships",
                column: "ArtistId",
                principalTable: "Artists",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Relationships_Artists_ArtistId",
                table: "Relationships");

            migrationBuilder.DropIndex(
                name: "IX_Relationships_ArtistId",
                table: "Relationships");

            migrationBuilder.DropColumn(
                name: "ArtistId",
                table: "Relationships");
        }
    }
}
