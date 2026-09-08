using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <summary>
    /// Ask again for the artists Qobuz refused to name.
    /// </summary>
    /// <remarks>
    /// No schema change: this clears <c>PortraitLookupUtc</c> for every artist
    /// that has no press photograph, so they go back on the picture worklist
    /// once. <c>ReaskForArtistPortraits</c> is the same act for the same reason
    /// and the reason is the same again — the stamp means "we have asked", and
    /// asking now means something different.
    ///
    /// What changed is <c>QobuzPortraits.Picture</c>: two search results
    /// spelling the artist's name exactly used to be refused outright, and are
    /// now decided by album count where one plainly dwarfs the other. That is
    /// the rule that left AC/DC — the best-known name in this library — on a
    /// wide photograph of the Olympic Stadium while every neighbour had a face.
    ///
    /// <b>Narrower than its predecessor, deliberately.</b> An artist who already
    /// has a Qobuz picture cannot be improved by a rule about which Qobuz
    /// picture to take, so re-asking about them would spend their hourly
    /// allowance to write back what is already there. The rest cost a handful of
    /// Wikidata queries — a batch is 250 artists — and one Qobuz request each
    /// for the album artists among them.
    ///
    /// The host is matched rather than the whole URL: it is the same allowlisted
    /// host <c>QobuzPortraits.ImageHost</c> checks, and it is what distinguishes
    /// a press photograph from Wikidata's P18 without this file having to know
    /// how either URL is shaped.
    ///
    /// <c>Down</c> is deliberately empty, for its predecessor's reason: the
    /// inverse of "ask again" is not "un-ask".
    /// </remarks>
    public partial class ReaskForAmbiguousArtistPortraits : Migration
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
