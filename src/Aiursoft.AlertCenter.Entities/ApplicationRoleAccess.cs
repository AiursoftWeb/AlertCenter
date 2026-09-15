using Microsoft.AspNetCore.Identity;

namespace Aiursoft.AlertCenter.Entities;

public class ApplicationRoleAccess
{
    public Guid ApplicationId { get; set; }
    public SourceApplication Application { get; set; } = null!;
    public string RoleId { get; set; } = string.Empty;
    public IdentityRole Role { get; set; } = null!;
    public bool CanManage { get; set; }
}
