using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class IncidentMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MetadataJson",
                table: "Incidents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CanPatchIncidentMetadata",
                table: "ApplicationApiKeys",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The preceding data-only migration has no target model for EF's table rebuild.
            // These unindexed columns can be dropped directly by the bundled SQLite version.
            migrationBuilder.Sql("""
                ALTER TABLE "Incidents" DROP COLUMN "MetadataJson";
                ALTER TABLE "ApplicationApiKeys" DROP COLUMN "CanPatchIncidentMetadata";
                """);
        }
    }
}
