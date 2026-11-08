using System.Globalization;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public sealed class OnCallGroupFormService(IOnCallScheduleGenerator generator, IStringLocalizer localizer)
{
    public static string ToLocal(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone)
            .ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

    public static bool IsValidAnchor(DateTime anchor) => anchor.Kind != DateTimeKind.Local &&
        anchor >= DateTime.MinValue.AddDays(28) && anchor <= DateTime.MaxValue.AddDays(-28);

    public static DateTime NextWholeHour(TimeZoneInfo zone, DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        local = DateTime.SpecifyKind(local.Date.AddHours(local.Hour), DateTimeKind.Unspecified);
        while (true)
        {
            if (!zone.IsInvalidTime(local))
            {
                var utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
                if (utc > nowUtc) return utc;
            }
            local = local.AddHours(1);
        }
    }

    public static bool IsUnaligned(OnCallRule? rule, TimeZoneInfo zone, DateTime anchorUtc) =>
        rule?.HandoverPoints.Count > 0 &&
        OnCallScheduleGenerator.NextHandover(rule.HandoverPoints, zone, anchorUtc.AddTicks(-1)) != anchorUtc;

    public OnCallGroupFormPreviewResult Preview(OnCallRule? rule, TimeZoneInfo zone, DateTime? anchorUtc,
        IReadOnlyList<string> userIds, DateTime nowUtc, bool anchorLocked = false, OnCallShift? lastShift = null)
    {
        var suggestions = rule?.HandoverPoints.Count > 0
            ? rule.HandoverPoints.OrderBy(point => point.DayOfWeek).ThenBy(point => point.MinuteOfDay)
                .DistinctBy(point => (point.DayOfWeek, point.MinuteOfDay))
                .Select(point => OnCallScheduleGenerator.NextHandover([point], zone, nowUtc)).ToList()
            : [NextWholeHour(zone, nowUtc)];
        var anchor = DateTime.SpecifyKind(anchorUtc ?? suggestions[0], DateTimeKind.Utc);
        var result = new OnCallGroupFormPreviewResult
        {
            AnchorLocal = ToLocal(anchor, zone), SuggestedAnchorLocal = ToLocal(suggestions[0], zone),
            AnchorLocked = anchorLocked, Suggestions = suggestions.Select(value => ToLocal(value, zone)).ToList()
        };
        if (rule == null) return result;
        if (IsUnaligned(rule, zone, anchor))
            result.Warnings.Add(localizer["The start time does not match a weekly handover point; the first shift will be a partial segment."].Value);
        if (userIds.Count == 0)
        {
            result.Errors.Add(localizer["Add rotation queue members to preview shifts."].Value);
            return result;
        }
        try
        {
            var start = DateTime.SpecifyKind(lastShift?.EndsAtUtc ?? anchor, DateTimeKind.Utc);
            var history = LastShiftHistory(rule, userIds, lastShift);
            result.Slots = generator.GenerateRange(rule, anchor, userIds, start, 4, zone, history)
                .GroupBy(shift => (shift.StartsAtUtc, shift.EndsAtUtc))
                .Select(batch => new OnCallSlotRequest
                {
                    StartsAtUtc = batch.Key.StartsAtUtc, EndsAtUtc = batch.Key.EndsAtUtc,
                    StartLocal = ToLocal(batch.Key.StartsAtUtc, zone), EndLocal = ToLocal(batch.Key.EndsAtUtc, zone),
                    UserIds = batch.Select(shift => shift.UserId).ToList()
                }).ToList();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            result.Errors.Add(ex.Message);
        }
        catch (OverflowException)
        {
            result.Errors.Add(localizer["The team preview request format is invalid."].Value);
        }
        return result;
    }

    private static List<GeneratedOnCallShift> LastShiftHistory(OnCallRule rule, IReadOnlyList<string> userIds, OnCallShift? last)
    {
        if (last == null) return [];
        var ids = userIds.ToArray();
        var history = last.Members.OrderBy(member => Array.IndexOf(ids, member.UserId))
            .Select(member => new GeneratedOnCallShift(member.UserId, last.StartsAtUtc, last.EndsAtUtc)).ToList();
        if (last.Members.Count == 0) history.Add(new(string.Empty, last.StartsAtUtc, last.EndsAtUtc));
        // Match automatic extension: preserve the cursor only while its assignments still match the current queue.
        if (last.RotationPosition is { } cursor && last.Members.Count > 0)
        {
            var expected = Enumerable.Range(0, last.Members.Count).Select(index =>
                new GeneratedOnCallShift(ids[OnCallScheduleGenerator.QueueIndex(rule, ids.Length, cursor + index)],
                    last.StartsAtUtc, last.EndsAtUtc, RotationPosition: cursor + index)).ToList();
            if (expected.Select(shift => shift.UserId).ToHashSet(StringComparer.Ordinal).SetEquals(last.Members.Select(member => member.UserId)))
                history = expected;
        }
        return history;
    }
}
