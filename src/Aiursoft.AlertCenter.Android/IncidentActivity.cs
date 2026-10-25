using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using Aiursoft.AlertCenter.SDK.Models;
using AlertDialog = AndroidX.AppCompat.App.AlertDialog;

namespace Aiursoft.AlertCenter.Android;

[Activity(Label = "Incident", Exported = false, Theme = "@style/AppTheme")]
public sealed class IncidentActivity : WorkspaceActivity
{
    private Guid _id;
    private int _page = 1;
    private string _draft = string.Empty;
    private MobileIncidentResponse? _details;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (!Guid.TryParse(Intent?.GetStringExtra("incidentId"), out _id)) { Finish(); return; }
        _page = savedInstanceState?.GetInt("page", 1) ?? 1;
        _draft = savedInstanceState?.GetString("draft") ?? string.Empty;
        Setup("Incident", back: true);
        Toolbar.Menu!.Add(0, 1, 0, "Refresh")!.SetIcon(Resource.Drawable.ic_refresh)!.SetShowAsAction(ShowAsAction.Always);
        Toolbar.Menu!.Add(0, 2, 1, "Actions")!.SetIcon(Resource.Drawable.ic_tasks)!.SetShowAsAction(ShowAsAction.Always);
        Toolbar.MenuItemClick += (_, e) =>
        {
            if (e.Item!.ItemId == 1) _ = RunAsync(ReloadAsync);
            if (e.Item.ItemId == 2) ShowActions();
        };
        Text(Body, "Loading incident…");
    }
    protected override void OnResume()
    {
        base.OnResume();
        if (Session.IsAuthenticated && _id != Guid.Empty) _ = RunAsync(ReloadAsync);
    }
    protected override void OnSaveInstanceState(Bundle outState)
    {
        outState.PutInt("page", _page);
        outState.PutString("draft", _draft);
        base.OnSaveInstanceState(outState);
    }
    protected override async Task ReloadAsync()
    {
        _details = await Session.RequireApi().GetIncidentAsync(_id, _page);
        var incident = _details.Incident;
        Toolbar.Title = $"#{incident.ShortId}";
        Toolbar.Menu!.FindItem(2)!.SetVisible(incident.CanManage);
        Body.RemoveAllViews();
        Text(Body, incident.Title, 25);
        Text(Body, $"{incident.ApplicationName}\n{incident.Level.ToUpperInvariant()} · {ProgressName(incident.Status)} · {(incident.AcknowledgedAtUtc.HasValue ? "ACK" : "Not acknowledged")}", 16);
        Text(Body, $"Owner: {incident.OwnerName ?? "Unassigned"}");
        if (incident.CanManage) Button(Body, "Handle incident", ShowActions);
        if (!string.IsNullOrWhiteSpace(incident.Content)) Text(Body, incident.Content);
        Text(Body, "Times", 20);
        Text(Body, $"Occurred: {incident.OccurredAtUtc.ToLocalTime():g}\nReceived: {incident.ReceivedAtUtc.ToLocalTime():g}" +
            (incident.AcknowledgedAtUtc.HasValue ? $"\nAcknowledged: {incident.AcknowledgedAtUtc.Value.ToLocalTime():g}" : string.Empty) +
            (incident.MitigatedAtUtc.HasValue ? $"\nMitigated: {incident.MitigatedAtUtc.Value.ToLocalTime():g}" : string.Empty) +
            (incident.ResolvedAtUtc.HasValue ? $"\nResolved: {incident.ResolvedAtUtc.Value.ToLocalTime():g}" : string.Empty), 14);
        Text(Body, "On-call engineers", 20);
        Text(Body, $"Original: {Names(_details.OriginalOces)}\nCurrent: {Names(_details.CurrentOces)}", 14);
        if (_details.CurrentOceError != null) Text(Body, _details.CurrentOceError, 14);
        if (!string.IsNullOrWhiteSpace(incident.ContextJson))
            Button(Body, "View context", () => ShowJson("Context", incident.ContextJson));
        if (!string.IsNullOrWhiteSpace(incident.MetadataJson))
            Button(Body, "View metadata", () => ShowJson("Metadata", incident.MetadataJson));
        Text(Body, "Discussion and timeline", 20);
        if (_details.Activities.Count == 0) Text(Body, "No activity yet.");
        foreach (var item in _details.Activities)
        {
            Text(Body, $"{item.ActorName} · {ActivityName(item.Type)}", 16);
            Text(Body, item.CreatedAtUtc.ToLocalTime().ToString("g"), 12);
            if (item.PreviousStatus != item.NewStatus && item.NewStatus != null)
                Text(Body, $"Progress: {(item.PreviousStatus == null ? "—" : ProgressName(item.PreviousStatus))} → {ProgressName(item.NewStatus)}", 14);
            if (item.PreviousLevel != item.NewLevel && item.NewLevel != null)
                Text(Body, $"Severity: {item.PreviousLevel ?? "—"} → {item.NewLevel}", 14);
            if (item.NewOwnerName != null) Text(Body, $"Owner: {item.NewOwnerName}", 14);
            if (item.Note != null) Text(Body, item.Note);
        }
        Text(Body, $"Activity page {_page}", 13);
        if (_page > 1) Button(Body, "Newer activity", () => { _page--; _ = RunAsync(ReloadAsync); });
        if (_details.HasNextPage) Button(Body, "Older activity", () => { _page++; _ = RunAsync(ReloadAsync); });
        if (incident.CanManage) Button(Body, "Add comment", () => Compose("comment", "Add comment"));
    }
    private static string ActivityName(string type) => type switch
    {
        "acked" => "Acknowledged", "created" => "Created", "mitigated" => "Mitigated",
        "resolved" => "Resolved", "reopened" => "Reopened", "level_changed" => "Severity changed",
        "comment" => "Comment", "ownership_changed" => "Ownership changed", _ => "Updated"
    };
    private static string Names(List<string> names) => names.Count == 0 ? "Unavailable" : string.Join(", ", names);
    private void ShowActions()
    {
        if (IsBusy) return;
        var incident = _details?.Incident;
        if (incident?.CanManage != true) return;
        var actions = new List<(string Action, string Label)> { ("acknowledge", "Acknowledge"), ("take-ownership", "Take ownership"), ("comment", "Add comment") };
        if (incident.Status == "new" && incident.AcknowledgedAtUtc.HasValue) actions.Add(("mitigate", "Mitigate"));
        if (incident.Status == "new" && !incident.AcknowledgedAtUtc.HasValue)
            Toast.MakeText(this, "Acknowledge the incident before mitigating it.", ToastLength.Short)!.Show();
        if (incident.Status == "mitigated") actions.Add(("resolve", "Resolve"));
        if (incident.Status is "mitigated" or "resolved") actions.Add(("reopen", "Reopen"));
        if (incident.Status is "new" or "mitigated") actions.Add(("severity", "Change severity"));
        new AlertDialog.Builder(this).SetTitle("Handle incident")!
            .SetItems(actions.Select(item => item.Label).ToArray(), (_, e) => Compose(actions[e.Which].Action, actions[e.Which].Label))!
            .SetNegativeButton("Cancel", (_, _) => { })!.Show();
    }
    private void Compose(string action, string label)
    {
        if (_details?.Incident.CanManage != true) return;
        var version = _details.Incident.Version;
        var panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        panel.SetPadding(Dp(24), Dp(8), Dp(24), Dp(8));
        if (action == "resolve") Text(panel, "Record the final resolution of this incident. This may notify the original on-call engineers.", 14);
        var note = Input(panel, action == "comment" ? "Comment (Markdown supported)" : "Reason or progress", _draft, multiline: true);
        note.TextChanged += (_, _) => _draft = note.Text ?? string.Empty;
        var levels = new[] { "sev0", "sev1", "sev2", "sev3", "sev4" };
        var level = action == "severity" ? Select(panel, "New severity", levels, Array.IndexOf(levels, _details.Incident.Level)) : null;
        var dialog = new AlertDialog.Builder(this).SetTitle(label)!.SetView(panel)!
            .SetPositiveButton(action == "comment" ? "Post comment" : label, (_, _) => { })!
            .SetNegativeButton("Cancel", (_, _) => { })!.Create()!;
        dialog.Show();
        var submit = dialog.GetButton((int)DialogButtonType.Positive)!;
        submit.Click += (_, _) =>
        {
            var text = note.Text?.Trim() ?? string.Empty;
            if (text.Length > 4000 || (text.Length == 0 && action is "comment" or "mitigate" or "resolve" or "severity"))
            {
                note.Error = "Enter a reason or comment of up to 4,000 characters.";
                return;
            }
            _ = RunAsync(async () =>
            {
                if (action == "comment") await Session.RequireApi().CommentAsync(_id, text);
                else await Session.RequireApi().ExecuteAsync(_id, action, new MobileIncidentAction
                {
                    Version = version, Note = text, Level = level == null ? null : levels[level.SelectedItemPosition]
                });
                _draft = string.Empty;
                _page = 1;
                dialog.Dismiss();
                await ReloadAsync();
            }, submit);
        };
    }
    private void ShowJson(string title, string? value)
    {
        var scroll = new ScrollView(this);
        var panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        panel.SetPadding(Dp(24), Dp(12), Dp(24), Dp(12));
        Text(panel, value ?? string.Empty, 14);
        scroll.AddView(panel);
        new AlertDialog.Builder(this).SetTitle(title)!.SetView(scroll)!.SetPositiveButton("Close", (_, _) => { })!.Show();
    }
}
