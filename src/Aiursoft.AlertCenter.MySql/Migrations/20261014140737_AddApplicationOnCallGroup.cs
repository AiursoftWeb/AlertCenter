using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddApplicationOnCallGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OnCallGroupId",
                table: "SourceApplications",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.CreateIndex(
                name: "IX_SourceApplications_OnCallGroupId",
                table: "SourceApplications",
                column: "OnCallGroupId");

            migrationBuilder.AddForeignKey(
                name: "FK_SourceApplications_OnCallGroups_OnCallGroupId",
                table: "SourceApplications",
                column: "OnCallGroupId",
                principalTable: "OnCallGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SourceApplications_OnCallGroups_OnCallGroupId",
                table: "SourceApplications");

            migrationBuilder.DropIndex(
                name: "IX_SourceApplications_OnCallGroupId",
                table: "SourceApplications");

            migrationBuilder.DropColumn(
                name: "OnCallGroupId",
                table: "SourceApplications");
        }
    }
}
