using Aiursoft.AlertCenter.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.AlertCenter.Models.DashboardViewModels;

public class IndexViewModel : UiStackLayoutViewModel
{
    public IndexViewModel()
    {
        PageTitle = "Console Overview";
    }

    public int TotalApplications { get; set; }
    public int TotalIncidents { get; set; }
    public int ActiveIncidents { get; set; }
    public int MyIncidents { get; set; }
    public int MyActiveIncidents { get; set; }
    public int TotalDeliveries { get; set; }
    public int SuccessfulDeliveries { get; set; }
    public int UnreadNotifications { get; set; }
    public int ChannelsCount { get; set; }
    public int Sev0Count { get; set; }
    public int Sev1Count { get; set; }
    public int Sev2Count { get; set; }
    public int Sev3Count { get; set; }
    public int Sev4Count { get; set; }
    public List<Incident> LatestIncidents { get; set; } = [];
    public List<Incident> RecentIncidents { get; set; } = [];
    public bool CanManageApplications { get; set; }
}
