using Aiursoft.AlertCenter.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.AlertCenter.Models;

public class SeverityPolicyViewModel : UiStackLayoutViewModel
{
    public SeverityPolicyViewModel() => PageTitle = "Severity policy";
    public bool SmsAvailable { get; set; }
    public List<SeverityRule> Rules { get; set; } = [];
}
