using Microsoft.AspNetCore.Identity;

namespace Aiursoft.AlertCenter.Entities;

public class ApplicationRoleAccess
{
    public Guid ApplicationId { get; set; }
    public SourceApplication Application { get; set; } = null!;
    public string RoleId { get; set; } = string.Empty;
    public IdentityRole Role { get; set; } = null!;
    public bool CanViewIncidents { get; set; }
    public bool CanCreateIncidents { get; set; }
    public bool CanUpdateIncidents { get; set; }
    public bool CanManageApiKeys { get; set; }
    public bool CanDeleteIncidents { get; set; }
    public bool CanManageApplication { get; set; }
}
