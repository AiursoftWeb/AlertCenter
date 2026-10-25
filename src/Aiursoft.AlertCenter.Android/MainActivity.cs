using System.Text.Json;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using Aiursoft.AlertCenter.SDK.Models;
using Google.Android.Material.Card;
using AlertDialog = AndroidX.AppCompat.App.AlertDialog;

namespace Aiursoft.AlertCenter.Android;

[Activity(Label = "AlertCenter", Exported = false, Theme = "@style/AppTheme")]
public sealed class MainActivity : WorkspaceActivity
{
    private MobileIncidentQuery _query = new();
    private List<MobileApplication> _applications = [];
    private TextView _summary = null!;
    private LinearLayout _list = null!;
    private LinearLayout _paging = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var saved = savedInstanceState?.GetString("query");
        if (saved != null) _query = JsonSerializer.Deserialize<MobileIncidentQuery>(saved) ?? new();
        Setup("Incidents", drawer: true);
        Toolbar.Menu!.Add(0, 1, 0, "Refresh")!.SetIcon(Resource.Drawable.ic_refresh)!.SetShowAsAction(ShowAsAction.Always);
        Toolbar.MenuItemClick += (_, e) => { if (e.Item!.ItemId == 1) _ = RunAsync(ReloadAsync); };
        _summary = Text(Body, "Loading incidents…", 14);
        Button(Body, "Filters", ShowFilters);
        _list = new LinearLayout(this) { Orientation = Orientation.Vertical };
        Body.AddView(_list);
        _paging = new LinearLayout(this) { Orientation = Orientation.Vertical };
        Body.AddView(_paging);
    }
    protected override void OnResume()
    {
        base.OnResume();
        if (Session.IsAuthenticated) _ = RunAsync(ReloadAsync);
    }
    protected override void OnSaveInstanceState(Bundle outState)
    {
        outState.PutString("query", JsonSerializer.Serialize(_query));
        base.OnSaveInstanceState(outState);
    }
    protected override async Task ReloadAsync()
    {
        var api = Session.RequireApi();
        var user = await api.GetUserAsync();
        Session.UpdateDisplayName(user.DisplayName);
        _applications = (await api.GetApplicationsAsync()).Applications;
        var response = await api.GetIncidentsAsync(_query);
        RenderNavigation();
        Toolbar.Title = _query.OnlyMine ? "My incidents" : "Incidents";
        _summary.Text = $"{(_query.ApplicationId.HasValue ? _applications.FirstOrDefault(a => a.Id == _query.ApplicationId)?.Name ?? "Application" : "All applications")} · {ProgressName(_query.Status)} · {(_query.Level == "lte-sev3" ? "sev0–sev3" : _query.Level == "lte-sev4" || _query.Level == null ? "All severities" : _query.Level)}";
        _list.RemoveAllViews();
        if (response.Incidents.Count == 0) Text(_list, "No incidents match these filters.");
        foreach (var incident in response.Incidents)
        {
            var card = new MaterialCardView(this) { Radius = Dp(14), CardElevation = Dp(1) };
            var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
            layout.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));
            card.AddView(layout);
            var statusColor = incident.Status switch
            {
                "mitigated" => Resource.Color.warning_container,
                "resolved" => Resource.Color.success_container,
                _ => Resource.Color.danger_container
            };
            card.SetCardBackgroundColor(Resources!.GetColor(statusColor, Theme));
            Text(layout, $"{incident.Level.ToUpperInvariant()} · {ProgressName(incident.Status)} · {(incident.AcknowledgedAtUtc.HasValue ? "ACK" : "Not acknowledged")}", 13);
            Text(layout, $"#{incident.ShortId}  {incident.Title}", 19);
            Text(layout, $"{incident.ApplicationName} · {incident.OwnerName ?? "Unassigned"}\n{incident.ReceivedAtUtc.ToLocalTime():g}", 13);
            // The full card opens the incident, including its selectable summary text.
            card.Click += (_, _) => OpenIncident(incident.Id);
            foreach (var text in Enumerable.Range(0, layout.ChildCount).Select(layout.GetChildAt).OfType<TextView>())
            {
                text.SetTextIsSelectable(false);
                text.Click += (_, _) => OpenIncident(incident.Id);
            }
            _list.AddView(card, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(12) });
        }
        _paging.RemoveAllViews();
        ContentScroll.Post(() => ContentScroll.ScrollTo(0, 0));
        Text(_paging, $"Page {response.Page}", 13);
        if (_query.Page > 1) Button(_paging, "Previous page", () => { _query.Page--; _ = RunAsync(ReloadAsync); });
        if (response.HasNextPage) Button(_paging, "Next page", () => { _query.Page++; _ = RunAsync(ReloadAsync); });
    }
    private void OpenIncident(Guid id) => StartActivity(new Intent(this, typeof(IncidentActivity)).PutExtra("incidentId", id.ToString()));
    private void RenderNavigation()
    {
        Navigation.RemoveAllViews();
        Text(Navigation, "AlertCenter", 24);
        Text(Navigation, Session.DisplayName, 16);
        Button(Navigation, "All incidents", () => Navigate(false, null));
        Button(Navigation, "My incidents", () => Navigate(true, null));
        if (_applications.Any(a => a.CanCreate)) Button(Navigation, "New incident", () =>
        {
            Drawer!.CloseDrawers();
            StartActivity(typeof(CreateIncidentActivity));
        });
        Text(Navigation, "Applications", 13);
        foreach (var application in _applications.Where(a => a.CanView))
            Button(Navigation, application.Name, () => Navigate(false, application.Id));
        Text(Navigation, Session.Endpoint, 12);
        Button(Navigation, "Sign out / change server", () =>
        {
            Session.SignOut();
            StartActivity(new Intent(this, typeof(LoginActivity)).AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask));
            Finish();
        });
    }
    private void Navigate(bool mine, Guid? applicationId)
    {
        _query.OnlyMine = mine;
        _query.ApplicationId = applicationId;
        _query.Page = 1;
        Drawer!.CloseDrawers();
        _ = RunAsync(ReloadAsync);
    }
    private void ShowFilters()
    {
        if (IsBusy) return;
        var panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        panel.SetPadding(Dp(24), Dp(8), Dp(24), 0);
        var keyword = Input(panel, "Search title, content or application", _query.Keyword);
        var statuses = new[] { "new", "mitigated", "resolved", "all" };
        var status = Select(panel, "Progress", ["Active", "Mitigated", "Resolved", "All progress"], Array.IndexOf(statuses, _query.Status));
        var levels = new[] { "lte-sev3", "sev0", "sev1", "sev2", "sev3", "sev4", "lte-sev4" };
        var level = Select(panel, "Severity", ["sev0–sev3 (default)", "sev0", "sev1", "sev2", "sev3", "sev4", "All severities"], Array.IndexOf(levels, _query.Level));
        var ack = Select(panel, "Acknowledgement", ["All", "Acknowledged", "Not acknowledged"], _query.Acknowledged.HasValue ? _query.Acknowledged.Value ? 1 : 2 : 0);
        new AlertDialog.Builder(this).SetTitle("Filter incidents")!.SetView(panel)!
            .SetPositiveButton("Apply", (_, _) =>
            {
                _query.Keyword = keyword.Text;
                _query.Status = statuses[status.SelectedItemPosition];
                _query.Level = levels[level.SelectedItemPosition];
                _query.Acknowledged = ack.SelectedItemPosition == 0 ? null : ack.SelectedItemPosition == 1;
                _query.Page = 1;
                _ = RunAsync(ReloadAsync);
            })!.SetNeutralButton("Reset", (_, _) => { _query = new MobileIncidentQuery(); _ = RunAsync(ReloadAsync); })!
            .SetNegativeButton("Cancel", (_, _) => { })!.Show();
    }
}
