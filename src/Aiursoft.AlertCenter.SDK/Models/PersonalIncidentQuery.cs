using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.SDK.Models;

public sealed class PersonalIncidentQuery
{
    [Range(1, 1000000)] public int Page { get; set; } = 1;
    public Guid? ApplicationId { get; set; }
    public bool OnlyMine { get; set; }
    public bool AllVisible { get; set; }
    [StringLength(200)] public string? Keyword { get; set; }
    [RegularExpression("^(active|all|new|mitigated|resolved)$")] public string Status { get; set; } = "active";
    [RegularExpression("^sev[0-4]$")] public string? Level { get; set; }
}
