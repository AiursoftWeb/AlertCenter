using System.ComponentModel.DataAnnotations;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Aiursoft.UiStack.Layout;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Aiursoft.AlertCenter.Models;

public class OnCallViewModel : UiStackLayoutViewModel
{
    public OnCallViewModel() { PageTitle = "自动排班预览"; }

    public bool IsAdministrator { get; set; }
    public List<OnCallGroup> Groups { get; set; } = [];
    public List<SelectListItem> TimeZones { get; set; } = [];
    public List<OnCallRule> Rules { get; set; } = [];
    public List<User> Staff { get; set; } = [];
    public Guid? SelectedRuleId { get; set; }
    public Guid? EditingRuleId { get; set; }
    public IReadOnlyList<GeneratedOnCallShift> Preview { get; set; } = [];
    public string? PreviewError { get; set; }
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
    [Range(1, 24, ErrorMessage = "班次时长需为 1–24 小时。")]
    public int ShiftHours { get; set; } = 8;
    [Range(0, 168, ErrorMessage = "最短休息需为 0–168 小时。")]
    public int RestHours { get; set; } = 16;
    [Required(ErrorMessage = "请选择 UTC 开始时间。")]
    public DateTime? AnchorUtc { get; set; }
    public List<OnCallMemberInput> Members { get; set; } = [];
}

public class OnCallMemberInput
{
    public string UserId { get; set; } = string.Empty;
    public bool Selected { get; set; }
    public int RotationOrder { get; set; }
}
