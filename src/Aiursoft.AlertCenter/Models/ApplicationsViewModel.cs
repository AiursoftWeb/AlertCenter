using System.ComponentModel.DataAnnotations;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.AlertCenter.Models;

public class ApplicationsViewModel : UiStackLayoutViewModel
{
    public ApplicationsViewModel() { PageTitle = "Alert Center"; }
    public List<SourceApplication> Applications { get; set; } = [];
    public string? ApiKey { get; set; }
    public bool ApiKeyWasReset { get; set; }
    public string? ApplicationSearchKeyword { get; set; }
    public CreateApplicationRequest ApplicationInput { get; set; } = new();
    public List<OnCallGroup> OnCallGroups { get; set; } = [];
    public bool CanManageApplications { get; set; }
    public HashSet<Guid> ManageableApplicationIds { get; set; } = [];
}

public class CreateApplicationRequest
{
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    public Guid? OnCallGroupId { get; set; }
}
