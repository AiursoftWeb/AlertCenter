using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddApplicationAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApplicationRoleAccesses",
                columns: table => new
                {
                    ApplicationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoleId = table.Column<string>(type: "TEXT", nullable: false),
                    CanManage = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationRoleAccesses", x => new { x.ApplicationId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_ApplicationRoleAccesses_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApplicationRoleAccesses_SourceApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "SourceApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationUserAccesses",
                columns: table => new
                {
                    ApplicationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    CanManage = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationUserAccesses", x => new { x.ApplicationId, x.UserId });
                    table.ForeignKey(
                        name: "FK_ApplicationUserAccesses_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApplicationUserAccesses_SourceApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "SourceApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationRoleAccesses_RoleId",
                table: "ApplicationRoleAccesses",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationUserAccesses_UserId",
                table: "ApplicationUserAccesses",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApplicationRoleAccesses");

            migrationBuilder.DropTable(
                name: "ApplicationUserAccesses");
        }
    }
}
