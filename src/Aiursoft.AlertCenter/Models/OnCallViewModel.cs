using System.ComponentModel.DataAnnotations;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Aiursoft.UiStack.Layout;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Aiursoft.AlertCenter.Models;

public class OnCallViewModel : UiStackLayoutViewModel
{
    public OnCallViewModel() { PageTitle = "自动排班规则"; }

    public bool IsAdministrator { get; set; }
    public List<OnCallGroup> Groups { get; set; } = [];
    public List<SelectListItem> TimeZones { get; set; } = [];
    public List<SelectListItem> Roles { get; set; } = [];
    public List<OnCallRule> Rules { get; set; } = [];
    public List<User> SelectedUsers { get; set; } = [];
    public List<User> PreviewUsers { get; set; } = [];
    public Guid? SelectedRuleId { get; set; }
    public IReadOnlyList<GeneratedOnCallShift> Preview { get; set; } = [];
    public IReadOnlyList<OnCallPreviewDay> PreviewDays { get; set; } = [];
    public string? PreviewError { get; set; }
    public DateTime WeekStartUtc { get; set; }
    public bool ScheduleSaved { get; set; }
    public CreateOnCallGroupRequest GroupInput { get; set; } = new();
    public SaveOnCallRuleRequest RuleInput { get; set; } = new();
}

public class CreateOnCallGroupRequest
{
    [Required(ErrorMessage = "请输入团队名称。"), StringLength(100, ErrorMessage = "团队名称不能超过 100 个字符。")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "请选择时区。"), StringLength(100, ErrorMessage = "时区 ID 不能超过 100 个字符。")]
    public string TimeZoneId { get; set; } = "UTC";
}

public class SaveOnCallRuleRequest
{
    public Guid? Id { get; set; }
    [Required(ErrorMessage = "请选择团队。")]
    public Guid? GroupId { get; set; }
    [Required(ErrorMessage = "请输入规则名称。"), StringLength(100, ErrorMessage = "规则名称不能超过 100 个字符。")]
    public string Name { get; set; } = string.Empty;
    [Range(8, 8, ErrorMessage = "固定每天三班，每班 8 小时。")]
    public int ShiftHours { get; set; } = 8;
    public bool BalanceShiftTimes { get; set; }
    [Range(0, 168, ErrorMessage = "最短休息需为 0–168 小时。")]
    public int RestHours { get; set; } = 16;
    [Required(ErrorMessage = "请选择 UTC 开始时间。")]
    public DateTime? AnchorUtc { get; set; }
    public List<string> UserIds { get; set; } = [];
}

public class SaveOnCallScheduleRequest
{
    public Guid RuleId { get; set; }
    public Guid Version { get; set; }
    public DateTime StartUtc { get; set; }
    public List<OnCallSlotRequest> Slots { get; set; } = [];
}

public class OnCallSlotRequest
{
    public DateTime StartsAtUtc { get; set; }
    public List<string> UserIds { get; set; } = [];
}
