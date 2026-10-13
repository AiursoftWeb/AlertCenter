using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    AvatarRelativePath = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: true),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumber = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GlobalSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlobalSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "OnCallRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ShiftHours = table.Column<int>(type: "INTEGER", nullable: false),
                    RestHours = table.Column<int>(type: "INTEGER", nullable: false),
                    BalanceShiftTimes = table.Column<bool>(type: "INTEGER", nullable: false),
                    MinimumStaff = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetStaff = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PushChannels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    WebhookHeadersProtected = table.Column<string>(type: "text", nullable: true),
                    WebhookPayloadTemplate = table.Column<string>(type: "text", nullable: true),
                    EmailRecipient = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    EmailCcRecipients = table.Column<string>(type: "text", maxLength: 8000, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushChannels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourceApplications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SecretHash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceApplications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RoleId = table.Column<string>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    RoleId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OnCallGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AnchorUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ScheduleVersion = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnCallGroups_OnCallRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "OnCallRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PushChannelBindingRoles",
                columns: table => new
                {
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoleId = table.Column<string>(type: "TEXT", nullable: false)
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
                });

            migrationBuilder.CreateTable(
                name: "PushChannelBindingUsers",
                columns: table => new
                {
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false)
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
                });

            migrationBuilder.CreateTable(
                name: "PushChannelRoles",
                columns: table => new
                {
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoleId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushChannelRoles", x => new { x.ChannelId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_PushChannelRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PushChannelRoles_PushChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "PushChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PushChannelUsers",
                columns: table => new
                {
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushChannelUsers", x => new { x.ChannelId, x.UserId });
                    table.ForeignKey(
                        name: "FK_PushChannelUsers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PushChannelUsers_PushChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "PushChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationPushChannels",
                columns: table => new
                {
                    ApplicationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReceiveSev0 = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    ReceiveSev1 = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    ReceiveSev2 = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    ReceiveSev3 = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    ReceiveSev4 = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationPushChannels", x => new { x.ApplicationId, x.ChannelId });
                    table.ForeignKey(
                        name: "FK_ApplicationPushChannels_PushChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "PushChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApplicationPushChannels_SourceApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "SourceApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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

            migrationBuilder.CreateTable(
                name: "Incidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IncidentKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ActiveMatchKey = table.Column<string>(type: "TEXT", maxLength: 220, nullable: true),
                    AlertTitle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AlertLevel = table.Column<string>(type: "TEXT", maxLength: 4, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    TriggeredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResolvedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Incidents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Incidents_SourceApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "SourceApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OnCallGroupMembers",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    RotationOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallGroupMembers", x => new { x.GroupId, x.UserId });
                    table.ForeignKey(
                        name: "FK_OnCallGroupMembers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OnCallGroupMembers_OnCallGroups_GroupId",
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
                    StartsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RestHours = table.Column<int>(type: "INTEGER", nullable: false),
                    MinimumStaff = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallShifts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnCallShifts_OnCallGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "OnCallGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IncidentTimelineEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IncidentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentTimelineEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentTimelineEntries_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_IncidentTimelineEntries_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReportedMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IncidentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReportId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IncidentKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Content = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Level = table.Column<string>(type: "TEXT", maxLength: 4, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ContextJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportedMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportedMessages_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ReportedMessages_SourceApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "SourceApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OnCallShiftMembers",
                columns: table => new
                {
                    ShiftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallShiftMembers", x => new { x.ShiftId, x.UserId });
                    table.ForeignKey(
                        name: "FK_OnCallShiftMembers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OnCallShiftMembers_OnCallShifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "OnCallShifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PushDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TestContent = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AttemptedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PushDeliveries_PushChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "PushChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PushDeliveries_ReportedMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "ReportedMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InAppNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InAppNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InAppNotifications_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InAppNotifications_PushDeliveries_DeliveryId",
                        column: x => x.DeliveryId,
                        principalTable: "PushDeliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PushDeliveryRecipients",
                columns: table => new
                {
                    DeliveryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushDeliveryRecipients", x => new { x.DeliveryId, x.UserId });
                    table.ForeignKey(
                        name: "FK_PushDeliveryRecipients_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PushDeliveryRecipients_PushDeliveries_DeliveryId",
                        column: x => x.DeliveryId,
                        principalTable: "PushDeliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationPushChannels_ChannelId",
                table: "ApplicationPushChannels",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationRoleAccesses_RoleId",
                table: "ApplicationRoleAccesses",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationUserAccesses_UserId",
                table: "ApplicationUserAccesses",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InAppNotifications_DeliveryId_UserId",
                table: "InAppNotifications",
                columns: new[] { "DeliveryId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InAppNotifications_UserId_CreatedAtUtc",
                table: "InAppNotifications",
                columns: new[] { "UserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_ApplicationId_ActiveMatchKey",
                table: "Incidents",
                columns: new[] { "ApplicationId", "ActiveMatchKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_ApplicationId_AlertTitle_AlertLevel_Status",
                table: "Incidents",
                columns: new[] { "ApplicationId", "AlertTitle", "AlertLevel", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_ApplicationId_IncidentKey",
                table: "Incidents",
                columns: new[] { "ApplicationId", "IncidentKey" });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentTimelineEntries_ActorId",
                table: "IncidentTimelineEntries",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_IncidentTimelineEntries_IncidentId_CreatedAtUtc",
                table: "IncidentTimelineEntries",
                columns: new[] { "IncidentId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OnCallGroupMembers_GroupId_RotationOrder",
                table: "OnCallGroupMembers",
                columns: new[] { "GroupId", "RotationOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnCallGroupMembers_UserId",
                table: "OnCallGroupMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallGroups_RuleId",
                table: "OnCallGroups",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShiftMembers_UserId",
                table: "OnCallShiftMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShifts_GroupId_StartsAtUtc",
                table: "OnCallShifts",
                columns: new[] { "GroupId", "StartsAtUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushChannelBindingRoles_RoleId",
                table: "PushChannelBindingRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_PushChannelBindingUsers_UserId",
                table: "PushChannelBindingUsers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PushChannelRoles_RoleId",
                table: "PushChannelRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_PushChannelUsers_UserId",
                table: "PushChannelUsers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PushDeliveries_ChannelId",
                table: "PushDeliveries",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_PushDeliveries_MessageId_ChannelId",
                table: "PushDeliveries",
                columns: new[] { "MessageId", "ChannelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushDeliveries_Status_CreatedAtUtc",
                table: "PushDeliveries",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PushDeliveryRecipients_UserId",
                table: "PushDeliveryRecipients",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportedMessages_ApplicationId_ReceivedAtUtc",
                table: "ReportedMessages",
                columns: new[] { "ApplicationId", "ReceivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportedMessages_ApplicationId_ReportId",
                table: "ReportedMessages",
                columns: new[] { "ApplicationId", "ReportId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportedMessages_IncidentId_ReceivedAtUtc",
                table: "ReportedMessages",
                columns: new[] { "IncidentId", "ReceivedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApplicationPushChannels");

            migrationBuilder.DropTable(
                name: "ApplicationRoleAccesses");

            migrationBuilder.DropTable(
                name: "ApplicationUserAccesses");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "GlobalSettings");

            migrationBuilder.DropTable(
                name: "InAppNotifications");

            migrationBuilder.DropTable(
                name: "IncidentTimelineEntries");

            migrationBuilder.DropTable(
                name: "OnCallGroupMembers");

            migrationBuilder.DropTable(
                name: "OnCallShiftMembers");

            migrationBuilder.DropTable(
                name: "PushChannelBindingRoles");

            migrationBuilder.DropTable(
                name: "PushChannelBindingUsers");

            migrationBuilder.DropTable(
                name: "PushChannelRoles");

            migrationBuilder.DropTable(
                name: "PushChannelUsers");

            migrationBuilder.DropTable(
                name: "PushDeliveryRecipients");

            migrationBuilder.DropTable(
                name: "OnCallShifts");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "PushDeliveries");

            migrationBuilder.DropTable(
                name: "OnCallGroups");

            migrationBuilder.DropTable(
                name: "PushChannels");

            migrationBuilder.DropTable(
                name: "ReportedMessages");

            migrationBuilder.DropTable(
                name: "OnCallRules");

            migrationBuilder.DropTable(
                name: "Incidents");

            migrationBuilder.DropTable(
                name: "SourceApplications");
        }
    }
}
