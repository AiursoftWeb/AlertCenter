using Aiursoft.AlertCenter.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.AlertCenter.Models;

public class ApplicationOnCallViewModel : UiStackLayoutViewModel
{
    public ApplicationOnCallViewModel() { PageTitle = "应用值班设置"; }
    public SourceApplication Application { get; set; } = null!;
    public List<OnCallGroup> Groups { get; set; } = [];
    public Guid? OnCallGroupId { get; set; }
}

public class SaveApplicationOnCallRequest
{
    public Guid ApplicationId { get; set; }
    public Guid? OnCallGroupId { get; set; }
}
