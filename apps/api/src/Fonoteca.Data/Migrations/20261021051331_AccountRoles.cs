using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fonoteca.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxBitRate",
                table: "AspNetUsers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Roles",
                table: "AspNetUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Every account from before roles keeps doing everything it did:
            // Account.Everything, the owner's choice.
            migrationBuilder.Sql(
                """UPDATE "AspNetUsers" SET "Roles" = 319;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxBitRate",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "Roles",
                table: "AspNetUsers");
        }
    }
}
