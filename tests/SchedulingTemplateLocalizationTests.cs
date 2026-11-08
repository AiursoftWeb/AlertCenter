using System.Globalization;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class SchedulingTemplateLocalizationTests
{
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
    public void TemplateTerminologyAndConfigurationAreLocalized(string culture)
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            const string description = "Define how shifts are split and rotated for teams' automatic extension and one-click generation. Changing a template does not affect scheduled shifts.";
            var resources = new Dictionary<string, string[]>
            {
                ["Services.ViewModelArgsInjector"] = ["Scheduling Templates"],
                ["Authorization.AppPermissions"] = ["Allows managing on-call teams, scheduling templates, and schedules."],
                ["Controllers.OnCallController"] = ["Please select an existing scheduling template.", "Please enter the template name.",
                    "Scheduling template updated.", "Scheduling template created successfully.", "Scheduling template deleted.",
                    "The template or team has changed. Refresh the page and try again.",
                    "This template is still bound to a team. Please update the team's template binding first.",
                    "No template bound to the team yet. You can manually create a schedule."],
                ["Services.OnCallScheduleService"] = ["Please bind a scheduling template to the team first."],
                ["Models.SaveOnCallRuleRequest"] = ["Please enter the template name.", "Template name must not exceed 100 characters."],
                ["Views.OnCall.Index"] = ["Scheduling Templates", "Create Template", "Saved templates", "Template", "View template",
                    "No saved templates yet. Create a template first, then have the team bind and use it.",
                    "Delete scheduling template", "Confirm template deletion", "Fixed duration: {0} minutes", "Teams using", description],
                ["Views.OnCall.RuleDetails"] = ["Reusable scheduling template · ", "Back to Template List", "Edit template", "Template configuration",
                    "Fixed duration: {0} minutes", description],
                ["Views.OnCall._RuleForm"] = ["Template name", "Fixed duration", "Shift duration (minutes)", "Day of week", "Handover time",
                    "Remove", "Add handover point", "8-hour three-shift preset", "Save template", "Cancel", description],
                ["Views.OnCall.Create"] = ["Create Scheduling Template", "Back to Template List"],
                ["Views.OnCall.Edit"] = ["Edit scheduling template", "Back to Template Details"],
                ["Views.OnCall.Groups"] = ["Template"],
                ["Views.OnCall._GroupForm"] = ["Scheduling Template", description],
                ["Views.OnCall.GroupDetails"] = ["Template:", "Staff per shift", "Template not bound; manual scheduling is still available.",
                    "Templates are not deleted. Only teams with no saved shifts and no bound applications can be deleted. This action is irreversible."],
                ["Views.OnCall.Details"] = ["Scheduling template and members", "Template:"],
                ["Views.OnCall.Schedule"] = ["Manage scheduling templates", "Template"]
            };
            foreach (var resource in new[] { "Views.OnCall.Index", "Views.OnCall.RuleDetails", "Views.OnCall._RuleForm" })
            {
                resources[resource] = [..resources[resource], "Split mode", "Weekly handover points", "Staff per shift", "Rotation strategy",
                    "Pure queue", "Swap adjacent members on alternate rounds for even-sized teams", ..Enum.GetNames<DayOfWeek>()];
            }
            foreach (var (resource, keys) in resources)
            {
                var localizer = TestLocalization.Factory.Create(resource, typeof(Startup).Assembly.GetName().Name!);
                foreach (var key in keys)
                {
                    var value = localizer[key];
                    Assert.IsFalse(value.ResourceNotFound, $"{culture}: {resource}: {key}");
                    Assert.IsFalse(string.IsNullOrWhiteSpace(value.Value), $"{culture}: {resource}: {key}");
                    if (culture != "en-GB" && key == description)
                        Assert.AreNotEqual(key, value.Value, $"{culture}: {resource}: description must be translated");
                    if (key.Contains("{0}", StringComparison.Ordinal))
                        Assert.Contains("{0}", value.Value);
                }
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }
}
