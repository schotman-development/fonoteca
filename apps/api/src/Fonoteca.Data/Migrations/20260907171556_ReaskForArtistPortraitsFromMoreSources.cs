using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <summary>
    /// Ask again, now that there are four places to ask instead of two.
    /// </summary>
    /// <remarks>
    /// No schema change: this clears <c>PortraitLookupUtc</c> for every artist
    /// that has no press photograph, so they go back on the picture worklist
    /// once. It is the third of these and the reasoning has not changed — the
    /// stamp means "we have asked", and asking now means something different.
    ///
    /// What changed is the chain. TheAudioDB joins Qobuz ahead of Wikidata's
    /// P18: it is looked up by MusicBrainz id and keeps artist thumbnails in a
    /// different field from album art, so it is both certain about who and
    /// certain to be a photograph — and it is the only source that reaches an
    /// artist whose name is not written in Latin script. Measured against the
    /// forty album artists Qobuz could not place, it answers for 16, and 15 of
    /// those 16 are genuine photographs.
    ///
    /// <b>Narrower than a blanket re-ask, deliberately.</b> An artist who
    /// already has a Qobuz picture cannot be improved by sources that are asked
    /// after Qobuz, so re-asking about them would spend a rationed allowance to
    /// write back what is already there.
    ///
    /// <b>And it matters more than the previous two did</b>, because the album
    /// fallback went away in the same change: an artist who ends this run
    /// without a picture now draws a monogram where they used to draw a sleeve.
    /// Skipping the re-ask would leave the catalogue looking emptier than it is.
    ///
    /// <c>Down</c> is deliberately empty, for its predecessors' reason: the
    /// inverse of "ask again" is not "un-ask".
    /// </remarks>
    public partial class ReaskForArtistPortraitsFromMoreSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(
                """
                UPDATE "Artists" SET "PortraitLookupUtc" = NULL
                WHERE "PortraitUrl" IS NULL
                   OR "PortraitUrl" NOT LIKE 'https://static.qobuz.com/%';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
