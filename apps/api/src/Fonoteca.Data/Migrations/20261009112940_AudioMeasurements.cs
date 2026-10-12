using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class AudioMeasurements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MeasuredUtc",
                table: "MediaFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Measurement_DurationSeconds",
                table: "MediaFiles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Measurement_DynamicRange",
                table: "MediaFiles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Measurement_EffectiveBitDepth",
                table: "MediaFiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Measurement_IntegratedLufs",
                table: "MediaFiles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Measurement_LeadingSilenceSeconds",
                table: "MediaFiles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Measurement_LoudnessRangeLu",
                table: "MediaFiles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Measurement_PeakCount",
                table: "MediaFiles",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Measurement_SamplePeakDbfs",
                table: "MediaFiles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Measurement_TrailingSilenceSeconds",
                table: "MediaFiles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Measurement_TruePeakDbtp",
                table: "MediaFiles",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_MeasuredUtc",
                table: "MediaFiles",
                column: "MeasuredUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_MeasuredUtc",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "MeasuredUtc",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "Measurement_DurationSeconds",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "Measurement_DynamicRange",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "Measurement_EffectiveBitDepth",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "Measurement_IntegratedLufs",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "Measurement_LeadingSilenceSeconds",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "Measurement_LoudnessRangeLu",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "Measurement_PeakCount",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "Measurement_SamplePeakDbfs",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "Measurement_TrailingSilenceSeconds",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "Measurement_TruePeakDbtp",
                table: "MediaFiles");
        }
    }
}
