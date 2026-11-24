using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.Sqlite.Migrations
{
    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public partial class OnCallShiftStartNonUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OnCallShifts_GroupId_StartsAtUtc",
                table: "OnCallShifts");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShifts_GroupId_StartsAtUtc",
                table: "OnCallShifts",
                columns: new[] { "GroupId", "StartsAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OnCallShifts_GroupId_StartsAtUtc",
                table: "OnCallShifts");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShifts_GroupId_StartsAtUtc",
                table: "OnCallShifts",
                columns: new[] { "GroupId", "StartsAtUtc" },
                unique: true);
        }
    }
}
