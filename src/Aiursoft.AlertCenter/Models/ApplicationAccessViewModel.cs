using System.ComponentModel.DataAnnotations;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.UiStack.Layout;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Aiursoft.AlertCenter.Models;

public class ApplicationAccessViewModel : UiStackLayoutViewModel
{
    public ApplicationAccessViewModel() => PageTitle = "Application Access Permissions";
    public bool IsOwner { get; set; }
    public SourceApplication Application { get; set; } = null!;
    public List<ApplicationUserAccess> UserAccesses { get; set; } = [];
    public List<ApplicationRoleAccess> RoleAccesses { get; set; } = [];
    public List<SelectListItem> Users { get; set; } = [];
    public List<SelectListItem> Roles { get; set; } = [];
}

public class SaveApplicationAccessRequest
{
    [Required] public Guid ApplicationId { get; set; }
    public string? UserId { get; set; }
    public string? RoleId { get; set; }
    public bool CanViewIncidents { get; set; }
    public bool CanCreateIncidents { get; set; }
    public bool CanUpdateIncidents { get; set; }
    public bool CanManageApiKeys { get; set; }
    public bool CanDeleteIncidents { get; set; }
    public bool CanManageApplication { get; set; }
}
