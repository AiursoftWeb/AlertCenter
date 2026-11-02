using System.ComponentModel.DataAnnotations;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.AlertCenter.Models;

public class RenameApplicationViewModel : UiStackLayoutViewModel
{
    public RenameApplicationViewModel() { PageTitle = "Rename application"; }
    public Guid Id { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
}
