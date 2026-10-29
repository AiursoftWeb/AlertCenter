using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using Aiursoft.AlertCenter.SDK.Models;

namespace Aiursoft.AlertCenter.Android;

[Activity(Label = "Incident", Exported = false, Theme = "@style/AppTheme")]
public sealed class IncidentActivity : WorkspaceActivity
{
    private Guid _id;
    private int _pages = 1;
    private int _scrollPosition;
    private MobileIncidentResponse? _details;
    private readonly List<MobileActivity> _activities = [];
    private readonly List<MarkdownView> _readers = [];

    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state);
        if (!Guid.TryParse(Intent?.GetStringExtra("incidentId"), out _id)) { Finish(); return; }
        _pages = state?.GetInt("pages", 1) ?? 1;
        _scrollPosition = state?.GetInt("scroll", 0) ?? 0;
        Setup("Incident", back: true);
        Toolbar.Menu!.Add(0, 1, 0, T("Refresh"))!.SetIcon(Resource.Drawable.ic_refresh)!.SetShowAsAction(ShowAsAction.Always);
        Toolbar.Menu!.Add(0, 2, 1, T("More actions"))!.SetIcon(Resource.Drawable.ic_more)!.SetShowAsAction(ShowAsAction.Always);
        Toolbar.MenuItemClick += (_, e) =>
        {
            if (e.Item!.ItemId == 1) _ = RunAsync(ReloadAsync);
            if (e.Item.ItemId == 2 && !IsBusy) ShowActions();
        };
        Text(Body, "Loading incident…", 14);
    }
    protected override void OnResume()
    {
        base.OnResume();
        if (Session.IsAuthenticated) _ = RunAsync(ReloadAsync);
    }
    protected override void OnSaveInstanceState(Bundle state)
    {
        state.PutInt("pages", _pages);
        state.PutInt("scroll", ContentScroll.ScrollY);
        base.OnSaveInstanceState(state);
    }
    protected override async Task ReloadAsync()
    {
        var position = _details == null ? _scrollPosition : ContentScroll.ScrollY;
        var activities = new List<MobileActivity>();
        MobileIncidentResponse? result = null;
        for (var page = 1; page <= _pages; page++)
        {
            result = await Session.RequireApi().GetIncidentAsync(_id, page);
            activities.AddRange(result.Activities);
            if (!result.HasNextPage) break;
        }
        _details = result;
        _pages = result!.Page;
        _activities.Clear(); _activities.AddRange(activities);
        Render();
        ContentScroll.Post(() => ContentScroll.ScrollTo(0, position));
        if (Session.Feedback != null) { ToastSuccess(Session.Feedback); Session.Feedback = null; }
    }
    private void Render()
    {
        var incident = _details!.Incident;
        Toolbar.Title = "#" + incident.ShortId;
        ClearReaders();
        Body.RemoveAllViews(); Dock.RemoveAllViews();
        Text(Body, incident.ApplicationName, 14, localize: false);
        Text(Body, incident.Title, 26, true, localize: false);
        var badges = Row(Body);
        Pill(badges, SeverityName(incident.Level), incident.Level is "sev0" or "sev1" ? Resource.Color.danger_container : incident.Level is "sev2" or "sev3" ? Resource.Color.warning_container : Resource.Color.brand_container,
            incident.Level is "sev0" or "sev1" ? Resource.Color.on_danger_container : incident.Level is "sev2" or "sev3" ? Resource.Color.on_warning_container : Resource.Color.on_brand_container);
        Pill(badges, ProgressName(incident.Status));
        Text(Body, incident.AcknowledgedAtUtc.HasValue ? "Acknowledged" : "Not acknowledged", 14);
        var people = Card(Body);
        Person(people, "Owner", incident.AcknowledgedAtUtc.HasValue ? incident.OwnerName ?? "—" : "—");
        Person(people, "Original on-call", incident.OriginalOces == null ? T("Not recorded") : incident.OriginalOces.Count == 0 ? T("No on-call engineer") : Names(incident.OriginalOces));
        Person(people, "On call now", _details.CurrentOces.Count == 0 ? T("No on-call engineer") : Names(_details.CurrentOces));
        if (_details.CurrentOceError != null) Text(people, "Current on-call information is temporarily unavailable.", 13);
        if (!string.IsNullOrWhiteSpace(incident.Content))
        {
            var body = Card(Body);
            Text(body, "What happened", 19, true);
            Markdown(body, incident.Content);
            TextButton(body, "Copy description", () => Copy(incident.Content));
        }
        var section = Row(Body);
        var title = Text(section, "Activity", 22, true);
        title.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1);
        if (incident.CanManage)
        {
            var comment = TextButton(section, "Comment", () => Edit("comment"));
            comment.LayoutParameters = new LinearLayout.LayoutParams(-2, -2);
        }
        if (_activities.Count == 0) Text(Body, "No activity yet.", 14);
        foreach (var item in _activities)
        {
            var activity = Card(Body);
            var row = Row(activity);
            Pill(row, item.Type == "comment" ? "•" : "✓", Resource.Color.brand_container, Resource.Color.on_brand_container);
            var actor = Text(row, item.ActorName, 16, true, localize: false);
            actor.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1);
            Text(activity, T(ActivityName(item.Type)) + " · " + item.CreatedAtUtc.ToLocalTime().ToString("g"), 13);
            if (item.NewStatus != null && item.NewStatus != item.PreviousStatus)
                Pill(activity, ProgressName(item.NewStatus));
            if (item.NewLevel != null && item.NewLevel != item.PreviousLevel)
                Text(activity, (item.PreviousLevel?.ToUpperInvariant() ?? "—") + " → " + SeverityName(item.NewLevel), 14);
            if (item.NewOwnerName != null) Text(activity, T("Owner") + " · " + item.NewOwnerName, 14);
            if (item.IsDeleted) Text(activity, "Comment deleted.", 14);
            else if (!string.IsNullOrWhiteSpace(item.Note))
            {
                Markdown(activity, item.Note);
            }
            if (item.CanDelete) TextButton(activity, "Delete comment", () => ConfirmDeleteComment(item));
        }
        if (_details.HasNextPage)
            TextButton(Body, "Load earlier activity", () => _ = RunAsync(async () => { _pages++; await ReloadAsync(); }));
        var technical = Card(Body);
        TextButton(technical, "Report information", () => Sheet("Report information", (panel, _) =>
        {
            Text(panel, T("Occurred") + " · " + incident.OccurredAtUtc.ToLocalTime().ToString("f"), 14);
            Text(panel, T("Received") + " · " + incident.ReceivedAtUtc.ToLocalTime().ToString("f"), 14);
            if (incident.AcknowledgedAtUtc.HasValue) Text(panel, T("Last acknowledgement") + " · " + incident.AcknowledgedAtUtc.Value.ToLocalTime().ToString("f"), 14);
            if (incident.MitigatedAtUtc.HasValue) Text(panel, T("Mitigated") + " · " + incident.MitigatedAtUtc.Value.ToLocalTime().ToString("f"), 14);
            if (incident.ResolvedAtUtc.HasValue) Text(panel, T("Resolved") + " · " + incident.ResolvedAtUtc.Value.ToLocalTime().ToString("f"), 14);
            TextButton(panel, "Copy incident ID", () => Copy(incident.Id.ToString()));
        }));
        if (!string.IsNullOrWhiteSpace(incident.ContextJson)) TextButton(technical, "View context", () => Json("Context", incident.ContextJson));
        if (!string.IsNullOrWhiteSpace(incident.MetadataJson)) TextButton(technical, "View metadata", () => Json("Metadata", incident.MetadataJson));
        TextButton(technical, "Open on the web", () => OpenWeb("/Incidents/Details/" + _id));
        Dock.Visibility = incident.CanManage ? ViewStates.Visible : ViewStates.Gone;
        if (incident.CanManage)
        {
            var operation = !incident.AcknowledgedAtUtc.HasValue ? "acknowledge" : incident.Status == "new" ? "mitigate" : incident.Status == "mitigated" ? "resolve" : "comment";
            Button(Dock, OperationName(operation), () => Edit(operation), true);
        }
    }
    private void Markdown(LinearLayout parent, string value)
    {
        var view = new MarkdownView(this, value, IsDark, Session.Endpoint);
        _readers.Add(view);
        parent.AddView(view, new LinearLayout.LayoutParams(-1, Dp(60)));
    }
    private void ClearReaders()
    {
        foreach (var view in _readers)
        {
            (view.Parent as ViewGroup)?.RemoveView(view);
            view.StopLoading(); view.Destroy(); view.Dispose();
        }
        _readers.Clear();
    }
    protected override void OnDestroy()
    {
        ClearReaders();
        base.OnDestroy();
    }
    private void Person(LinearLayout parent, string label, string value)
    {
        var row = Row(parent);
        var heading = Text(row, label, 13);
        heading.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1);
        var name = Text(row, value, 15, true, localize: false);
        name.Gravity = GravityFlags.End;
        name.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1.35f);
    }
    private static string Names(List<string> names) => names.Count == 0 ? T("Not recorded") : string.Join(", ", names);
    internal static string OperationName(string action) => T(action switch
    {
        "acknowledge" => "Acknowledge and take ownership", "take-ownership" => "Take ownership", "mitigate" => "Record mitigation",
        "resolve" => "Resolve incident", "reopen" => "Reopen incident", "severity" => "Adjust severity", _ => "Add comment"
    });
    private static string ActivityName(string action) => action switch
    {
        "acked" => "Acknowledged", "created" => "Incident reported", "mitigated" => "Mitigation recorded", "resolved" => "Incident resolved",
        "reopened" => "Incident reopened", "level_changed" => "Severity adjusted", "comment" => "Comment", "ownership_changed" => "Ownership changed", _ => "Updated"
    };
    private void Edit(string action) => StartActivity(new Intent(this, typeof(IncidentEditorActivity)).PutExtra("incidentId", _id.ToString()).PutExtra("operation", action));
    private void ShowActions()
    {
        if (_details == null) return;
        var incident = _details.Incident;
        Sheet("Incident actions", (panel, sheet) =>
        {
            if (incident.CanManage)
            {
                var actions = new List<string> { "acknowledge", "take-ownership", "comment" };
                if (incident.Status == "new" && incident.AcknowledgedAtUtc.HasValue) actions.Add("mitigate");
                if (incident.Status == "mitigated") actions.Add("resolve");
                if (incident.Status is "new" or "mitigated") actions.Add("severity");
                if (incident.Status is "mitigated" or "resolved") actions.Add("reopen");
                foreach (var action in actions) TextButton(panel, OperationName(action), () => { sheet.Dismiss(); Edit(action); });
            }
            TextButton(panel, "Share incident", () =>
            {
                sheet.Dismiss();
                var share = new Intent(Intent.ActionSend).SetType("text/plain")!
                    .PutExtra(Intent.ExtraSubject, incident.Title)
                    .PutExtra(Intent.ExtraText, Session.Endpoint.TrimEnd('/') + "/Incidents/Details/" + _id);
                StartActivity(Intent.CreateChooser(share, T("Share incident")));
            });
            TextButton(panel, "Copy link", () => Copy(Session.Endpoint.TrimEnd('/') + "/Incidents/Details/" + _id));
            if (incident.CanDelete) TextButton(panel, "Permanently delete incident", () => { sheet.Dismiss(); Delete(); });
        });
    }
    private void Delete()
    {
        Sheet("Permanently delete incident", (panel, sheet) =>
        {
            Text(panel, "This deletes the incident and its history. Enter its exact title to confirm.", 14);
            Text(panel, _details!.Incident.Title, 16, true, localize: false);
            var confirmation = Input(panel, "Incident title");
            Button(panel, "Delete permanently", () =>
            {
                if (confirmation.Text != _details.Incident.Title) { confirmation.Error = T("Enter the exact incident title."); return; }
                _ = RunAsync(async () =>
                {
                    await Session.RequireApi().DeleteIncidentAsync(_id, new MobileDeleteIncidentRequest { Version = _details.Incident.Version, ConfirmationTitle = confirmation.Text! });
                    sheet.Dismiss(); Session.Feedback = "Incident deleted."; Finish();
                });
            });
        });
    }
    private void ConfirmDeleteComment(MobileActivity item)
    {
        Sheet("Delete comment?", (panel, sheet) =>
        {
            Text(panel, "The timeline will retain a record of this deletion.", 14);
            Button(panel, "Delete comment", () => _ = RunAsync(async () =>
            {
                await Session.RequireApi().DeleteCommentAsync(_id, item.Id);
                sheet.Dismiss(); await ReloadAsync(); ToastSuccess("Comment deleted.");
            }));
        });
    }
    private void Copy(string value)
    {
        ((ClipboardManager)GetSystemService(ClipboardService)!).PrimaryClip = ClipData.NewPlainText("AlertCenter", value);
        ToastSuccess("Copied.");
    }
    private void Json(string title, string value)
    {
        try { value = System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonDocument.Parse(value), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }); }
        catch (System.Text.Json.JsonException) { }
        Sheet(title, (panel, _) =>
        {
            var text = Text(panel, value, 14, localize: false); text.Typeface = global::Android.Graphics.Typeface.Monospace; text.SetTextIsSelectable(true);
            TextButton(panel, "Copy JSON", () => Copy(value));
        });
    }
}
