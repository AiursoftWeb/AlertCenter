using System.ComponentModel.DataAnnotations;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.AlertCenter.Models;

public class CreateIncidentViewModel : UiStackLayoutViewModel
{
    public CreateIncidentViewModel() => PageTitle = "Create Incident";
    public Guid ApplicationId { get; set; }
    [Required, StringLength(200)] public string Title { get; set; } = string.Empty;
    [StringLength(4000)] public string? Content { get; set; }
    [Required, RegularExpression("^sev[0-4]$")] public string Level { get; set; } = "sev4";
}
