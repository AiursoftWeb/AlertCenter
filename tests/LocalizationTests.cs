using Aiursoft.AlertCenter.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class LocalizationTests
{
    [TestMethod]
    public void ScheduleValidationUsesTheProvidedLocalizer()
    {
        var localizer = new PrefixLocalizer();
        var errors = OnCallScheduleValidator.Validate(DateTime.UtcNow, 1, 16, [], [],
            new Dictionary<string, string>(), localizer);

        Assert.AreEqual(1, errors.Count);
        Assert.StartsWith("localized: ", errors[0]);
        Assert.Contains("UTC", errors[0]);
    }

    private sealed class PrefixLocalizer : IStringLocalizer
    {
        public LocalizedString this[string name] => new(name, "localized: " + name);
        public LocalizedString this[string name, params object[] arguments] => new(name, string.Format("localized: " + name, arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}

internal static class TestLocalization
{
    internal static IStringLocalizerFactory Factory { get; } = new ResourceManagerStringLocalizerFactory(
        Options.Create(new LocalizationOptions { ResourcesPath = "Resources" }), NullLoggerFactory.Instance);

    internal static IStringLocalizer<T> For<T>() => new StringLocalizer<T>(Factory);
}
