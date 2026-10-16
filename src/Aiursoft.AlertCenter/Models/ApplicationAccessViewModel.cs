using System.ComponentModel.DataAnnotations;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.UiStack.Layout;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Aiursoft.AlertCenter.Models;

public class ApplicationAccessViewModel : UiStackLayoutViewModel
{
    public ApplicationAccessViewModel() => PageTitle = "Application Access Permissions";
    public SourceApplication Application { get; set; } = null!;
    public List<ApplicationUserAccess> UserAccesses { get; set; } = [];
    public List<ApplicationRoleAccess> RoleAccesses { get; set; } = [];
    public List<SelectListItem> Users { get; set; } = [];
    public List<SelectListItem> Roles { get; set; } = [];
}

public class SaveApplicationAccessRequest
{
    [Required] public Guid ApplicationId { get; set; }
    public string[] ViewUserIds { get; set; } = [];
    public string[] ManageUserIds { get; set; } = [];
    public string[] ViewRoleIds { get; set; } = [];
    public string[] ManageRoleIds { get; set; } = [];
}
