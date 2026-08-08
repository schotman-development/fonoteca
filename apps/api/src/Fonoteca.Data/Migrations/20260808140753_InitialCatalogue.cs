using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "Artists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    SortName = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Mbid = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Disambiguation = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Artists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DomainEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SubjectType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DomainEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReleaseGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Mbid = table.Column<Guid>(type: "uuid", nullable: true),
                    PrimaryType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FirstReleaseYear = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Works",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Mbid = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Works", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Releases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Mbid = table.Column<Guid>(type: "uuid", nullable: true),
                    ReleaseGroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    Country = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    ReleasedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Label = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CatalogNumber = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Barcode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    TrackCount = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Releases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Releases_ReleaseGroups_ReleaseGroupId",
                        column: x => x.ReleaseGroupId,
                        principalTable: "ReleaseGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Recordings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Mbid = table.Column<Guid>(type: "uuid", nullable: true),
                    Duration = table.Column<TimeSpan>(type: "interval", nullable: true),
                    WorkId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recordings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Recordings_Works_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Works",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ArtistCredits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtistId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReleaseGroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    JoinPhrase = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreditedAs = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtistCredits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArtistCredits_Artists_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "Artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ArtistCredits_Recordings_RecordingId",
                        column: x => x.RecordingId,
                        principalTable: "Recordings",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ArtistCredits_ReleaseGroups_ReleaseGroupId",
                        column: x => x.ReleaseGroupId,
                        principalTable: "ReleaseGroups",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ArtistCredits_Releases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "Releases",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "MediaFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    LastModifiedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AudioHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Fingerprint = table.Column<string>(type: "text", nullable: true),
                    Integrity = table.Column<int>(type: "integer", nullable: false),
                    LastScannedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastVerifiedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Quality_BitDepth = table.Column<int>(type: "integer", nullable: true),
                    Quality_BitrateBps = table.Column<long>(type: "bigint", nullable: true),
                    Quality_Channels = table.Column<int>(type: "integer", nullable: true),
                    Quality_Codec = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Quality_Duration = table.Column<TimeSpan>(type: "interval", nullable: true),
                    Quality_IsLossless = table.Column<bool>(type: "boolean", nullable: true),
                    Quality_SampleRateHz = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaFiles_Recordings_RecordingId",
                        column: x => x.RecordingId,
                        principalTable: "Recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Relationships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Attribute = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WorkId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Relationships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Relationships_Recordings_RecordingId",
                        column: x => x.RecordingId,
                        principalTable: "Recordings",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Relationships_Works_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Works",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Tracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    DiscNumber = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tracks_Recordings_RecordingId",
                        column: x => x.RecordingId,
                        principalTable: "Recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Tracks_Releases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "Releases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArtistCredits_ArtistId",
                table: "ArtistCredits",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_ArtistCredits_RecordingId",
                table: "ArtistCredits",
                column: "RecordingId");

            migrationBuilder.CreateIndex(
                name: "IX_ArtistCredits_ReleaseGroupId",
                table: "ArtistCredits",
                column: "ReleaseGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ArtistCredits_ReleaseId",
                table: "ArtistCredits",
                column: "ReleaseId");

            migrationBuilder.CreateIndex(
                name: "IX_Artists_Mbid",
                table: "Artists",
                column: "Mbid",
                unique: true,
                filter: "\"Mbid\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Artists_Name",
                table: "Artists",
                column: "Name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Artists_SortName",
                table: "Artists",
                column: "SortName");

            migrationBuilder.CreateIndex(
                name: "IX_DomainEvents_CorrelationId",
                table: "DomainEvents",
                column: "CorrelationId",
                filter: "\"CorrelationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DomainEvents_OccurredAtUtc",
                table: "DomainEvents",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DomainEvents_SubjectType_SubjectId_OccurredAtUtc",
                table: "DomainEvents",
                columns: new[] { "SubjectType", "SubjectId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_AudioHash",
                table: "MediaFiles",
                column: "AudioHash",
                filter: "\"AudioHash\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_ContentHash",
                table: "MediaFiles",
                column: "ContentHash",
                filter: "\"ContentHash\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_Integrity",
                table: "MediaFiles",
                column: "Integrity");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_LastScannedUtc",
                table: "MediaFiles",
                column: "LastScannedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_Path",
                table: "MediaFiles",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_RecordingId",
                table: "MediaFiles",
                column: "RecordingId");

            migrationBuilder.CreateIndex(
                name: "IX_Recordings_Mbid",
                table: "Recordings",
                column: "Mbid",
                unique: true,
                filter: "\"Mbid\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Recordings_Title",
                table: "Recordings",
                column: "Title")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Recordings_WorkId",
                table: "Recordings",
                column: "WorkId");

            migrationBuilder.CreateIndex(
                name: "IX_Relationships_RecordingId",
                table: "Relationships",
                column: "RecordingId");

            migrationBuilder.CreateIndex(
                name: "IX_Relationships_SourceType_SourceId_Type",
                table: "Relationships",
                columns: new[] { "SourceType", "SourceId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_Relationships_TargetType_TargetId_Type",
                table: "Relationships",
                columns: new[] { "TargetType", "TargetId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_Relationships_WorkId",
                table: "Relationships",
                column: "WorkId");

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseGroups_Mbid",
                table: "ReleaseGroups",
                column: "Mbid",
                unique: true,
                filter: "\"Mbid\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseGroups_Title",
                table: "ReleaseGroups",
                column: "Title")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Releases_Barcode",
                table: "Releases",
                column: "Barcode");

            migrationBuilder.CreateIndex(
                name: "IX_Releases_Mbid",
                table: "Releases",
                column: "Mbid",
                unique: true,
                filter: "\"Mbid\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Releases_ReleaseGroupId",
                table: "Releases",
                column: "ReleaseGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_RecordingId",
                table: "Tracks",
                column: "RecordingId");

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_ReleaseId_DiscNumber_Position",
                table: "Tracks",
                columns: new[] { "ReleaseId", "DiscNumber", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Works_Mbid",
                table: "Works",
                column: "Mbid",
                unique: true,
                filter: "\"Mbid\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Works_Title",
                table: "Works",
                column: "Title")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArtistCredits");

            migrationBuilder.DropTable(
                name: "DomainEvents");

            migrationBuilder.DropTable(
                name: "MediaFiles");

            migrationBuilder.DropTable(
                name: "Relationships");

            migrationBuilder.DropTable(
                name: "Tracks");

            migrationBuilder.DropTable(
                name: "Artists");

            migrationBuilder.DropTable(
                name: "Recordings");

            migrationBuilder.DropTable(
                name: "Releases");

            migrationBuilder.DropTable(
                name: "Works");

            migrationBuilder.DropTable(
                name: "ReleaseGroups");
        }
    }
}
