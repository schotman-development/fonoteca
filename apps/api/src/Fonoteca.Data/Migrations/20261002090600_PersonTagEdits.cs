using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersonTagEdits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EditsJson",
                table: "ReleaseGroups",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TagEditsJson",
                table: "MediaFiles",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EditsJson",
                table: "ReleaseGroups");

            migrationBuilder.DropColumn(
                name: "TagEditsJson",
                table: "MediaFiles");
        }
    }
}
