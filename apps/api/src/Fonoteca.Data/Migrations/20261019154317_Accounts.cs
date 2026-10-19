using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class Accounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Loves_ArtistId",
                table: "Loves");

            migrationBuilder.DropIndex(
                name: "IX_Loves_MediaFileId",
                table: "Loves");

            migrationBuilder.DropIndex(
                name: "IX_Loves_RecordingId",
                table: "Loves");

            migrationBuilder.DropIndex(
                name: "IX_Loves_ReleaseGroupId",
                table: "Loves");

            migrationBuilder.DropIndex(
                name: "IX_Loves_WorkId",
                table: "Loves");

            migrationBuilder.AddColumn<Guid>(
                name: "AccountId",
                table: "Loves",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubsonicPassword = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiKeys_AspNetUsers_AccountId",
                        column: x => x.AccountId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Loves_AccountId_ArtistId",
                table: "Loves",
                columns: new[] { "AccountId", "ArtistId" },
                unique: true,
                filter: "\"ArtistId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Loves_AccountId_MediaFileId",
                table: "Loves",
                columns: new[] { "AccountId", "MediaFileId" },
                unique: true,
                filter: "\"MediaFileId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Loves_AccountId_RecordingId",
                table: "Loves",
                columns: new[] { "AccountId", "RecordingId" },
                unique: true,
                filter: "\"RecordingId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Loves_AccountId_ReleaseGroupId",
                table: "Loves",
                columns: new[] { "AccountId", "ReleaseGroupId" },
                unique: true,
                filter: "\"ReleaseGroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Loves_AccountId_WorkId",
                table: "Loves",
                columns: new[] { "AccountId", "WorkId" },
                unique: true,
                filter: "\"WorkId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Loves_ArtistId",
                table: "Loves",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_Loves_MediaFileId",
                table: "Loves",
                column: "MediaFileId");

            migrationBuilder.CreateIndex(
                name: "IX_Loves_RecordingId",
                table: "Loves",
                column: "RecordingId");

            migrationBuilder.CreateIndex(
                name: "IX_Loves_ReleaseGroupId",
                table: "Loves",
                column: "ReleaseGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Loves_WorkId",
                table: "Loves",
                column: "WorkId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_AccountId",
                table: "ApiKeys",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_Hash",
                table: "ApiKeys",
                column: "Hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Loves_AspNetUsers_AccountId",
                table: "Loves",
                column: "AccountId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Loves_AspNetUsers_AccountId",
                table: "Loves");

            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_Loves_AccountId_ArtistId",
                table: "Loves");

            migrationBuilder.DropIndex(
                name: "IX_Loves_AccountId_MediaFileId",
                table: "Loves");

            migrationBuilder.DropIndex(
                name: "IX_Loves_AccountId_RecordingId",
                table: "Loves");

            migrationBuilder.DropIndex(
                name: "IX_Loves_AccountId_ReleaseGroupId",
                table: "Loves");

            migrationBuilder.DropIndex(
                name: "IX_Loves_AccountId_WorkId",
                table: "Loves");

            migrationBuilder.DropIndex(
                name: "IX_Loves_ArtistId",
                table: "Loves");

            migrationBuilder.DropIndex(
                name: "IX_Loves_MediaFileId",
                table: "Loves");

            migrationBuilder.DropIndex(
                name: "IX_Loves_RecordingId",
                table: "Loves");

            migrationBuilder.DropIndex(
                name: "IX_Loves_ReleaseGroupId",
                table: "Loves");

            migrationBuilder.DropIndex(
                name: "IX_Loves_WorkId",
                table: "Loves");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "Loves");

            migrationBuilder.CreateIndex(
                name: "IX_Loves_ArtistId",
                table: "Loves",
                column: "ArtistId",
                unique: true,
                filter: "\"ArtistId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Loves_MediaFileId",
                table: "Loves",
                column: "MediaFileId",
                unique: true,
                filter: "\"MediaFileId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Loves_RecordingId",
                table: "Loves",
                column: "RecordingId",
                unique: true,
                filter: "\"RecordingId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Loves_ReleaseGroupId",
                table: "Loves",
                column: "ReleaseGroupId",
                unique: true,
                filter: "\"ReleaseGroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Loves_WorkId",
                table: "Loves",
                column: "WorkId",
                unique: true,
                filter: "\"WorkId\" IS NOT NULL");
        }
    }
}
