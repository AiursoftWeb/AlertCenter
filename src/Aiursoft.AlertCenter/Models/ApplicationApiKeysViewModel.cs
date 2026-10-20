using System.ComponentModel.DataAnnotations;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.AlertCenter.Models;

public class ApplicationApiKeysViewModel : UiStackLayoutViewModel
{
    public ApplicationApiKeysViewModel() => PageTitle = "API Key Management";
    public SourceApplication Application { get; set; } = null!;
    public List<ApplicationApiKey> Keys { get; set; } = [];
    public string? NewKey { get; set; }
}

public class ApplicationApiKeyEditViewModel : UiStackLayoutViewModel
{
    public ApplicationApiKeyEditViewModel() => PageTitle = "API Key";
    public Guid ApplicationId { get; set; }
    public Guid? Id { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public bool CanQueryIncidents { get; set; }
    public bool CanCreateIncidents { get; set; }
    public bool CanMitigateIncidents { get; set; }
    public bool CanReopenIncidents { get; set; }
    public bool CanPatchIncidentMetadata { get; set; }
    public bool CanResolveIncidents { get; set; }
    public bool CanChangeIncidentSeverity { get; set; }
}
