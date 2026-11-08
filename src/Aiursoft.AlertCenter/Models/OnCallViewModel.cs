using System.ComponentModel.DataAnnotations;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Aiursoft.UiStack.Layout;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Aiursoft.AlertCenter.Models;

public class OnCallViewModel : UiStackLayoutViewModel
{
    public OnCallViewModel() { PageTitle = "On-call"; }

    public bool CanManageOnCall { get; set; }
    public List<OnCallGroup> Groups { get; set; } = [];
    public List<SelectListItem> TimeZones { get; set; } = [];
    public List<SelectListItem> Roles { get; set; } = [];
    public List<OnCallRule> Rules { get; set; } = [];
    public List<User> SelectedUsers { get; set; } = [];
    public List<User> PreviewUsers { get; set; } = [];
    public Guid? SelectedRuleId { get; set; }
    public Guid? SelectedGroupId { get; set; }
    public IReadOnlyList<GeneratedOnCallShift> Preview { get; set; } = [];
    public List<OnCallShift> AdjacentShifts { get; set; } = [];
    public List<OnCallGroupMember> OtherGroupMemberships { get; set; } = [];
    public List<OnCallShift> OtherGroupShifts { get; set; } = [];
    public IReadOnlyList<OnCallPreviewDay> PreviewDays { get; set; } = [];
    public string? PreviewError { get; set; }
    public DateTime WeekStartUtc { get; set; }
    public bool ScheduleSaved { get; set; }
    public bool GroupHasSavedSchedule { get; set; }
    public bool GroupHasBoundApplications { get; set; }
    public bool CanGenerateSchedule { get; set; }
    public int ScheduleRestHours { get; set; } = 16;
    public int ScheduleMinimumStaff { get; set; } = 1;
    public List<string> StaffingWarnings { get; set; } = [];
    public List<OnCallScheduleValidator.TimelineWarning> TimelineWarnings { get; set; } = [];
    public List<OnCallShift> SavedShifts { get; set; } = [];
    public List<OnCallTeamIssues> UpcomingIssues { get; set; } = [];
    public DateTime WeekEndUtc { get; set; }
    public DateTime PreviousWeekUtc { get; set; }
    public DateTime NextWeekUtc { get; set; }
    public CreateOnCallGroupRequest GroupInput { get; set; } = new();
    public SaveOnCallRuleRequest RuleInput { get; set; } = new();
}

public class CreateOnCallGroupRequest
{
    public DayOfWeek WeekStartsOn { get; set; } = DayOfWeek.Monday;
    public bool FallbackSelectionSubmitted { get; set; }
    [Range(0, double.MaxValue)] public double? MinimumRestHours { get; set; }
    [Range(0, double.MaxValue)] public double? MaximumContinuousHours { get; set; }
    [Range(0, 200)] public int? MinimumShiftStaff { get; set; }
    public bool? AutoExtend { get; set; }
    public List<string>? FallbackUserIds { get; set; }
    [Required(ErrorMessage = "Please enter the team name."), StringLength(100, ErrorMessage = "Team name cannot exceed 100 characters.")]
    public string? Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Please select a time zone."), StringLength(100, ErrorMessage = "Time zone ID cannot exceed 100 characters.")]
    public string? TimeZoneId { get; set; } = "UTC";
    [MaxLength(8000, ErrorMessage = "The on-call email CC configuration must not exceed 8,000 characters.")]
    public string? EmailCcRecipients { get; set; }
    public Guid? RuleId { get; set; }
    public string? AnchorLocal { get; set; }
    public DateTime? AnchorUtc { get; set; } = DateTime.UtcNow.Date;
    public Guid Version { get; set; }
    public List<string>? UserIds { get; set; } = [];
}

public class OnCallGroupFormPreviewRequest
{
    public Guid? GroupId { get; set; }
    public Guid? RuleId { get; set; }
    public string? TimeZoneId { get; set; }
    public string? AnchorLocal { get; set; }
    public List<string>? UserIds { get; set; } = [];
}

public class OnCallGroupFormPreviewResult
{
    public string AnchorLocal { get; set; } = string.Empty;
    public string SuggestedAnchorLocal { get; set; } = string.Empty;
    public bool AnchorLocked { get; set; }
    public List<string> Suggestions { get; set; } = [];
    public List<OnCallSlotRequest> Slots { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}

public class SaveOnCallRuleRequest
{
    public string? SplitMode { get; set; }
    [Range(1, int.MaxValue)] public int? ShiftMinutes { get; set; }
    [Range(1, 200)] public int? StaffPerShift { get; set; }
    public bool? SwapAlternateRounds { get; set; }
    public List<OnCallHandoverPointRequest>? HandoverPoints { get; set; }
    public Guid? Id { get; set; }
    [Required(ErrorMessage = "Please enter the template name."), StringLength(100, ErrorMessage = "Template name must not exceed 100 characters.")]
    public string? Name { get; set; } = string.Empty;
    [Range(1, int.MaxValue)]
    public int ShiftHours { get; set; } = 8;
    public bool BalanceShiftTimes { get; set; }
    [Range(0, 168, ErrorMessage = "Minimum rest must be 0–168 hours.")]
    public int RestHours { get; set; } = 16;
    [Range(1, 200, ErrorMessage = "Minimum staff per shift must be 1–200.")]
    public int MinimumStaff { get; set; } = 1;
    [Range(1, 200, ErrorMessage = "Target staff per shift must be 1–200.")]
    public int? TargetStaff { get; set; } = 1;
}

public class OnCallHandoverPointRequest
{
    public DayOfWeek DayOfWeek { get; set; }
    [Range(0, 1439)] public int MinuteOfDay { get; set; }
}

public class SaveOnCallScheduleRequest
{
    public string? StartLocal { get; set; }
    public string? EndLocal { get; set; }
    public bool ConfirmReplace { get; set; }
    public DateTime? EndUtc { get; set; }
    public Guid GroupId { get; set; }
    public Guid Version { get; set; }
    public DateTime StartUtc { get; set; }
    public List<OnCallSlotRequest> Slots { get; set; } = [];
    public List<OnCallScheduleWeekRequest> AdjacentWeeks { get; set; } = [];
}

public class OnCallScheduleWeekRequest
{
    public DateTime StartUtc { get; set; }
    public List<OnCallSlotRequest> Slots { get; set; } = [];
}

public class OnCallSlotRequest
{
    public string? StartLocal { get; set; }
    public string? EndLocal { get; set; }
    public Guid? Id { get; set; }
    public DateTime StartsAtUtc { get; set; }
    public DateTime? EndsAtUtc { get; set; }
    public List<string> UserIds { get; set; } = [];
}
