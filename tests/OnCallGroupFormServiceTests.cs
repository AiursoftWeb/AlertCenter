using Aiursoft.AlertCenter.Controllers;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class OnCallGroupFormServiceTests
{
    private static OnCallScheduleGenerator Generator() => new(TestLocalization.For<OnCallScheduleGenerator>());
    private static OnCallGroupFormService Service() => new(Generator(), TestLocalization.For<OnCallController>());

    [TestMethod]
    public void WeeklySuggestionsUseCanonicalTemplateOrderAndPreviewUsesSameGeneratorAndQueue()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        var rule = new OnCallRule
        {
            Name = "Weekly", HandoverPoints =
            [
                new() { DayOfWeek = DayOfWeek.Friday, MinuteOfDay = 1320 },
                new() { DayOfWeek = DayOfWeek.Monday, MinuteOfDay = 1320 }
            ]
        };
        var now = new DateTime(2026, 11, 9, 14, 0, 0, DateTimeKind.Utc);
        var preview = Service().Preview(rule, zone, null, ["c", "a", "b"], now);
        Assert.AreEqual("2026-11-16T22:00", preview.SuggestedAnchorLocal);
        Assert.AreEqual(preview.SuggestedAnchorLocal, preview.AnchorLocal);
        CollectionAssert.AreEqual(new[] { "2026-11-16T22:00", "2026-11-13T22:00" }, preview.Suggestions);
        Assert.AreEqual(4, preview.Slots.Count);
        Assert.AreEqual(new DateTime(2026, 11, 16, 14, 0, 0, DateTimeKind.Utc), preview.Slots[0].StartsAtUtc);
        Assert.AreEqual("2026-11-20T22:00", preview.Slots[0].EndLocal);
        CollectionAssert.AreEqual(new[] { "c", "a", "b", "c" }, preview.Slots.Select(slot => slot.UserIds.Single()).ToArray());
        var anchor = OnCallScheduleService.LocalToUtc(preview.AnchorLocal, zone);
        var generated = Generator().GenerateRange(rule, anchor, ["c", "a", "b"], anchor, 4, zone);
        CollectionAssert.AreEqual(generated.Select(shift => (shift.StartsAtUtc, shift.EndsAtUtc, shift.UserId)).ToArray(),
            preview.Slots.Select(slot => (slot.StartsAtUtc, slot.EndsAtUtc!.Value, slot.UserIds.Single())).ToArray());
        Assert.AreEqual(0, preview.Warnings.Count);
        Assert.AreEqual(0, preview.Errors.Count);
    }

    [TestMethod]
    public void FixedPreviewStartsAtGroupAnchorAndHonorsStaffingAndQueueOrder()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        var anchor = new DateTime(2026, 11, 9, 14, 0, 0, DateTimeKind.Utc);
        var rule = new OnCallRule { ShiftMinutes = 90, StaffPerShift = 2 };
        var preview = Service().Preview(rule, zone, anchor, ["b", "c", "a"], anchor.AddDays(8), true);
        Assert.IsTrue(preview.AnchorLocked);
        Assert.AreEqual(anchor, preview.Slots[0].StartsAtUtc);
        Assert.AreEqual(4, preview.Slots.Count);
        CollectionAssert.AreEqual(new[] { "b", "c", "a", "b", "c", "a", "b", "c" },
            preview.Slots.SelectMany(slot => slot.UserIds).ToArray());
        Assert.IsTrue(preview.Slots.All(slot => slot.EndsAtUtc - slot.StartsAtUtc == TimeSpan.FromMinutes(90)));
    }

    [TestMethod]
    [DataRow("Asia/Kathmandu", "2026-11-09T14:30:00Z", "2026-11-09T21:00")]
    [DataRow("Asia/Shanghai", "2026-11-09T14:00:00Z", "2026-11-09T23:00")]
    [DataRow("America/New_York", "2026-03-08T06:30:00Z", "2026-03-08T03:00")]
    [DataRow("America/New_York", "2026-11-01T05:30:00Z", "2026-11-01T01:00")]
    [DataRow("Australia/Lord_Howe", "2026-10-03T15:00:00Z", "2026-10-04T03:00")]
    public void FixedAndNoTemplateSuggestionsUseFutureWholeTeamLocalHour(string zoneId, string nowText, string expected)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var now = DateTime.Parse(nowText, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        var manual = Service().Preview(null, zone, null, [], now);
        Assert.AreEqual(expected, manual.SuggestedAnchorLocal);
        Assert.AreEqual(0, manual.Slots.Count);
        Assert.AreEqual(0, manual.Errors.Count);
        Assert.IsTrue(OnCallScheduleService.LocalToUtc(manual.AnchorLocal, zone) > now);
        var fixedTemplate = Service().Preview(new OnCallRule(), zone, null, ["a"], now);
        Assert.AreEqual(expected, fixedTemplate.SuggestedAnchorLocal);
        Assert.AreEqual(4, fixedTemplate.Slots.Count);
    }

    [TestMethod]
    public void WeeklySuggestionsUseGeneratorsDstHandlingAndManualStartOnlyWarns()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var rule = new OnCallRule { HandoverPoints = [new() { DayOfWeek = DayOfWeek.Sunday, MinuteOfDay = 150 }] };
        var now = new DateTime(2026, 3, 8, 5, 0, 0, DateTimeKind.Utc);
        var automatic = Service().Preview(rule, zone, null, ["a"], now);
        Assert.AreEqual("2026-03-08T03:00", automatic.AnchorLocal);
        Assert.AreEqual(0, automatic.Warnings.Count);
        var manual = Service().Preview(rule, zone, now, ["a"], now);
        Assert.AreEqual(4, manual.Slots.Count);
        Assert.AreEqual(1, manual.Warnings.Count);
        Assert.AreEqual(0, manual.Errors.Count);
    }

    [TestMethod]
    public void SavedPreviewContinuesLastShiftCursorAndFallsBackForDepartedMembers()
    {
        var now = new DateTime(2026, 11, 9, 14, 0, 0, DateTimeKind.Utc);
        var rule = new OnCallRule { ShiftMinutes = 60, SwapAlternateRounds = true };
        var last = new OnCallShift
        {
            StartsAtUtc = now, EndsAtUtc = now.AddHours(1), RotationPosition = 4,
            Members = [new() { UserId = "b" }]
        };
        var preview = Service().Preview(rule, TimeZoneInfo.Utc, now.AddDays(-7), ["a", "b", "c", "d"], now, true, last);
        Assert.AreEqual(last.EndsAtUtc, preview.Slots[0].StartsAtUtc);
        CollectionAssert.AreEqual(new[] { "a", "d", "c", "a" }, preview.Slots.Select(slot => slot.UserIds.Single()).ToArray());
        var expected = Generator().GenerateRange(rule, now.AddDays(-7), ["a", "b", "c", "d"], last.EndsAtUtc, 4,
            TimeZoneInfo.Utc, [new("b", last.StartsAtUtc, last.EndsAtUtc, 4)]);
        CollectionAssert.AreEqual(expected.Select(shift => shift.UserId).ToArray(), preview.Slots.Select(slot => slot.UserIds.Single()).ToArray());
        last.Members = [new() { UserId = "departed" }];
        preview = Service().Preview(rule, TimeZoneInfo.Utc, now.AddDays(-7), ["a", "b", "c", "d"], now, true, last);
        CollectionAssert.AreEqual(new[] { "a", "b", "c", "d" }, preview.Slots.Select(slot => slot.UserIds.Single()).ToArray());
        last.Members = [];
        preview = Service().Preview(rule, TimeZoneInfo.Utc, now.AddDays(-7), ["a", "b", "c", "d"], now, true, last);
        CollectionAssert.AreEqual(new[] { "a", "b", "c", "d" }, preview.Slots.Select(slot => slot.UserIds.Single()).ToArray());
    }

    [TestMethod]
    public void FullStaffPreviewContinuesSkippedDuplicateQueuePositionsExactlyLikeMaintenance()
    {
        var now = new DateTime(2026, 11, 9, 14, 0, 0, DateTimeKind.Utc);
        var rule = new OnCallRule { ShiftMinutes = 60, StaffPerShift = 4, SwapAlternateRounds = true };
        var last = new OnCallShift { StartsAtUtc = now, EndsAtUtc = now.AddHours(1), RotationPosition = 1,
            Members = new[] { "b", "c", "d", "a" }.Select(id => new OnCallShiftMember { UserId = id }).ToList() };
        var group = new OnCallGroup { Rule = rule,
            Members = new[] { "a", "b", "c", "d" }.Select((id, index) => new OnCallGroupMember { UserId = id, RotationOrder = index }).ToList() };
        var expected = Generator().GenerateRange(rule, now, ["a", "b", "c", "d"], last.EndsAtUtc, 4,
            TimeZoneInfo.Utc, OnCallMaintenanceWorker.RotationHistory(group, last));
        var preview = Service().Preview(rule, TimeZoneInfo.Utc, now, ["a", "b", "c", "d"], now, true, last);
        Assert.AreEqual("d", preview.Slots[0].UserIds[0]);
        CollectionAssert.AreEqual(expected.Select(shift => shift.UserId).ToArray(), preview.Slots.SelectMany(slot => slot.UserIds).ToArray());
        Assert.IsTrue(preview.Slots.All(slot => slot.UserIds.Distinct().Count() == 4));
    }

    [TestMethod]
    public void EmptyAndInsufficientQueuesReturnInformativeGeneratorErrorsWithoutSlots()
    {
        var now = new DateTime(2026, 11, 9, 14, 0, 0, DateTimeKind.Utc);
        var rule = new OnCallRule { StaffPerShift = 2 };
        var empty = Service().Preview(rule, TimeZoneInfo.Utc, null, [], now);
        Assert.AreEqual(0, empty.Slots.Count);
        Assert.Contains("Add rotation queue members", empty.Errors.Single());
        var insufficient = Service().Preview(rule, TimeZoneInfo.Utc, null, ["a"], now);
        Assert.AreEqual(0, insufficient.Slots.Count);
        Assert.AreEqual("Staff per shift must not exceed the rotation queue size.", insufficient.Errors.Single());
    }
}
