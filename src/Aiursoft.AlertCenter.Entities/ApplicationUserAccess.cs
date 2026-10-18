namespace Aiursoft.AlertCenter.Entities;

public class ApplicationUserAccess
{
    public Guid ApplicationId { get; set; }
    public SourceApplication Application { get; set; } = null!;
    public string UserId { get; set; } = string.Empty;
    public User User { get; set; } = null!;
    public bool CanViewIncidents { get; set; }
    public bool CanCreateIncidents { get; set; }
    public bool CanUpdateIncidents { get; set; }
    public bool CanManageApiKeys { get; set; }
    public bool CanDeleteIncidents { get; set; }
    public bool CanManageApplication { get; set; }
}
