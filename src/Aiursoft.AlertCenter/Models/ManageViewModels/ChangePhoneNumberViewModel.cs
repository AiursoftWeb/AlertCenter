using System.ComponentModel.DataAnnotations;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.AlertCenter.Models.ManageViewModels;

public class ChangePhoneNumberViewModel : UiStackLayoutViewModel
{
    public ChangePhoneNumberViewModel()
    {
        PageTitle = "Bind Phone Number";
    }

    [MaxLength(32)]
    [Display(Name = "Phone Number")]
    public string? PhoneNumber { get; set; }

    public string? CurrentPhoneNumber { get; set; }
}
