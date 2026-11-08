using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aiursoft.AlertCenter.Entities;

namespace Aiursoft.AlertCenter.Services;

public static class OnCallCalendar
{
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string Render(IEnumerable<OnCallShift> shifts, string publicBaseUrl, DateTime now)
    {
        var output = new StringBuilder();
        void Line(string value)
        {
            var bytes = 0;
            foreach (var rune in value.EnumerateRunes())
            {
                if (bytes + rune.Utf8SequenceLength > 75)
                {
                    output.Append("\r\n ");
                    bytes = 1;
                }
                output.Append(rune.ToString());
                bytes += rune.Utf8SequenceLength;
            }
            output.Append("\r\n");
        }
        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//Aiursoft//AlertCenter On-call//EN");
        Line("CALSCALE:GREGORIAN");
        foreach (var shift in shifts)
        {
            Line("BEGIN:VEVENT");
            Line($"UID:{shift.Id:D}@alertcenter.aiursoft.com");
            Line($"DTSTAMP:{Utc(now)}");
            Line($"DTSTART:{Utc(shift.StartsAtUtc)}");
            Line($"DTEND:{Utc(shift.EndsAtUtc)}");
            Line($"SUMMARY:{Escape($"On-call: {shift.Group.Name}")}");
            Line($"DESCRIPTION:{Escape(string.Join(", ", shift.Members.OrderBy(member => member.UserId).Select(member => member.User.DisplayName)) + "\n" + publicBaseUrl.TrimEnd('/') + "/OnCall/ScheduleDetails/" + shift.GroupId)}");
            Line("END:VEVENT");
        }
        Line("END:VCALENDAR");
        return output.ToString();
    }

    private static string Utc(DateTime value) => value.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\r\n", "\n").Replace("\r", "\n")
        .Replace("\n", "\\n").Replace(";", "\\;").Replace(",", "\\,");
}
