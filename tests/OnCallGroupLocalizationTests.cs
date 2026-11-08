using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class OnCallGroupLocalizationTests
{
    private static readonly string WebRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../src/Aiursoft.AlertCenter"));

    [TestMethod]
    [DataRow("ar-SA")]
    [DataRow("da-DK")]
    [DataRow("de-DE")]
    [DataRow("el-GR")]
    [DataRow("en-GB")]
    [DataRow("es-ES")]
    [DataRow("fi-FI")]
    [DataRow("fr-FR")]
    [DataRow("hi-IN")]
    [DataRow("id-ID")]
    [DataRow("it-IT")]
    [DataRow("ja-JP")]
    [DataRow("ko-KR")]
    [DataRow("nl-NL")]
    [DataRow("pl-PL")]
    [DataRow("pt-BR")]
    [DataRow("pt-PT")]
    [DataRow("ro-RO")]
    [DataRow("ru-RU")]
    [DataRow("sv-SE")]
    [DataRow("th-TH")]
    [DataRow("tr-TR")]
    [DataRow("uk-UA")]
    [DataRow("vi-VN")]
    [DataRow("zh-CN")]
    [DataRow("zh-HK")]
    [DataRow("zh-TW")]
    public void TeamFormViewsPreviewAndJavaScriptAreLocalized(string culture)
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            var resources = new Dictionary<string, HashSet<string>>();
            foreach (var view in new[] { "_GroupForm", "GroupDetails" })
            {
                var source = File.ReadAllText(Path.Combine(WebRoot, "Views/OnCall", view + ".cshtml"));
                resources["Views.OnCall." + view] = Regex.Matches(source, "(?:Localizer|viewLocalizer)\\[\"([^\"]+)\"\\]")
                    .Select(match => match.Groups[1].Value).ToHashSet();
                Assert.IsGreaterThan(0, resources["Views.OnCall." + view].Count, view);
            }
            resources["Views.OnCall._GroupForm"].UnionWith(Enum.GetNames<DayOfWeek>());
            resources["Controllers.OnCallController"] =
            [
                "The start time does not match a weekly handover point; the first shift will be a partial segment.",
                "Add rotation queue members to preview shifts.",
                "The team preview request format is invalid.",
                "Please select a valid team local start time (must be at least 28 days from the date boundary).",
                "Invalid team local time. During daylight saving transitions, skipped times are invalid and repeated times use standard time.",
                "On-call team does not exist."
            ];
            resources["Views.Shared._ActionFeedback"] = [];
            var feedback = File.ReadAllText(Path.Combine(WebRoot, "Views/Shared/_ActionFeedback.cshtml"));
            var supplied = Regex.Matches(feedback,
                    "<span data-key=\"([^\"]+)\">@(?:Localizer|viewLocalizer)\\[(\"(?:\\\\.|[^\"\\\\])*\")\\](?:\\.Value)?</span>")
                .ToDictionary(match => match.Groups[1].Value,
                    match => JsonSerializer.Deserialize<string>(match.Groups[2].Value)!);
            foreach (Match span in Regex.Matches(feedback, "<span data-key=\"[^\"]+\">[^<]+</span>"))
                if (Regex.IsMatch(span.Value, "\\{\\d+\\}"))
                    Assert.Contains("].Value</span>", span.Value, "JavaScript placeholders must not be HTML-formatted without arguments.");
            foreach (var script in new[] { "oncall-member-search.js", "oncall-group-form.js" })
            {
                var source = File.ReadAllText(Path.Combine(WebRoot, "wwwroot/scripts", script));
                var calls = Regex.Matches(source, "getLocalizedText\\(['\"]([^'\"]+)['\"], ['\"]([^'\"]+)['\"]\\)");
                Assert.IsGreaterThan(0, calls.Count, script);
                foreach (Match call in calls)
                {
                    var key = call.Groups[2].Value;
                    Assert.IsTrue(supplied.TryGetValue(call.Groups[1].Value, out var suppliedKey),
                        $"{script}: missing literal localization span for {call.Groups[1].Value}");
                    Assert.AreEqual(key, suppliedKey, script);
                    resources["Views.Shared._ActionFeedback"].Add(key);
                }
            }
            var failures = new List<string>();
            foreach (var (resource, keys) in resources)
            {
                var path = Path.Combine(WebRoot, "Resources", resource.Replace('.', Path.DirectorySeparatorChar)) + $".{culture}.resx";
                var entries = XDocument.Load(path).Root!.Elements("data")
                    .ToDictionary(element => element.Attribute("name")!.Value, element => element.Element("value")!.Value);
                var localizer = TestLocalization.Factory.Create(resource, typeof(Startup).Assembly.GetName().Name!);
                foreach (var key in keys)
                {
                    var localized = localizer[key];
                    if (!entries.ContainsKey(key) || localized.ResourceNotFound || string.IsNullOrWhiteSpace(localized.Value))
                        failures.Add($"{resource}: missing {key}");
                    else if (culture.StartsWith("zh-", StringComparison.Ordinal) && key == localized.Value)
                        failures.Add($"{resource}: untranslated {key}");
                    foreach (Match placeholder in Regex.Matches(key, "\\{\\d+\\}"))
                        if (!localized.Value.Contains(placeholder.Value, StringComparison.Ordinal))
                            failures.Add($"{resource}: missing {placeholder.Value} in {key}");
                }
            }
            Assert.IsEmpty(failures, culture + ":\n" + string.Join("\n", failures));
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }
}
