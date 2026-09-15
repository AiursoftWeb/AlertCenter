namespace Aiursoft.AlertCenter.Entities;

public class ApplicationUserAccess
{
    public Guid ApplicationId { get; set; }
    public SourceApplication Application { get; set; } = null!;
    public string UserId { get; set; } = string.Empty;
    public User User { get; set; } = null!;
    public bool CanManage { get; set; }
}
