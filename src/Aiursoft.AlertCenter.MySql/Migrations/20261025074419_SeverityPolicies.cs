using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Aiursoft.AlertCenter.MySql.Migrations
{
    /// <inheritdoc />
    public partial class SeverityPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SeverityRules",
                columns: table => new
                {
                    Level = table.Column<string>(type: "varchar(4)", maxLength: 4, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Email = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Sms = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Webhook = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    InApp = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ShowInLists = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeverityRules", x => x.Level);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "SeverityRules",
                columns: new[] { "Level", "Email", "InApp", "ShowInLists", "Sms", "Webhook" },
                values: new object[,]
                {
                    { "sev0", true, true, true, true, true },
                    { "sev1", true, true, true, true, true },
                    { "sev2", true, true, true, true, true },
                    { "sev3", true, true, true, false, true },
                    { "sev4", false, false, true, false, false }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeverityRules");
        }
    }
}
