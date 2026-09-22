using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddOnCallScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OnCallGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OnCallGroupMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    JoinedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallGroupMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnCallGroupMembers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OnCallGroupMembers_OnCallGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "OnCallGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OnCallLeaveRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    StartsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ReviewedByUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReviewedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallLeaveRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnCallLeaveRequests_AspNetUsers_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_OnCallLeaveRequests_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OnCallLeaveRequests_OnCallGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "OnCallGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OnCallShiftTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    StartMinuteOfDay = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallShiftTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnCallShiftTemplates_OnCallGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "OnCallGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OnCallShifts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TemplateId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    StartsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallShifts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnCallShifts_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OnCallShifts_OnCallGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "OnCallGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OnCallShifts_OnCallShiftTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "OnCallShiftTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "OnCallShiftTemplateMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TemplateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    RotationOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallShiftTemplateMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnCallShiftTemplateMembers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OnCallShiftTemplateMembers_OnCallShiftTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "OnCallShiftTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OnCallShiftOverrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginalShiftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CoveringUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    StartsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    CreatedByUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallShiftOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnCallShiftOverrides_AspNetUsers_CoveringUserId",
                        column: x => x.CoveringUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OnCallShiftOverrides_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_OnCallShiftOverrides_OnCallGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "OnCallGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OnCallShiftOverrides_OnCallShifts_OriginalShiftId",
                        column: x => x.OriginalShiftId,
                        principalTable: "OnCallShifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OnCallCoverageDebts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginOverrideId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwingUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    CoveringUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    SettlementOverrideId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SettledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallCoverageDebts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnCallCoverageDebts_AspNetUsers_CoveringUserId",
                        column: x => x.CoveringUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OnCallCoverageDebts_AspNetUsers_OwingUserId",
                        column: x => x.OwingUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OnCallCoverageDebts_OnCallGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "OnCallGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OnCallCoverageDebts_OnCallShiftOverrides_OriginOverrideId",
                        column: x => x.OriginOverrideId,
                        principalTable: "OnCallShiftOverrides",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OnCallCoverageDebts_OnCallShiftOverrides_SettlementOverrideId",
                        column: x => x.SettlementOverrideId,
                        principalTable: "OnCallShiftOverrides",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "OnCallHandoverRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ShiftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestedByUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    ReceivingUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    RequestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RespondedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    OverrideId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallHandoverRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnCallHandoverRequests_AspNetUsers_ReceivingUserId",
                        column: x => x.ReceivingUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OnCallHandoverRequests_AspNetUsers_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OnCallHandoverRequests_OnCallShiftOverrides_OverrideId",
                        column: x => x.OverrideId,
                        principalTable: "OnCallShiftOverrides",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_OnCallHandoverRequests_OnCallShifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "OnCallShifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OnCallCoverageDebts_CoveringUserId",
                table: "OnCallCoverageDebts",
                column: "CoveringUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallCoverageDebts_GroupId",
                table: "OnCallCoverageDebts",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallCoverageDebts_OriginOverrideId",
                table: "OnCallCoverageDebts",
                column: "OriginOverrideId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnCallCoverageDebts_OwingUserId",
                table: "OnCallCoverageDebts",
                column: "OwingUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallCoverageDebts_SettlementOverrideId",
                table: "OnCallCoverageDebts",
                column: "SettlementOverrideId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnCallGroupMembers_GroupId_UserId",
                table: "OnCallGroupMembers",
                columns: new[] { "GroupId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnCallGroupMembers_UserId",
                table: "OnCallGroupMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallGroups_Name",
                table: "OnCallGroups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnCallHandoverRequests_OverrideId",
                table: "OnCallHandoverRequests",
                column: "OverrideId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallHandoverRequests_ReceivingUserId",
                table: "OnCallHandoverRequests",
                column: "ReceivingUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallHandoverRequests_RequestedByUserId",
                table: "OnCallHandoverRequests",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallHandoverRequests_ShiftId_Status",
                table: "OnCallHandoverRequests",
                columns: new[] { "ShiftId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_OnCallLeaveRequests_GroupId_UserId_StartsAtUtc_EndsAtUtc",
                table: "OnCallLeaveRequests",
                columns: new[] { "GroupId", "UserId", "StartsAtUtc", "EndsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OnCallLeaveRequests_ReviewedByUserId",
                table: "OnCallLeaveRequests",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallLeaveRequests_UserId",
                table: "OnCallLeaveRequests",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShiftOverrides_CoveringUserId",
                table: "OnCallShiftOverrides",
                column: "CoveringUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShiftOverrides_CreatedByUserId",
                table: "OnCallShiftOverrides",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShiftOverrides_GroupId_StartsAtUtc_EndsAtUtc",
                table: "OnCallShiftOverrides",
                columns: new[] { "GroupId", "StartsAtUtc", "EndsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShiftOverrides_OriginalShiftId",
                table: "OnCallShiftOverrides",
                column: "OriginalShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShifts_GroupId_StartsAtUtc_EndsAtUtc",
                table: "OnCallShifts",
                columns: new[] { "GroupId", "StartsAtUtc", "EndsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShifts_TemplateId",
                table: "OnCallShifts",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShifts_UserId",
                table: "OnCallShifts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShiftTemplateMembers_TemplateId_RotationOrder",
                table: "OnCallShiftTemplateMembers",
                columns: new[] { "TemplateId", "RotationOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShiftTemplateMembers_TemplateId_UserId",
                table: "OnCallShiftTemplateMembers",
                columns: new[] { "TemplateId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShiftTemplateMembers_UserId",
                table: "OnCallShiftTemplateMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShiftTemplates_GroupId_Name",
                table: "OnCallShiftTemplates",
                columns: new[] { "GroupId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OnCallCoverageDebts");

            migrationBuilder.DropTable(
                name: "OnCallGroupMembers");

            migrationBuilder.DropTable(
                name: "OnCallHandoverRequests");

            migrationBuilder.DropTable(
                name: "OnCallLeaveRequests");

            migrationBuilder.DropTable(
                name: "OnCallShiftTemplateMembers");

            migrationBuilder.DropTable(
                name: "OnCallShiftOverrides");

            migrationBuilder.DropTable(
                name: "OnCallShifts");

            migrationBuilder.DropTable(
                name: "OnCallShiftTemplates");

            migrationBuilder.DropTable(
                name: "OnCallGroups");
        }
    }
}
