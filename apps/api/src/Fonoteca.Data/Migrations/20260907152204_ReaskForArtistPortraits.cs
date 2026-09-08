using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <summary>
    /// Ask again, now that there is a better source to ask.
    /// </summary>
    /// <remarks>
    /// No schema change: this clears <c>PortraitLookupUtc</c> so that every
    /// artist goes back on the picture worklist once.
    ///
    /// The stamp means "we have asked", and asking now means something
    /// different — the pass prefers a Qobuz press photograph for the artists an
    /// album is billed to, where before there was only Wikidata's P18, which for
    /// a band is very often a wide concert shot with the band eight pixels of
    /// it. Left alone, the stamp is exactly what would stop the improvement
    /// reaching a catalogue that already has one: nothing else clears it, and
    /// the feature would appear not to work at all.
    ///
    /// The codebase's documented way to re-ask after a rule change is a
    /// hand-written UPDATE. This is that UPDATE, run once, where nobody has to
    /// be told about it. It costs a dozen Wikidata queries and one Qobuz request
    /// per album artist — measured, about six minutes for this library — and it
    /// throws away no work that cannot be recovered by asking.
    ///
    /// <c>Down</c> is deliberately empty. The inverse of "ask again" is not
    /// "un-ask": the previous stamps and the pictures they went with are gone,
    /// and pretending otherwise by writing some timestamp back would be
    /// inventing a lookup that did not happen.
    /// </remarks>
    public partial class ReaskForArtistPortraits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(
                """UPDATE "Artists" SET "PortraitLookupUtc" = NULL;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
