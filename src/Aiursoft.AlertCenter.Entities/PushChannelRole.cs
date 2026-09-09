using Microsoft.AspNetCore.Identity;

namespace Aiursoft.AlertCenter.Entities;

public class PushChannelRole
{
    public Guid ChannelId { get; set; }
    public PushChannel Channel { get; set; } = null!;
    public string RoleId { get; set; } = string.Empty;
    public IdentityRole Role { get; set; } = null!;
}
