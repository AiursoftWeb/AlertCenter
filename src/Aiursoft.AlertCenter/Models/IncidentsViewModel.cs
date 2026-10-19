using System.ComponentModel.DataAnnotations;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.AlertCenter.Models;

public class IncidentsViewModel : UiStackLayoutViewModel
{
    public IncidentsViewModel() => PageTitle = "Incidents";
    public IncidentFilter Filter { get; set; } = new();
    public bool HasNextPage { get; set; }
    public int ActiveCount { get; set; }
    public List<SourceApplication> Applications { get; set; } = [];
    public List<Incident> Incidents { get; set; } = [];
    public Incident? Incident { get; set; }
    public string FormattedContext => IncidentContextPresentation.Format(Incident?.ContextJson);
    public List<IncidentActivityViewModel> Activities { get; set; } = [];
    public bool CanDelete { get; set; }
    public bool CanManage { get; set; }
    public IReadOnlyList<OnCallOce> CurrentOces { get; set; } = [];
    public string? CurrentOceError { get; set; }
    public List<IncidentCommentViewModel> Comments { get; set; } = [];
    public int CommentsPage { get; set; } = 1;
    public bool HasMoreActivities { get; set; }
    public bool CanComment { get; set; }
    public string CommentInput { get; set; } = string.Empty;
}

public class IncidentNoteRequest
{
    [Required] public Guid IncidentId { get; set; }
    public Guid Version { get; set; }
    [StringLength(4000)] public string? Note { get; set; }
}

public class IncidentLevelRequest : IncidentNoteRequest
{
    [Required, RegularExpression("^sev[0-4]$")] public string Level { get; set; } = string.Empty;
}

public class IncidentMitigationRequest : IncidentNoteRequest
{
    public bool ResolveImmediately { get; set; }
}

public sealed record IncidentActivityViewModel(IncidentTimelineEntry? Entry, IncidentCommentViewModel? Comment);
