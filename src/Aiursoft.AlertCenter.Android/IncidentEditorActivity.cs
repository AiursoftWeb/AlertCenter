using Android.App;
using Android.OS;
using Android.Widget;
using Aiursoft.AlertCenter.SDK.Models;
using Google.Android.Material.TextField;

namespace Aiursoft.AlertCenter.Android;

[Activity(Label = "Update incident", Exported = false, Theme = "@style/AppTheme")]
public sealed class IncidentEditorActivity : WorkspaceActivity
{
    private Guid _id;
    private string _operation = "comment";
    private MobileIncident? _incident;
    private TextInputEditText _note = null!;
    private CheckBox? _resolve;
    private string _draft = string.Empty;
    private string _level = "sev3";
    private bool _resolveImmediately;

    protected override bool HasUnsavedChanges => !string.IsNullOrEmpty(_note?.Text ?? _draft)
        || (_resolve?.Checked ?? false) || (_operation == "severity" && _incident != null && _level != _incident.Level);

    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state);
        if (!Guid.TryParse(Intent?.GetStringExtra("incidentId"), out _id)) { Finish(); return; }
        _operation = Intent?.GetStringExtra("operation") ?? "comment";
        _draft = state?.GetString("draft") ?? string.Empty;
        _level = state?.GetString("level") ?? string.Empty;
        _resolveImmediately = state?.GetBoolean("resolve") ?? false;
        Setup(IncidentActivity.OperationName(_operation), back: true);
        _ = RunAsync(ReloadAsync);
    }
    protected override void OnSaveInstanceState(Bundle state)
    {
        state.PutString("draft", _note?.Text ?? _draft);
        state.PutString("level", _level);
        state.PutBoolean("resolve", _resolve?.Checked ?? false);
        base.OnSaveInstanceState(state);
    }
    protected override async Task ReloadAsync()
    {
        _draft = _note?.Text ?? _draft;
        _resolveImmediately = _resolve?.Checked ?? _resolveImmediately;
        _resolve = null;
        _incident = (await Session.RequireApi().GetIncidentAsync(_id)).Incident;
        Body.RemoveAllViews(); Dock.RemoveAllViews();
        if (!_incident.CanManage) { Dock.Visibility = global::Android.Views.ViewStates.Gone; Text(Body, "You do not have permission to update this incident."); return; }
        var summary = Card(Body);
        Text(summary, _incident.Title, 18, true, localize: false);
        Text(summary, "#" + _incident.ShortId + " · " + ProgressName(_incident.Status), 13);
        Text(Body, _operation switch
        {
            "mitigate" => "Describe the measures taken and the current recovery status.",
            "resolve" => "Record why the incident is fully resolved. This completes its workflow and may notify the original on-call engineers.",
            "reopen" => "Returns this incident to unmitigated. Ownership is preserved and configured recipients are notified.",
            "severity" => "Increasing severity queues an email. Decreasing it does not.",
            "comment" => "Share findings, progress or the next step.",
            _ => "You will become the incident owner. Original on-call email recipients stay the same."
        }, 14);
        if (_operation == "severity")
        {
            string[] levels = ["sev0", "sev1", "sev2", "sev3", "sev4"];
            if (string.IsNullOrEmpty(_level)) _level = _incident.Level;
            Choice(Body, "Severity", levels.Select(SeverityName).ToArray(), Array.IndexOf(levels, _level), i => _level = levels[i]);
        }
        _note = Input(Body, _operation == "comment" ? "Comment" : _operation is "acknowledge" or "take-ownership" or "reopen" ? "Note (optional)" : "Reason", _draft, multiline: true);
        var count = Text(Body, $"{_draft.Length} / 4000", 12);
        _note.TextChanged += (_, _) => { _draft = _note.Text ?? string.Empty; count.Text = $"{_draft.Length} / 4000"; };
        TextButton(Body, "Preview", () => ShowMarkdownPreview(_note.Text ?? string.Empty));
        if (_operation == "mitigate" && _incident.Level is "sev2" or "sev3" or "sev4")
        {
            _resolve = new CheckBox(this) { Text = T("Also resolve: fully fixed and verified"), Checked = _resolveImmediately };
            Body.AddView(_resolve);
            Text(Body, "Uses this reason for both mitigation and resolution.", 13);
        }
        var applicable = _operation switch
        {
            "mitigate" => _incident.Status == "new" && _incident.AcknowledgedAtUtc.HasValue,
            "resolve" => _incident.Status == "mitigated",
            "severity" => _incident.Status is "new" or "mitigated",
            "reopen" => _incident.Status is "mitigated" or "resolved",
            _ => true
        };
        if (!applicable)
        {
            Text(Body, "This action no longer applies. Your draft is still available.", 14);
            Dock.Visibility = global::Android.Views.ViewStates.Gone;
            return;
        }
        Dock.Visibility = global::Android.Views.ViewStates.Visible;
        Button(Dock, IncidentActivity.OperationName(_operation), () => _ = RunAsync(SubmitAsync), true);
    }
    private async Task SubmitAsync()
    {
        var text = _note.Text?.Trim() ?? string.Empty;
        if (text.Length > 4000 || (string.IsNullOrWhiteSpace(text) && _operation is "comment" or "mitigate" or "resolve" or "severity"))
        { _note.Error = T("Enter a reason or comment of up to 4,000 characters."); return; }
        if (_operation == "comment") await Session.RequireApi().CommentAsync(_id, text);
        else await Session.RequireApi().ExecuteAsync(_id, _operation, new MobileIncidentAction
        {
            Version = _incident!.Version, Note = text, Level = _operation == "severity" ? _level : null,
            ResolveImmediately = _operation == "mitigate" && (_resolve?.Checked ?? false)
        });
        Session.Feedback = "Incident updated.";
        Finish();
    }
}
