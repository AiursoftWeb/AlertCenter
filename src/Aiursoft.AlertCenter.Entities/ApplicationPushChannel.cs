namespace Aiursoft.AlertCenter.Entities;

public class ApplicationPushChannel
{
    public Guid ApplicationId { get; set; }
    public SourceApplication Application { get; set; } = null!;
    public Guid ChannelId { get; set; }
    public PushChannel Channel { get; set; } = null!;
    public bool ReceiveSev0 { get; set; } = true;
    public bool ReceiveSev1 { get; set; } = true;
    public bool ReceiveSev2 { get; set; } = true;
    public bool ReceiveSev3 { get; set; } = true;
    public bool ReceiveSev4 { get; set; }
}
