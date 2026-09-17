using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddPushChannelBindingAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PushChannelBindingRoles",
                columns: table => new
                {
                    ChannelId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RoleId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushChannelBindingRoles", x => new { x.ChannelId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_PushChannelBindingRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PushChannelBindingRoles_PushChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "PushChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PushChannelBindingUsers",
                columns: table => new
                {
                    ChannelId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushChannelBindingUsers", x => new { x.ChannelId, x.UserId });
                    table.ForeignKey(
                        name: "FK_PushChannelBindingUsers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PushChannelBindingUsers_PushChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "PushChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_PushChannelBindingRoles_RoleId",
                table: "PushChannelBindingRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_PushChannelBindingUsers_UserId",
                table: "PushChannelBindingUsers",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PushChannelBindingRoles");

            migrationBuilder.DropTable(
                name: "PushChannelBindingUsers");
        }
    }
}
