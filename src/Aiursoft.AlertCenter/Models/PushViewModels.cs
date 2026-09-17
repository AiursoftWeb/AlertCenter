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
    public InboxFilter InboxFilter { get; set; } = new();
    public List<SourceApplication> Applications { get; set; } = [];
    public Guid? ChannelId { get; set; }
    public string? Status { get; set; }
    public Guid? ApplicationId { get; set; }
    public bool IsAdministrator { get; set; }
    public HashSet<Guid> ManageableApplicationIds { get; set; } = [];
}

public class ApplicationPushBindingsRequest
{
    public Guid[] Sev1ChannelIds { get; set; } = [];
    public Guid[] Sev2ChannelIds { get; set; } = [];
    public Guid[] Sev3ChannelIds { get; set; } = [];
    public Guid[] Sev4ChannelIds { get; set; } = [];
}

public class RecipientSelection
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? UserName { get; init; }
}

public class PushChannelEditViewModel : UiStackLayoutViewModel
{
    public PushChannelEditViewModel() { PageTitle = "配置推送渠道"; }
    public string[] RecipientUserIds { get; set; } = [];
    public string[] RecipientRoleIds { get; set; } = [];
    public string[] BindingUserIds { get; set; } = [];
    public string[] BindingRoleIds { get; set; } = [];
    public List<Microsoft.AspNetCore.Mvc.Rendering.SelectListItem> AvailableRoles { get; set; } = [];
    public List<Microsoft.AspNetCore.Mvc.Rendering.SelectListItem> AvailableUsers { get; set; } = [];
    public List<RecipientSelection> SelectedUsers { get; set; } = [];
    public List<RecipientSelection> SelectedRoles { get; set; } = [];
    public List<RecipientSelection> SelectedBindingUsers { get; set; } = [];
    public List<RecipientSelection> SelectedBindingRoles { get; set; } = [];
    public Guid? Id { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    [Required, StringLength(40)] public string Type { get; set; } = string.Empty;
    [StringLength(2048)] public string? Endpoint { get; set; }
    [StringLength(8000)] public string? WebhookHeadersJson { get; set; }
    [StringLength(16000)] public string? WebhookPayloadTemplate { get; set; }
    [StringLength(8000)] public string? EmailRecipients { get; set; }
    public bool IsEnabled { get; set; } = true;
    public List<ChannelTypeDefinition> ChannelTypes { get; set; } = [];
}
