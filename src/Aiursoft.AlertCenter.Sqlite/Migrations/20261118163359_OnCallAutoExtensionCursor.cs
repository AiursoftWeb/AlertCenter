using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class OnCallAutoExtensionCursor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AutoExtendedThroughUtc",
                table: "OnCallGroups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "OnCallGroups"
                SET "AutoExtendedThroughUtc" = (
                    SELECT MAX(s."EndsAtUtc")
                    FROM "OnCallShifts" AS s
                    WHERE s."GroupId" = "OnCallGroups"."Id" AND s."Source" = 1
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoExtendedThroughUtc",
                table: "OnCallGroups");
        }
    }
}
