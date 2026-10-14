using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.MySql.Migrations
{
    /// <inheritdoc />
    public partial class OnCallEmailNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ChannelId",
                table: "PushDeliveries",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)")
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "EmailCcRecipients",
                table: "OnCallGroups",
                type: "varchar(8000)",
                maxLength: 8000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "OnCallEmailSnapshots",
                columns: table => new
                {
                    DeliveryId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    GroupId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    GroupName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ShiftId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    StartsAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    EndsAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    NotificationAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CcRecipients = table.Column<string>(type: "text", maxLength: 8000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ValidationError = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallEmailSnapshots", x => x.DeliveryId);
                    table.ForeignKey(
                        name: "FK_OnCallEmailSnapshots_PushDeliveries_DeliveryId",
                        column: x => x.DeliveryId,
                        principalTable: "PushDeliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "OnCallEmailRecipient",
                columns: table => new
                {
                    DeliveryId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<string>(type: "varchar(450)", maxLength: 450, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DisplayName = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Email = table.Column<string>(type: "varchar(320)", maxLength: 320, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallEmailRecipient", x => new { x.DeliveryId, x.UserId });
                    table.ForeignKey(
                        name: "FK_OnCallEmailRecipient_OnCallEmailSnapshots_DeliveryId",
                        column: x => x.DeliveryId,
                        principalTable: "OnCallEmailSnapshots",
                        principalColumn: "DeliveryId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM PushDeliveries WHERE ChannelId IS NULL");
            migrationBuilder.DropTable(
                name: "OnCallEmailRecipient");

            migrationBuilder.DropTable(
                name: "OnCallEmailSnapshots");

            migrationBuilder.DropColumn(
                name: "EmailCcRecipients",
                table: "OnCallGroups");

            migrationBuilder.AlterColumn<Guid>(
                name: "ChannelId",
                table: "PushDeliveries",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)",
                oldNullable: true)
                .OldAnnotation("Relational:Collation", "ascii_general_ci");
        }
    }
}
