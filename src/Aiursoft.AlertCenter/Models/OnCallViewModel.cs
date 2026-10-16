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
    public CreateOnCallGroupRequest GroupInput { get; set; } = new();
    public SaveOnCallRuleRequest RuleInput { get; set; } = new();
}

public class CreateOnCallGroupRequest
{
    [Required(ErrorMessage = "Please enter the team name."), StringLength(100, ErrorMessage = "Team name cannot exceed 100 characters.")]
    public string? Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Please select a time zone."), StringLength(100, ErrorMessage = "Time zone ID cannot exceed 100 characters.")]
    public string? TimeZoneId { get; set; } = "UTC";
    [MaxLength(8000, ErrorMessage = "The on-call email CC configuration must not exceed 8,000 characters.")]
    public string? EmailCcRecipients { get; set; }
    public Guid? RuleId { get; set; }
    [Required(ErrorMessage = "Please select a UTC start time.")]
    public DateTime? AnchorUtc { get; set; } = DateTime.UtcNow.Date;
    public Guid Version { get; set; }
    public List<string>? UserIds { get; set; } = [];
}

public class SaveOnCallRuleRequest
{
    public Guid? Id { get; set; }
    [Required(ErrorMessage = "Please enter the rule name."), StringLength(100, ErrorMessage = "Rule name must not exceed 100 characters.")]
    public string? Name { get; set; } = string.Empty;
    [Range(8, 8, ErrorMessage = "Fixed three shifts per day, 8 hours each.")]
    public int ShiftHours { get; set; } = 8;
    public bool BalanceShiftTimes { get; set; }
    [Range(0, 168, ErrorMessage = "Minimum rest must be 0–168 hours.")]
    public int RestHours { get; set; } = 16;
    [Range(1, 200, ErrorMessage = "Minimum staff per shift must be 1–200.")]
    public int MinimumStaff { get; set; } = 1;
    [Range(1, 200, ErrorMessage = "Target staff per shift must be 1–200.")]
    public int? TargetStaff { get; set; } = 1;
}

public class SaveOnCallScheduleRequest
{
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
    public DateTime StartsAtUtc { get; set; }
    public List<string> UserIds { get; set; } = [];
}
