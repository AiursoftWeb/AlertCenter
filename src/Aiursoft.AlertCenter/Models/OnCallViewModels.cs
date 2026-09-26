using System.ComponentModel.DataAnnotations;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Aiursoft.UiStack.Layout;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Aiursoft.AlertCenter.Models;

public class OnCallViewModel : UiStackLayoutViewModel
{
    public OnCallViewModel() => PageTitle = "值班";
    public List<OnCallGroup> Groups { get; set; } = [];
    public OnCallGroup? Group { get; set; }
    public OnCallGroupRequest GroupForm { get; set; } = new();
    public bool EditingGroupForm { get; set; }
    public List<OnCallGroupMember> Members { get; set; } = [];
    public List<OnCallShiftTemplate> Templates { get; set; } = [];
    public List<OnCallShiftTemplateMember> TemplateMembers { get; set; } = [];
    public List<OnCallShift> Shifts { get; set; } = [];
    public List<OnCallShiftOverride> Overrides { get; set; } = [];
    public List<OnCallShiftOverride> SettlementOverrides { get; set; } = [];
    public List<OnCallLeaveRequest> LeaveRequests { get; set; } = [];
    public List<OnCallHandoverRequest> HandoverRequests { get; set; } = [];
    public List<OnCallHandoverRequest> IncomingHandovers { get; set; } = [];
    public List<OnCallCoverageDebt> CoverageDebts { get; set; } = [];
    public int PendingLeaveCount { get; set; }
    public int PendingHandoverCount { get; set; }
    public int UnsettledDebtCount { get; set; }
    public List<SelectListItem> Users { get; set; } = [];
    public IReadOnlyList<TimeZoneInfo> TimeZones { get; set; } = TimeZoneInfo.GetSystemTimeZones();
    public string? OperationError { get; set; }
    public OnCallEngineer? CurrentEngineer { get; set; }
    public DateTime DisplayStartUtc { get; set; }
    public DateTime DisplayEndUtc { get; set; }
    public DateOnly SuggestedPublishDate { get; set; }
}

public class OnCallGroupRequest
{
    public Guid? Id { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    [Required, StringLength(100)] public string TimeZoneId { get; set; } = "UTC";
}

public class OnCallMemberRequest
{
    [Required] public Guid GroupId { get; set; }
    [Required, StringLength(450)] public string UserId { get; set; } = string.Empty;
}

public class OnCallTemplateRequest
{
    public Guid? Id { get; set; }
    [Required] public Guid GroupId { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    [Range(0, 1439)] public int StartMinuteOfDay { get; set; }
    [Range(1, 480)] public int DurationMinutes { get; set; } = 480;
    public string[] UserIds { get; set; } = [];
}

public class OnCallPublishRequest : IValidatableObject
{
    [Required] public Guid GroupId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (StartDate == default || EndDate == default)
            yield return new ValidationResult("请选择发布开始和结束日期。", [nameof(StartDate), nameof(EndDate)]);
        else if (StartDate > EndDate)
            yield return new ValidationResult("发布开始日期不能晚于结束日期。", [nameof(StartDate), nameof(EndDate)]);
    }
}

public class OnCallLeaveRequestModel : IValidatableObject
{
    [Required] public Guid GroupId { get; set; }
    [Required, StringLength(450)] public string UserId { get; set; } = string.Empty;
    public DateTime StartsAtLocal { get; set; }
    public DateTime EndsAtLocal { get; set; }
    [Required, StringLength(1000)] public string Reason { get; set; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (StartsAtLocal >= EndsAtLocal)
            yield return new ValidationResult("请假结束时间必须晚于开始时间。", [nameof(EndsAtLocal)]);
    }
}

public class OnCallLeaveReviewRequest
{
    [Required] public Guid Id { get; set; }
    public bool Approve { get; set; }
}

public class OnCallHandoverRequestModel : IValidatableObject
{
    [Required] public Guid ShiftId { get; set; }
    [Required, StringLength(450)] public string ReceivingUserId { get; set; } = string.Empty;
    [StringLength(1000)] public string? Note { get; set; }
    public DateTime StartsAtLocal { get; set; }
    public DateTime EndsAtLocal { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (StartsAtLocal >= EndsAtLocal)
            yield return new ValidationResult("交接结束时间必须晚于开始时间。", [nameof(EndsAtLocal)]);
    }
}

public class OnCallHandoverReviewRequest
{
    [Required] public Guid Id { get; set; }
    public bool Confirm { get; set; }
}

public class OnCallOverrideRequest : IValidatableObject
{
    [Required] public Guid OriginalShiftId { get; set; }
    [Required, StringLength(450)] public string CoveringUserId { get; set; } = string.Empty;
    public DateTime StartsAtLocal { get; set; }
    public DateTime EndsAtLocal { get; set; }
    [Required, StringLength(1000)] public string Reason { get; set; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (StartsAtLocal >= EndsAtLocal)
            yield return new ValidationResult("覆盖结束时间必须晚于开始时间。", [nameof(EndsAtLocal)]);
    }
}

public class OnCallDebtSettlementRequest
{
    [Required] public Guid DebtId { get; set; }
    [Required] public Guid SettlementOverrideId { get; set; }
}
