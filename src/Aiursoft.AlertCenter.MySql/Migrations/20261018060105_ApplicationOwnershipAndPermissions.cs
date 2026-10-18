using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.MySql.Migrations
{
    /// <inheritdoc />
    public partial class ApplicationOwnershipAndPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "CanManage",
                table: "ApplicationUserAccesses",
                newName: "CanManageApplication");

            migrationBuilder.RenameColumn(
                name: "CanManage",
                table: "ApplicationRoleAccesses",
                newName: "CanManageApplication");

            migrationBuilder.AddColumn<string>(
                name: "OwnerId",
                table: "SourceApplications",
                type: "varchar(450)",
                maxLength: 450,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "CanCreateIncidents",
                table: "ApplicationUserAccesses",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanDeleteIncidents",
                table: "ApplicationUserAccesses",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanManageApiKeys",
                table: "ApplicationUserAccesses",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanViewIncidents",
                table: "ApplicationUserAccesses",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanUpdateIncidents",
                table: "ApplicationUserAccesses",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanCreateIncidents",
                table: "ApplicationRoleAccesses",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanDeleteIncidents",
                table: "ApplicationRoleAccesses",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanManageApiKeys",
                table: "ApplicationRoleAccesses",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanViewIncidents",
                table: "ApplicationRoleAccesses",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanUpdateIncidents",
                table: "ApplicationRoleAccesses",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            // Preserve existing explicit grants; never infer ownership from a role or platform permission.
            migrationBuilder.Sql("UPDATE ApplicationUserAccesses SET CanViewIncidents = 1, CanUpdateIncidents = CanManageApplication, CanManageApiKeys = CanManageApplication;");
            migrationBuilder.Sql("UPDATE ApplicationRoleAccesses SET CanViewIncidents = 1, CanUpdateIncidents = CanManageApplication, CanManageApiKeys = CanManageApplication;");

            migrationBuilder.CreateIndex(
                name: "IX_SourceApplications_OwnerId",
                table: "SourceApplications",
                column: "OwnerId");

            migrationBuilder.AddForeignKey(
                name: "FK_SourceApplications_AspNetUsers_OwnerId",
                table: "SourceApplications",
                column: "OwnerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SourceApplications_AspNetUsers_OwnerId",
                table: "SourceApplications");

            migrationBuilder.DropIndex(
                name: "IX_SourceApplications_OwnerId",
                table: "SourceApplications");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "SourceApplications");

            migrationBuilder.DropColumn(
                name: "CanCreateIncidents",
                table: "ApplicationUserAccesses");

            migrationBuilder.DropColumn(
                name: "CanDeleteIncidents",
                table: "ApplicationUserAccesses");

            migrationBuilder.DropColumn(
                name: "CanManageApiKeys",
                table: "ApplicationUserAccesses");

            migrationBuilder.DropColumn(
                name: "CanViewIncidents",
                table: "ApplicationUserAccesses");

            migrationBuilder.DropColumn(
                name: "CanUpdateIncidents",
                table: "ApplicationUserAccesses");

            migrationBuilder.DropColumn(
                name: "CanCreateIncidents",
                table: "ApplicationRoleAccesses");

            migrationBuilder.DropColumn(
                name: "CanDeleteIncidents",
                table: "ApplicationRoleAccesses");

            migrationBuilder.DropColumn(
                name: "CanManageApiKeys",
                table: "ApplicationRoleAccesses");

            migrationBuilder.DropColumn(
                name: "CanViewIncidents",
                table: "ApplicationRoleAccesses");

            migrationBuilder.DropColumn(
                name: "CanUpdateIncidents",
                table: "ApplicationRoleAccesses");

            migrationBuilder.RenameColumn(
                name: "CanManageApplication",
                table: "ApplicationUserAccesses",
                newName: "CanManage");

            migrationBuilder.RenameColumn(
                name: "CanManageApplication",
                table: "ApplicationRoleAccesses",
                newName: "CanManage");
        }
    }
}
