using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddOnCallWorkflowTimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EndsAtUtc",
                table: "OnCallHandoverRequests",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "StartsAtUtc",
                table: "OnCallHandoverRequests",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<int>(
                name: "DurationMinutes",
                table: "OnCallCoverageDebts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EndsAtUtc",
                table: "OnCallHandoverRequests");

            migrationBuilder.DropColumn(
                name: "StartsAtUtc",
                table: "OnCallHandoverRequests");

            migrationBuilder.DropColumn(
                name: "DurationMinutes",
                table: "OnCallCoverageDebts");
        }
    }
}
