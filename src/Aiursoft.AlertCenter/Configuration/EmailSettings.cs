using System.Net.Mail;

namespace Aiursoft.AlertCenter.Configuration;

public sealed class EmailSettings
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = string.Empty;
    public bool RequireTls { get; set; } = true;

    public bool TryValidate(out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65535 || !IsSingleAddress(From))
        {
            error = "请配置 Email:Host、Email:Port 和 Email:From。";
            return false;
        }
        if (string.IsNullOrWhiteSpace(UserName) != string.IsNullOrWhiteSpace(Password))
        {
            error = "Email:UserName 和 Email:Password 必须同时配置或同时留空。";
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
