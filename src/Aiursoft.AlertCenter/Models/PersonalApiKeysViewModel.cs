using System.ComponentModel.DataAnnotations;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.AlertCenter.Models;

public sealed class PersonalApiKeyInput
{
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    [Range(1, 365)] public int LifetimeDays { get; set; } = 30;
    public bool CanHandleIncidents { get; set; }
}

public sealed record PersonalApiKeySummary(Guid Id, string Name, bool CanHandleIncidents, DateTime ExpiresAtUtc);

public sealed class PersonalApiKeysViewModel : UiStackLayoutViewModel
{
    public PersonalApiKeysViewModel() => PageTitle = "Personal API Keys";
    public PersonalApiKeyInput Input { get; init; } = new();
    public List<PersonalApiKeySummary> Keys { get; init; } = [];
    public string? NewKey { get; init; }
}
