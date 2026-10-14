using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.Sqlite.Migrations
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
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "EmailCcRecipients",
                table: "OnCallGroups",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OnCallEmailSnapshots",
                columns: table => new
                {
                    DeliveryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ShiftId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StartsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NotificationAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CcRecipients = table.Column<string>(type: "text", maxLength: 8000, nullable: true),
                    ValidationError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
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
                });

            migrationBuilder.CreateTable(
                name: "OnCallEmailRecipient",
                columns: table => new
                {
                    DeliveryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true)
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
                });
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
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
