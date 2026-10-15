namespace Aiursoft.AlertCenter.Entities;

public class PushChannelUser
{
    public Guid ChannelId { get; set; }
    public PushChannel Channel { get; set; } = null!;
    public string UserId { get; set; } = string.Empty;
    public User User { get; set; } = null!;
}
