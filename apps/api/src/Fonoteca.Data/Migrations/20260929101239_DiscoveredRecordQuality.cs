using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class DiscoveredRecordQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HiRes",
                table: "DiscoveredRecords",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaximumBitDepth",
                table: "DiscoveredRecords",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MaximumSamplingRate",
                table: "DiscoveredRecords",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HiRes",
                table: "DiscoveredRecords");

            migrationBuilder.DropColumn(
                name: "MaximumBitDepth",
                table: "DiscoveredRecords");

            migrationBuilder.DropColumn(
                name: "MaximumSamplingRate",
                table: "DiscoveredRecords");
        }
    }
}
