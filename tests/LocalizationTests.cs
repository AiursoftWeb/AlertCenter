using Aiursoft.AlertCenter.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class LocalizationTests
{
    [TestMethod]
    public void IncidentSearchSettingsHaveChineseNamesAndDescriptions()
    {
        var originalCulture = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("zh-CN");
            var localizer = TestLocalization.For<Configuration.SettingsMap>();
            var keys = new[]
            {
                Configuration.SettingsMap.EmbeddingOllamaInstance, Configuration.SettingsMap.EmbeddingModel,
                Configuration.SettingsMap.EmbeddingApiToken, Configuration.SettingsMap.EnableEmbeddingBasedSearch,
                Configuration.SettingsMap.EmbeddingQueryCacheLimit
            };
            foreach (var definition in Configuration.SettingsMap.Definitions.Where(d => keys.Contains(d.Key)))
            {
                Assert.IsFalse(localizer[definition.Name].ResourceNotFound, definition.Name);
                Assert.IsFalse(localizer[definition.Description].ResourceNotFound, definition.Description);
            }
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = originalCulture;
        }
    }

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
