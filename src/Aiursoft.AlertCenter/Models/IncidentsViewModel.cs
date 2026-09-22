using System.ComponentModel.DataAnnotations;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.AlertCenter.Models;

public class IncidentsViewModel : UiStackLayoutViewModel
{
    public IncidentsViewModel() => PageTitle = "事故处置";
    public IncidentFilter Filter { get; set; } = new();
    public int ActiveCount { get; set; }
    public List<SourceApplication> Applications { get; set; } = [];
    public List<Incident> Incidents { get; set; } = [];
    public Incident? Incident { get; set; }
    public List<IncidentTimelineEntry> TimelineEntries { get; set; } = [];
    public List<ReportedMessage> Messages { get; set; } = [];
    public bool CanManage { get; set; }
}

public class IncidentNoteRequest
{
    [Required] public Guid IncidentId { get; set; }
    [StringLength(4000)] public string? Note { get; set; }
}
