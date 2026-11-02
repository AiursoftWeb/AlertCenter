using System.ComponentModel.DataAnnotations;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.AlertCenter.Models;

public class RenameApplicationViewModel : UiStackLayoutViewModel
{
    public RenameApplicationViewModel() { PageTitle = "Rename application"; }
    public Guid Id { get; set; }
    [Required(ErrorMessage = "Enter an application name.")]
    [StringLength(100, ErrorMessage = "Application names must be 100 characters or fewer.")]
    public string Name { get; set; } = string.Empty;
}
