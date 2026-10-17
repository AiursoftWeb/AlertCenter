using System.Net.Mail;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Configuration;

public sealed class EmailSettings
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = string.Empty;
    public string PublicBaseUrl { get; set; } = "https://alertcenter.aiursoft.com";
    public bool RequireTls { get; set; } = true;

    public bool TryValidate(out string? error, IStringLocalizer? localizer = null)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65535 || !IsSingleAddress(From))
        {
            error = (localizer is null ? "Please configure Email:Host, Email:Port, and Email:From." : localizer["Please configure Email:Host, Email:Port, and Email:From."].Value);
            return false;
        }
        if (string.IsNullOrWhiteSpace(UserName) != string.IsNullOrWhiteSpace(Password))
        {
            error = (localizer is null ? "Email:UserName and Email:Password must both be configured or both left empty." : localizer["Email:UserName and Email:Password must both be configured or both left empty."].Value);
            return false;
        }
        return true;
    }

    private static bool IsSingleAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) return false;
        try
        {
            var address = new MailAddress(value);
            return string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
