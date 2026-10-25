using System.ComponentModel.DataAnnotations;
using Aiursoft.AiurProtocol.Models;

namespace Aiursoft.AlertCenter.SDK.Models;

public sealed class MobileConfigurationResponse : AiurResponse
{
    public string AuthenticationMode { get; set; } = string.Empty;
    public bool AllowRegistration { get; set; }
    public string Authority { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = [];
}

public sealed class LocalAuthenticationResponse : AiurResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class LocalLoginRequest
{
    [Required, StringLength(256, MinimumLength = 1)]
    public string EmailOrUserName { get; set; } = string.Empty;

    [Required, StringLength(256, MinimumLength = 6)]
    public string Password { get; set; } = string.Empty;
}

public sealed class LocalRegistrationRequest
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(256, MinimumLength = 6)]
    public string Password { get; set; } = string.Empty;
}

public sealed class MobileUserResponse : AiurResponse
{
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class MobileApplicationsResponse : AiurResponse
{
    public List<MobileApplication> Applications { get; set; } = [];
}

public sealed class MobileApplication
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool CanView { get; set; }
    public bool CanCreate { get; set; }
    public bool CanUpdate { get; set; }
}

public sealed class MobileIncidentQuery
{
    [Range(1, 1000000)] public int Page { get; set; } = 1;
    public Guid? ApplicationId { get; set; }
    public bool OnlyMine { get; set; }
    [StringLength(200)] public string? Keyword { get; set; }
    [RegularExpression("^(all|new|mitigated|resolved)$")] public string Status { get; set; } = "new";
    [RegularExpression("^(sev[0-4]|lte-sev[1-4])$")] public string? Level { get; set; }
    public bool? Acknowledged { get; set; }
}

public sealed class MobileIncidentsResponse : AiurResponse
{
    public List<MobileIncident> Incidents { get; set; } = [];
    public int Page { get; set; }
    public bool HasNextPage { get; set; }
}

public sealed class MobileIncidentResponse : AiurResponse
{
    public MobileIncident Incident { get; set; } = new();
    public List<MobileActivity> Activities { get; set; } = [];
    public int Page { get; set; }
    public bool HasNextPage { get; set; }
    public List<string> OriginalOces { get; set; } = [];
    public List<string> CurrentOces { get; set; } = [];
    public string? CurrentOceError { get; set; }
}

public sealed class MobileIncident
{
    public Guid Id { get; set; }
    public Guid Version { get; set; }
    public string ShortId { get; set; } = string.Empty;
    public Guid ApplicationId { get; set; }
    public string ApplicationName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Content { get; set; }
    public string? ContextJson { get; set; }
    public string? MetadataJson { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? OwnerName { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }
    public DateTimeOffset? MitigatedAtUtc { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
    public bool CanManage { get; set; }
}

public sealed class MobileActivity
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string ActorName { get; set; } = string.Empty;
    public string? Note { get; set; }
    public string? PreviousStatus { get; set; }
    public string? NewStatus { get; set; }
    public string? PreviousLevel { get; set; }
    public string? NewLevel { get; set; }
    public string? NewOwnerName { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class MobileIncidentAction
{
    public Guid Version { get; set; }
    [StringLength(4000)] public string? Note { get; set; }
    [RegularExpression("^sev[0-4]$")] public string? Level { get; set; }
}

public sealed class MobileCommentRequest
{
    [Required, StringLength(4000)] public string Markdown { get; set; } = string.Empty;
}

public sealed class MobileCreateIncidentRequest
{
    public Guid ApplicationId { get; set; }
    [Required, StringLength(200)] public string Title { get; set; } = string.Empty;
    [StringLength(4000)] public string? Content { get; set; }
    [Required, RegularExpression("^sev[0-4]$")] public string Level { get; set; } = "sev4";
}

public sealed class MobileCreatedIncidentResponse : AiurResponse
{
    public Guid IncidentId { get; set; }
    public bool CanView { get; set; }
}
