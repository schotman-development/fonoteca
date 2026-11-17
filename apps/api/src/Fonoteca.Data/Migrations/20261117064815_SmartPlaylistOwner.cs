using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class SmartPlaylistOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                table: "SmartPlaylists",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SmartPlaylists_OwnerId",
                table: "SmartPlaylists",
                column: "OwnerId");

            migrationBuilder.AddForeignKey(
                name: "FK_SmartPlaylists_AspNetUsers_OwnerId",
                table: "SmartPlaylists",
                column: "OwnerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SmartPlaylists_AspNetUsers_OwnerId",
                table: "SmartPlaylists");

            migrationBuilder.DropIndex(
                name: "IX_SmartPlaylists_OwnerId",
                table: "SmartPlaylists");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "SmartPlaylists");
        }
    }
}
