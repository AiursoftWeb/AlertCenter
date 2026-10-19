using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Aiursoft.AlertCenter.Models;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class FilterModelTests
{
    [TestMethod]
    [DataRow(null, null)]
    [DataRow("", null)]
    [DataRow(" \t\r\n", null)]
    [DataRow(" \tservice outage\r\n", "service outage")]
    [DataRow("　数据库  故障　", "数据库  故障")]
    public void NormalizeTrimsKeywordsAndClearsBlankSearchesForEveryFilter(string? keyword, string? expected)
    {
        var alert = new IncidentFilter { Keyword = keyword };
        var inbox = new InboxFilter { Keyword = keyword };
        var incident = new IncidentFilter { Keyword = keyword };

        alert.Normalize();
        inbox.Normalize();
        incident.Normalize();

        Assert.AreEqual(expected, alert.Keyword);
        Assert.AreEqual(expected, inbox.Keyword);
        Assert.AreEqual(expected, incident.Keyword);
    }

    [TestMethod]
    [DataRow(int.MinValue, 1)]
    [DataRow(0, 1)]
    [DataRow(1, 1)]
    [DataRow(42, 42)]
    [DataRow(1000000, 1000000)]
    [DataRow(int.MaxValue, 1000000)]
    public void NormalizeClampsAlertAndInboxPages(int page, int expected)
    {
        var alert = new IncidentFilter { Page = page };
        var inbox = new InboxFilter { Page = page };

        alert.Normalize();
        inbox.Normalize();

        Assert.AreEqual(expected, alert.Page);
        Assert.AreEqual(expected, inbox.Page);
    }

    [TestMethod]
    [DataRow(null, null, "unread")]
    [DataRow(true, null, "unread")]
    [DataRow(false, null, "all")]
    [DataRow(null, "read", "read")]
    [DataRow(true, "read", "read")]
    [DataRow(false, "read", "read")]
    [DataRow(true, "all", "all")]
    [DataRow(false, "unread", "unread")]
    public void InboxNormalizeUsesLegacyUnreadFlagOnlyWhenReadStatusIsMissing(bool? unreadOnly, string? readStatus, string expected)
    {
        var filter = new InboxFilter { UnreadOnly = unreadOnly, ReadStatus = readStatus };

        filter.Normalize();
        filter.Normalize();

        Assert.AreEqual(expected, filter.ReadStatus);
        Assert.AreEqual(unreadOnly, filter.UnreadOnly);
    }

    [TestMethod]
    [DataRow(null, "all")]
    [DataRow("new", "new")]
    [DataRow("acked", "all")]
    [DataRow("mitigated", "mitigated")]
    [DataRow("resolved", "resolved")]
    [DataRow("all", "all")]
    public void IncidentNormalizeDefaultsToAllWithoutOverwritingAnExplicitStatus(string? status, string expected)
    {
        var filter = new IncidentFilter { Status = status };

        filter.Normalize();

        Assert.AreEqual(expected, filter.Status);
        Assert.AreEqual(status == "acked" ? true : (bool?)null, filter.Acknowledged);
    }

    [TestMethod]
    public void DefaultRoutesOmitEmptyFiltersAndTheFirstPage()
    {
        var alert = new IncidentFilter();
        var inbox = new InboxFilter();
        alert.Normalize();
        inbox.Normalize();

        Assert.AreEqual(0, alert.ToRouteValues().Count);
        Assert.AreEqual(0, inbox.ToRouteValues().Count);
    }

    [TestMethod]
    [DataRow(null, "3")]
    [DataRow(-1, null)]
    [DataRow(0, null)]
    [DataRow(1, null)]
    [DataRow(2, "2")]
    public void RoutePageOverridesDoNotMutateTheSelectedPage(int? page, string? expected)
    {
        var alert = new IncidentFilter { Page = 3 };
        var inbox = new InboxFilter { Page = 3 };

        foreach (var values in new[] { alert.ToRouteValues(page), inbox.ToRouteValues(page) })
        {
            Assert.AreEqual(expected is null ? 0 : 1, values.Count);
            Assert.AreEqual(expected, values.GetValueOrDefault("page"));
        }
        Assert.AreEqual(3, alert.Page);
        Assert.AreEqual(3, inbox.Page);
    }

    [TestMethod]
    [DataRow("unread", null)]
    [DataRow("read", "read")]
    [DataRow("all", "all")]
    public void InboxRoutesOnlySerializeNonDefaultReadStatusAndNeverTheLegacyFlag(string readStatus, string? expected)
    {
        var filter = new InboxFilter { ReadStatus = readStatus, UnreadOnly = false };
        filter.Normalize();

        var values = filter.ToRouteValues();

        Assert.AreEqual(expected is null ? 0 : 1, values.Count);
        Assert.AreEqual(expected, values.GetValueOrDefault("readStatus"));
        Assert.IsFalse(values.ContainsKey("unreadOnly"));
    }

    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fa-IR")]
    public void RoutesPreserveEverySearchFieldWithCultureInvariantDatesAndPages(string culture)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var applicationId = Guid.Parse("81752d17-2803-4516-b6a3-11df80ac242f");
            var channelId = Guid.Parse("58d41938-7c6f-470d-af67-280b7f14df42");
            var fromDate = new DateOnly(2026, 9, 28);
            var toDate = new DateOnly(2026, 10, 5);
            var alert = new IncidentFilter
            {
                Keyword = " service outage ", ApplicationId = applicationId, Level = "sev1",
                FromDate = fromDate, ToDate = toDate, Page = 1234
            };
            var inbox = new InboxFilter
            {
                Keyword = " service outage ", ApplicationId = applicationId, ChannelId = channelId, Level = "sev1",
                ReadStatus = "read", FromDate = fromDate, ToDate = toDate, Page = 1234
            };
            alert.Normalize();
            inbox.Normalize();
            var expected = new Dictionary<string, string?>
            {
                ["keyword"] = "service outage", ["applicationId"] = applicationId.ToString(), ["level"] = "sev1",
                ["fromDate"] = "2026-09-28", ["toDate"] = "2026-10-05", ["page"] = "1234"
            };

            CollectionAssert.AreEquivalent(expected.ToArray(), alert.ToRouteValues().ToArray());
            expected["channelId"] = channelId.ToString();
            expected["readStatus"] = "read";
            CollectionAssert.AreEquivalent(expected.ToArray(), inbox.ToRouteValues().ToArray());
            expected["page"] = "2";
            CollectionAssert.AreEquivalent(expected.ToArray(), inbox.ToRouteValues(2).ToArray());
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [TestMethod]
    [DataRow(null, null, true)]
    [DataRow("2026-09-28", null, true)]
    [DataRow(null, "2026-09-28", true)]
    [DataRow("2026-09-28", "2026-09-28", true)]
    [DataRow("2026-09-28", "2026-09-29", true)]
    [DataRow("2026-09-29", "2026-09-28", false)]
    public void EveryFilterValidatesDateOrderAndAssociatesErrorsWithBothDates(string? from, string? to, bool valid)
    {
        DateOnly? fromDate = from is null ? null : DateOnly.ParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        DateOnly? toDate = to is null ? null : DateOnly.ParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        object[] filters =
        [
            new IncidentFilter { FromDate = fromDate, ToDate = toDate },
            new InboxFilter { FromDate = fromDate, ToDate = toDate },
            new IncidentFilter { FromDate = fromDate, ToDate = toDate }
        ];

        foreach (var filter in filters)
        {
            var errors = Validate(filter);
            Assert.AreEqual(valid ? 0 : 1, errors.Count, filter.GetType().Name);
            if (!valid)
                CollectionAssert.AreEquivalent(new[] { "FromDate", "ToDate" }, errors.Single().MemberNames.ToArray());
        }
    }

    [TestMethod]
    [DataRow("sev0")]
    [DataRow("sev1")]
    [DataRow("sev2")]
    [DataRow("sev3")]
    [DataRow("sev4")]
    public void EveryFilterAcceptsTheKeywordLimitAndEverySupportedSeverity(string level)
    {
        var keyword = new string('x', 200);
        object[] filters =
        [
            new IncidentFilter { Keyword = keyword, Level = level },
            new InboxFilter { Keyword = keyword, Level = level },
            new IncidentFilter { Keyword = keyword, Level = level }
        ];

        foreach (var filter in filters)
            Assert.AreEqual(0, Validate(filter).Count, filter.GetType().Name);
    }

    [TestMethod]
    public void EveryFilterRejectsKeywordsOverTheLimit()
    {
        var keyword = new string('x', 201);
        object[] filters =
        [
            new IncidentFilter { Keyword = keyword },
            new InboxFilter { Keyword = keyword },
            new IncidentFilter { Keyword = keyword }
        ];

        foreach (var filter in filters)
        {
            var errors = Validate(filter);
            Assert.AreEqual(1, errors.Count, filter.GetType().Name);
            CollectionAssert.AreEqual(new[] { "Keyword" }, errors.Single().MemberNames.ToArray());
        }
    }

    [TestMethod]
    [DataRow("sev5")]
    [DataRow("sev-1")]
    [DataRow("SEV1")]
    [DataRow("sev1 ")]
    public void EveryFilterRejectsUnsupportedSeverities(string level)
    {
        object[] filters =
        [
            new IncidentFilter { Level = level },
            new InboxFilter { Level = level },
            new IncidentFilter { Level = level }
        ];

        foreach (var filter in filters)
        {
            var errors = Validate(filter);
            Assert.AreEqual(1, errors.Count, filter.GetType().Name);
            CollectionAssert.AreEqual(new[] { "Level" }, errors.Single().MemberNames.ToArray());
        }
    }

    [TestMethod]
    [DataRow("unread", true)]
    [DataRow("read", true)]
    [DataRow("all", true)]
    [DataRow("READ", false)]
    [DataRow("archived", false)]
    public void InboxValidatesReadStatus(string status, bool valid)
    {
        var errors = Validate(new InboxFilter { ReadStatus = status });

        Assert.AreEqual(valid ? 0 : 1, errors.Count);
        if (!valid)
            CollectionAssert.AreEqual(new[] { "ReadStatus" }, errors.Single().MemberNames.ToArray());
    }

    [TestMethod]
    [DataRow("new", true)]
    [DataRow("acked", true)]
    [DataRow("mitigated", true)]
    [DataRow("resolved", true)]
    [DataRow("all", true)]
    [DataRow("NEW", false)]
    [DataRow("acknowledged", false)]
    public void IncidentValidatesStatus(string status, bool valid)
    {
        var errors = Validate(new IncidentFilter { Status = status });

        Assert.AreEqual(valid ? 0 : 1, errors.Count);
        if (!valid)
            CollectionAssert.AreEqual(new[] { "Status" }, errors.Single().MemberNames.ToArray());
    }

    private static List<ValidationResult> Validate(object filter)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(filter, new ValidationContext(filter), results, validateAllProperties: true);
        return results;
    }
}
