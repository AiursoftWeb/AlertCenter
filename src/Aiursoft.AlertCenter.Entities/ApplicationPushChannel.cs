namespace Aiursoft.AlertCenter.Entities;

public class ApplicationPushChannel
{
    public Guid ApplicationId { get; set; }
    public SourceApplication Application { get; set; } = null!;
    public Guid ChannelId { get; set; }
    public PushChannel Channel { get; set; } = null!;
}
