using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class Waveforms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<float[]>(
                name: "Waveform",
                table: "MediaFiles",
                type: "real[]",
                nullable: true);

            // Rule 1's corollary: every file the measure pass has asked about
            // carries `MeasuredUtc`, so without this none of them would be
            // decoded again for a waveform. Narrowed to the files it measured;
            // one it could not has no audio a waveform could come from. The
            // pass writes the same summaries back, and stamps again.
            migrationBuilder.Sql(
                """
                UPDATE "MediaFiles" SET "MeasuredUtc" = NULL
                WHERE "Measurement_LeadingSilenceSeconds" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Waveform",
                table: "MediaFiles");
        }
    }
}
