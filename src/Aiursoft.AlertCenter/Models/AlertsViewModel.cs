using System.ComponentModel.DataAnnotations;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.AlertCenter.Models;

public class AlertsViewModel : UiStackLayoutViewModel
{
    public AlertsViewModel() { PageTitle = "告警中心"; }
    public List<SourceApplication> Applications { get; set; } = [];
    public List<ReportedMessage> Messages { get; set; } = [];
    public ReportedMessage? Message { get; set; }
    public string? ApiKey { get; set; }
    public int Page { get; set; } = 1;
    public bool HasNextPage { get; set; }
}

public class CreateApplicationRequest
{
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
}
