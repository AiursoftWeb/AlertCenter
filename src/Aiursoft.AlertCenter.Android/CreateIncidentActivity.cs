using Android.App;
using Android.Content;
using Android.OS;
using Android.Widget;
using Aiursoft.AlertCenter.SDK.Models;
using Google.Android.Material.Button;
using Google.Android.Material.TextField;

namespace Aiursoft.AlertCenter.Android;

[Activity(Label = "New incident", Exported = false, Theme = "@style/AppTheme")]
public sealed class CreateIncidentActivity : WorkspaceActivity
{
    private TextInputEditText _title = null!;
    private TextInputEditText _content = null!;
    private Spinner _application = null!;
    private Spinner _level = null!;
    private MaterialButton _submit = null!;
    private readonly string[] _levels = ["sev0", "sev1", "sev2", "sev3", "sev4"];
    private List<MobileApplication> _applications = [];
    private Bundle? _saved;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _saved = savedInstanceState;
        Setup("New incident", back: true);
        Text(Body, "Create an incident", 25);
        Text(Body, "The application's notification rules apply to the new incident.", 14);
        _ = RunAsync(LoadAsync);
    }
    private async Task LoadAsync()
    {
        _applications = (await Session.RequireApi().GetApplicationsAsync()).Applications.Where(item => item.CanCreate).ToList();
        if (_applications.Count == 0) { Text(Body, "You cannot create incidents in any enabled application."); return; }
        _application = Select(Body, "Application", _applications.Select(item => item.Name).ToArray(),
            _applications.FindIndex(item => item.Id.ToString() == _saved?.GetString("application")));
        _title = Input(Body, "Title", _saved?.GetString("title"));
        _content = Input(Body, "Description (Markdown supported)", _saved?.GetString("content"), multiline: true);
        _level = Select(Body, "Severity", _levels, _saved?.GetInt("level", 4) ?? 4);
        _submit = Button(Body, "Create incident", () => _ = RunAsync(SubmitAsync, _submit));
    }
    protected override void OnSaveInstanceState(Bundle outState)
    {
        if (_title != null)
        {
            outState.PutString("title", _title.Text);
            outState.PutString("content", _content.Text);
            outState.PutInt("level", _level.SelectedItemPosition);
            outState.PutString("application", _applications[_application.SelectedItemPosition].Id.ToString());
        }
        base.OnSaveInstanceState(outState);
    }
    private async Task SubmitAsync()
    {
        var title = _title.Text?.Trim() ?? string.Empty;
        if (title.Length is 0 or > 200) { _title.Error = "Enter a title of up to 200 characters."; return; }
        if ((_content.Text?.Length ?? 0) > 4000) { _content.Error = "The description cannot exceed 4,000 characters."; return; }
        var response = await Session.RequireApi().CreateIncidentAsync(new MobileCreateIncidentRequest
        {
            ApplicationId = _applications[_application.SelectedItemPosition].Id,
            Title = title, Content = _content.Text, Level = _levels[_level.SelectedItemPosition]
        });
        if (response.CanView) StartActivity(new Intent(this, typeof(IncidentActivity)).PutExtra("incidentId", response.IncidentId.ToString()));
        else Toast.MakeText(this, "Incident created.", ToastLength.Long)!.Show();
        Finish();
    }
}
