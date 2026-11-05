using Aiursoft.AlertCenter.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.AlertCenter.Models;

public class IncidentSearchViewModel : UiStackLayoutViewModel
{
    public IncidentSearchViewModel() => PageTitle = "Incident search";
    public const int PageSize = 10;
    public string Query { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public int TotalCount { get; set; }
    public bool UsedAiSearch { get; set; }
    public List<Incident> Incidents { get; set; } = [];
    public bool HasNextPage => (long)Page * PageSize < TotalCount;
}
