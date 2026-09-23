using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class ArtistFollowedUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FollowedUtc",
                table: "Artists",
                type: "timestamp with time zone",
                nullable: true);

            // The repair, and the column is useless without the second half of
            // it. `Discography.IsNewRelease` returns false for a null
            // `FollowedUtc`, so every artist somebody already follows would have
            // no new releases ever — the feature silently dead for exactly the
            // people it exists for. Nothing records when they were followed, so
            // now() is the only honest answer: their back catalogue is already
            // written and is not re-judged, and only records minted from here on
            // are measured against it.
            //
            // The first statement undoes a live failure. Monitoring used to key
            // on `DiscographyLookupUtc IS NULL` — a first browse lays the
            // baseline, a later one marks arrivals — and that stamp belongs to
            // MusicBrainz. So the first time a shop was asked about artists
            // MusicBrainz had already answered for, the stamp said "later
            // browse" and the shop's whole answer was written as arrivals:
            // measured here at 1,224 rows across 28 followed artists, every
            // artist's monitored count equal to their total, which is the shape
            // that gives it away. Not one record had been released.
            //
            // **Stated consequence, not avoided: this clears the column a person
            // owns.** `DiscoveredRecord.Monitored` is one of the three facts in
            // the catalogue nothing can recompute, and a row somebody marked by
            // hand is indistinguishable from one the bug marked — the bug wrote
            // every row it saw, so there is no provenance to sort them by. On
            // the installation this was written for that was checked before it
            // ran: all 1,224 rows were minted inside one seventeen-minute run
            // and not one had been seen again since, while every hand-marked
            // record sat on `ReleaseGroups` (20 of 142,298) and is untouched
            // here. Narrower is not available; leaving it would keep a shelf
            // nobody can read.
            migrationBuilder.Sql(
                """
                UPDATE "DiscoveredRecords" SET "Monitored" = false
                WHERE "Monitored";

                UPDATE "Artists" SET "FollowedUtc" = now()
                WHERE "Followed" AND "FollowedUtc" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The column goes; the unmonitor does not come back. What it
            // cleared was a wrong answer, and the old rule that produced it is
            // deleted, so restoring those 1,224 rows would put a shelf back that
            // nothing in this tree can now explain.
            migrationBuilder.DropColumn(
                name: "FollowedUtc",
                table: "Artists");
        }
    }
}
