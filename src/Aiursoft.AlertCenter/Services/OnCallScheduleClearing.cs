using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;

namespace Aiursoft.AlertCenter.Services;

internal static class OnCallScheduleClearing
{
    internal static OnCallSlotRequest Slot(OnCallShift shift) => new()
    {
        Id = shift.Id, StartsAtUtc = DateTime.SpecifyKind(shift.StartsAtUtc, DateTimeKind.Utc),
        EndsAtUtc = DateTime.SpecifyKind(shift.EndsAtUtc, DateTimeKind.Utc), Source = shift.Source,
        RotationPosition = shift.RotationPosition, UserIds = shift.Members.Select(member => member.UserId).ToList()
    };

    internal static bool SameAssignment(OnCallSlotRequest first, OnCallSlotRequest second) =>
        first.StartsAtUtc == second.StartsAtUtc && End(first) == End(second) &&
        first.UserIds.ToHashSet(StringComparer.Ordinal).SetEquals(second.UserIds);

    internal static DateTime End(OnCallSlotRequest slot) => slot.EndsAtUtc ?? slot.StartsAtUtc.AddHours(8);

    internal static List<OnCallSlotRequest> Apply(IEnumerable<OnCallSlotRequest> slots, ClearOnCallScheduleRequest range,
        List<OnCallSlotRequest>? affected = null)
    {
        var now = range.ClearedAtUtc!.Value;
        var result = new List<OnCallSlotRequest>();
        foreach (var slot in slots)
        {
            var end = End(slot);
            var left = range.StartUtc;
            if (!range.IncludeHistory || slot.StartsAtUtc < now && end > now)
                left = left > now ? left : now;
            var right = range.EndUtc;
            if ((!range.IncludeManual && slot.Source != OnCallShiftSource.Generated) ||
                end <= left || slot.StartsAtUtc >= right || right <= left)
            {
                result.Add(slot);
                continue;
            }
            affected?.Add(slot);
            result.AddRange(Outside(slot, left, right));
        }
        return result;
    }

    internal static IEnumerable<OnCallSlotRequest> Outside(OnCallSlotRequest slot, DateTime left, DateTime right, bool replacement = false)
    {
        if (slot.StartsAtUtc < left) yield return Fragment(slot, slot.StartsAtUtc, left, slot.Id, replacement);
        var end = End(slot);
        if (end > right)
            yield return Fragment(slot, right, end, !replacement && slot.StartsAtUtc >= left ? slot.Id : null, replacement);
    }

    private static OnCallSlotRequest Fragment(OnCallSlotRequest slot, DateTime start, DateTime end, Guid? id, bool replacement) => new()
    {
        Id = id, StartsAtUtc = start, EndsAtUtc = end, Source = slot.Source,
        FragmentOf = replacement ? slot.Id : slot.FragmentOf,
        RotationPosition = slot.RotationPosition, UserIds = slot.UserIds.ToList()
    };

    internal static List<(DateTime Start, DateTime End)> Gaps(IEnumerable<OnCallSlotRequest> slots, DateTime start, DateTime end)
    {
        var result = new List<(DateTime, DateTime)>();
        var cursor = start;
        foreach (var slot in slots.Where(slot => End(slot) > start && slot.StartsAtUtc < end).OrderBy(slot => slot.StartsAtUtc))
        {
            if (slot.StartsAtUtc > cursor) result.Add((cursor, slot.StartsAtUtc));
            if (End(slot) > cursor) cursor = End(slot);
        }
        if (cursor < end) result.Add((cursor, end));
        return result;
    }
}
