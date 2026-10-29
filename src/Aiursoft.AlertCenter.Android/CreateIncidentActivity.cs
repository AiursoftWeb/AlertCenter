using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Aiursoft.AlertCenter.SDK.Models;
using Google.Android.Material.TextField;

namespace Aiursoft.AlertCenter.Android;

[Activity(Label = "New incident", Exported = false, Theme = "@style/AppTheme")]
public sealed class CreateIncidentActivity : WorkspaceActivity
{
    private TextInputEditText _title = null!;
    private TextInputEditText _content = null!;
    private Guid _applicationId;
    private int _level = 4;
    private string _savedTitle = string.Empty;
    private string _savedContent = string.Empty;
    private List<MobileApplication> _applications = [];
    private readonly string[] _levels = ["sev0", "sev1", "sev2", "sev3", "sev4"];

    protected override bool HasUnsavedChanges => !string.IsNullOrEmpty(_title?.Text ?? _savedTitle)
        || !string.IsNullOrEmpty(_content?.Text ?? _savedContent);

    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state);
        _savedTitle = state?.GetString("title") ?? string.Empty;
        _savedContent = state?.GetString("content") ?? string.Empty;
        _level = state?.GetInt("level", 4) ?? 4;
        Guid.TryParse(state?.GetString("application") ?? Intent?.GetStringExtra("applicationId"), out _applicationId);
        Setup("New incident", back: true);
        _ = RunAsync(ReloadAsync);
    }
    protected override async Task ReloadAsync()
    {
        _savedTitle = _title?.Text ?? _savedTitle;
        _savedContent = _content?.Text ?? _savedContent;
        _applications = (await Session.RequireApi().GetApplicationsAsync()).Applications.Where(item => item.CanCreate).ToList();
        Body.RemoveAllViews(); Dock.RemoveAllViews();
        if (_applications.Count == 0) { Text(Body, "You cannot create incidents in any enabled application."); Dock.Visibility = ViewStates.Gone; return; }
        Text(Body, "Report an incident", 28, true);
        Text(Body, "Describe the impact and give your team a clear starting point.", 14);
        var form = Card(Body);
        var selected = Math.Max(0, _applications.FindIndex(item => item.Id == _applicationId));
        _applicationId = _applications[selected].Id;
        Choice(form, "Application", _applications.Select(item => item.Name).ToArray(), selected, index => _applicationId = _applications[index].Id, localizeOptions: false);
        _title = Input(form, "Incident title", _savedTitle);
        _content = Input(form, "Description", _savedContent, multiline: true);
        var count = Text(form, $"{_savedContent.Length} / 4000", 12);
        _content.TextChanged += (_, _) => count.Text = $"{_content.Text?.Length ?? 0} / 4000";
        TextButton(form, "Preview", () => ShowMarkdownPreview(_content.Text ?? string.Empty));
        Choice(form, "Severity", _levels.Select(SeverityName).ToArray(), _level, index => _level = index);
        Text(Body, "The application's notification rules apply to this incident.", 14);
        Dock.Visibility = ViewStates.Visible;
        Button(Dock, "Create incident", () => _ = RunAsync(SubmitAsync), true);
    }
    protected override void OnSaveInstanceState(Bundle state)
    {
        state.PutString("title", _title?.Text ?? _savedTitle);
        state.PutString("content", _content?.Text ?? _savedContent);
        state.PutInt("level", _level);
        state.PutString("application", _applicationId.ToString());
        base.OnSaveInstanceState(state);
    }
    private async Task SubmitAsync()
    {
        var title = _title.Text?.Trim() ?? string.Empty;
        if (title.Length is 0 or > 200) { _title.Error = T("Enter a title of up to 200 characters."); return; }
        if ((_content.Text?.Length ?? 0) > 4000) { _content.Error = T("The description cannot exceed 4,000 characters."); return; }
        var response = await Session.RequireApi().CreateIncidentAsync(new MobileCreateIncidentRequest
        { ApplicationId = _applicationId, Title = title, Content = _content.Text, Level = _levels[_level] });
        Session.Feedback = "Incident created.";
        if (response.CanView) StartActivity(new Intent(this, typeof(IncidentActivity)).PutExtra("incidentId", response.IncidentId.ToString()));
        Finish();
    }
}
