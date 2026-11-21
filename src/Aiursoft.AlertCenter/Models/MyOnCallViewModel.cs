using Aiursoft.AlertCenter.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.AlertCenter.Models;

public class MyOnCallViewModel : UiStackLayoutViewModel
{
    public MyOnCallViewModel() { PageTitle = "My on-call"; }
    public List<OnCallShift> Upcoming { get; set; } = [];
    public List<OnCallShift> Current { get; set; } = [];
    public OnCallShift? Next { get; set; }
    public List<OnCallShift> History { get; set; } = [];
    public int HistoryPage { get; set; }
    public bool HasMoreHistory { get; set; }
    public double PastHours { get; set; }
    public double FutureHours { get; set; }
    public List<string> Warnings { get; set; } = [];
    public bool HasSubscription { get; set; }
    public string? SubscriptionUrl { get; set; }
}
