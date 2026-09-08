using System.ComponentModel.DataAnnotations;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.AlertCenter.Models;

public class PushViewModel : UiStackLayoutViewModel
{
    public PushViewModel() { PageTitle = "推送中心"; }
    public List<PushChannel> Channels { get; set; } = [];
    public List<ChannelTypeDefinition> ChannelTypes { get; set; } = [];
    public List<PushDelivery> Deliveries { get; set; } = [];
    public List<InAppNotification> Notifications { get; set; } = [];
    public SourceApplication? Application { get; set; }
    public ApplicationPushBindingsRequest Bindings { get; set; } = new();
    public PushDelivery? Delivery { get; set; }
    public int Page { get; set; } = 1;
    public bool HasNextPage { get; set; }
    public int UnreadCount { get; set; }
    public bool UnreadOnly { get; set; }
    public Guid? ChannelId { get; set; }
    public string? Status { get; set; }
}

public class ApplicationPushBindingsRequest
{
    public Guid[] Sev1ChannelIds { get; set; } = [];
    public Guid[] Sev2ChannelIds { get; set; } = [];
    public Guid[] Sev3ChannelIds { get; set; } = [];
    public Guid[] Sev4ChannelIds { get; set; } = [];
}

public class PushChannelEditViewModel : UiStackLayoutViewModel
{
    public PushChannelEditViewModel() { PageTitle = "配置推送渠道"; }
    public Guid? Id { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    [Required, StringLength(40)] public string Type { get; set; } = string.Empty;
    [StringLength(2048)] public string? Endpoint { get; set; }
    public bool IsEnabled { get; set; } = true;
    public List<ChannelTypeDefinition> ChannelTypes { get; set; } = [];
}
