using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.MySql.Migrations
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
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE `OnCallGroups` AS g
                SET `AutoExtendedThroughUtc` = (
                    SELECT MAX(s.`EndsAtUtc`)
                    FROM `OnCallShifts` AS s
                    WHERE s.`GroupId` = g.`Id` AND s.`Source` = 1
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
