using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Content.Res;
using Orientation = Android.Widget.Orientation;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Aiursoft.AlertCenter.SDK.Models;
using Google.Android.Material.Button;

namespace Aiursoft.AlertCenter.Android;

[Activity(Label = "AlertCenter", Exported = false, Theme = "@style/AppTheme")]
public sealed class MainActivity : WorkspaceActivity
{
    private MobileIncidentQuery _query = new();
    private List<MobileApplication> _applications = [];
    private readonly List<MobileIncident> _incidents = [];
    private RecyclerView _recycler = null!;
    private LinearLayout _queue = null!;
    private LinearLayout _filters = null!;
    private IncidentAdapter _adapter = null!;
    private int _tab;
    private bool _hasMore;
    private bool _loaded;
    private int _restorePosition;
    private readonly Dictionary<int, MobileIncidentQuery> _queries = [];
    private readonly Dictionary<int, int> _positions = [];

    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state);
        _tab = state?.GetInt("tab", 0) ?? 0;
        _restorePosition = state?.GetInt("position", 0) ?? 0;
        var saved = state?.GetString("query");
        if (saved != null) _query = JsonSerializer.Deserialize<MobileIncidentQuery>(saved) ?? new();
        Setup("Incidents");
        Toolbar.Menu!.Add(0, 1, 0, T("Search"))!.SetIcon(Resource.Drawable.ic_search)!.SetShowAsAction(ShowAsAction.Always);
        Toolbar.Menu!.Add(0, 2, 4, T("New incident"))!.SetShowAsAction(ShowAsAction.Never);
        Toolbar.Menu!.Add(0, 5, 1, T("Filters"))!.SetIcon(Resource.Drawable.ic_filter)!.SetShowAsAction(ShowAsAction.Always);
        Toolbar.Menu!.Add(0, 3, 2, T("Account and settings"))!.SetIcon(Resource.Drawable.ic_person)!.SetShowAsAction(ShowAsAction.Always);
        Toolbar.Menu!.Add(0, 4, 3, T("Refresh"))!.SetShowAsAction(ShowAsAction.Never);
        Toolbar.MenuItemClick += (_, e) =>
        {
            if (IsBusy) return;
            switch (e.Item!.ItemId)
            {
                case 1: ShowFilters(true); break;
                case 2: StartActivity(typeof(CreateIncidentActivity)); break;
                case 3: StartActivity(typeof(SettingsActivity)); break;
                case 4: _ = RunAsync(ReloadAsync); break;
                case 5: ShowFilters(false); break;
            }
        };
        _queue = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _filters = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _filters.SetPadding(Dp(20), Dp(4), Dp(20), Dp(8));
        _queue.AddView(_filters);
        _recycler = new RecyclerView(this);
        _recycler.SetLayoutManager(new LinearLayoutManager(this));
        _adapter = new IncidentAdapter(this);
        _recycler.SetAdapter(_adapter);
        _recycler.SetPadding(Dp(20), 0, Dp(20), Dp(12));
        _recycler.SetClipToPadding(false);
        _queue.AddView(_recycler, new LinearLayout.LayoutParams(-1, 0, 1));
        Content.AddView(_queue, new FrameLayout.LayoutParams(-1, -1));
        Dock.Visibility = ViewStates.Visible;
        BuildNavigation();
        Toolbar.Menu!.FindItem(2)!.SetVisible(false);
        RenderFilters();
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (Session.IsAuthenticated) _ = RunAsync(ReloadAsync);
    }

    protected override void OnSaveInstanceState(Bundle state)
    {
        state.PutString("query", JsonSerializer.Serialize(_query));
        state.PutInt("tab", _tab);
        state.PutInt("position", Position());
        base.OnSaveInstanceState(state);
    }
    private int Position() => (_recycler.GetLayoutManager() as LinearLayoutManager)?.FindFirstVisibleItemPosition() ?? 0;

    protected override async Task ReloadAsync()
    {
        var api = Session.RequireApi();
        var position = _loaded ? Position() : _restorePosition;
        var user = await api.GetUserAsync();
        Session.UpdateDisplayName(user.DisplayName);
        _applications = (await api.GetApplicationsAsync()).Applications;
        Toolbar.Menu!.FindItem(2)!.SetVisible(_applications.Any(item => item.CanCreate));
        if (_tab == 1) { RenderApplications(); ShowFeedback(); return; }
        // Re-read the visible pages without losing the user's place after returning from a detail.
        var pages = Math.Max(1, _query.Page);
        var items = new List<MobileIncident>();
        var request = JsonSerializer.Deserialize<MobileIncidentQuery>(JsonSerializer.Serialize(_query))!;
        MobileIncidentsResponse? last = null;
        for (var page = 1; page <= pages; page++)
        {
            request.Page = page;
            last = await api.GetIncidentsAsync(request);
            items.AddRange(last.Incidents);
            if (!last.HasNextPage) break;
        }
        _query.Page = last!.Page;
        _hasMore = last.HasNextPage;
        _incidents.Clear();
        _incidents.AddRange(items.DistinctBy(item => item.Id));
        _loaded = true;
        RenderFilters();
        _adapter.NotifyDataSetChanged();
        ShowFeedback();
        if (position > 0) _recycler.ScrollToPosition(Math.Min(position, _incidents.Count));
    }

    private void ShowFeedback()
    {
        if (Session.Feedback == null) return;
        ToastSuccess(Session.Feedback); Session.Feedback = null;
    }
    private async Task LoadMoreAsync()
    {
        var request = JsonSerializer.Deserialize<MobileIncidentQuery>(JsonSerializer.Serialize(_query))!;
        request.Page++;
        var response = await Session.RequireApi().GetIncidentsAsync(request);
        _query.Page = response.Page;
        _hasMore = response.HasNextPage;
        _incidents.AddRange(response.Incidents.Where(item => _incidents.All(existing => existing.Id != item.Id)));
        _adapter.NotifyDataSetChanged();
    }

    private void BuildNavigation()
    {
        Dock.RemoveAllViews();
        Dock.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));
        var row = Row(Dock);
        var labels = new[] { "Incidents", "Applications", "Mine" };
        var icons = new[] { Resource.Drawable.ic_notifications, Resource.Drawable.ic_apps, Resource.Drawable.ic_person };
        for (var i = 0; i < labels.Length; i++)
        {
            var tab = i;
            var item = new LinearLayout(this) { Orientation = Orientation.Vertical, Clickable = true, Focusable = true };
            item.SetGravity(GravityFlags.Center);
            item.SetPadding(Dp(4), Dp(6), Dp(4), Dp(4));
            item.ContentDescription = T(labels[i]);
            item.Selected = tab == _tab;
            var ripple = new global::Android.Util.TypedValue();
            Theme!.ResolveAttribute(global::Android.Resource.Attribute.SelectableItemBackground, ripple, true);
            item.SetBackgroundResource(ripple.ResourceId);
            var image = new ImageView(this) { ImportantForAccessibility = ImportantForAccessibility.No };
            image.SetImageResource(icons[i]);
            image.ImageTintList = ColorStateList.ValueOf(Color(tab == _tab ? Resource.Color.brand_primary : Resource.Color.text_secondary));
            item.AddView(image, new LinearLayout.LayoutParams(Dp(24), Dp(24)));
            var label = Text(item, labels[i], 12, tab == _tab);
            label.ImportantForAccessibility = ImportantForAccessibility.No;
            label.Gravity = GravityFlags.Center;
            label.SetTextColor(Color(tab == _tab ? Resource.Color.brand_primary : Resource.Color.text_secondary));
            row.AddView(item, new LinearLayout.LayoutParams(0, -2, 1));
            item.Click += (_, _) => { if (!IsBusy) SwitchTab(tab); };
        }
    }

    private void SwitchTab(int tab)
    {
        if (_tab != 1) { _queries[_tab] = _query; _positions[_tab] = Position(); }
        _tab = tab;
        _query = _queries.GetValueOrDefault(tab) ?? new MobileIncidentQuery { OnlyMine = tab == 2 };
        _restorePosition = _positions.GetValueOrDefault(tab);
        _loaded = false;
        Toolbar.Title = T(tab == 1 ? "Applications" : tab == 2 ? "My incidents" : "Incidents");
        _queue.Visibility = tab == 1 ? ViewStates.Gone : ViewStates.Visible;
        ContentScroll.Visibility = tab == 1 ? ViewStates.Visible : ViewStates.Gone;
        Toolbar.Menu!.FindItem(1)!.SetVisible(tab != 1);
        Toolbar.Menu!.FindItem(5)!.SetVisible(tab != 1);
        BuildNavigation();
        _ = RunAsync(ReloadAsync);
    }

    private void RenderFilters()
    {
        _queue.Visibility = _tab == 1 ? ViewStates.Gone : ViewStates.Visible;
        ContentScroll.Visibility = _tab == 1 ? ViewStates.Visible : ViewStates.Gone;
        Toolbar.Title = T(_tab == 1 ? "Applications" : _tab == 2 ? "My incidents" : "Incidents");
        Toolbar.Menu!.FindItem(1)!.SetVisible(_tab != 1);
        Toolbar.Menu!.FindItem(5)!.SetVisible(_tab != 1);
        _filters.RemoveAllViews();
        Text(_filters, _query.ApplicationId.HasValue
            ? _applications.FirstOrDefault(item => item.Id == _query.ApplicationId)?.Name ?? T("Selected application")
            : _tab == 2 ? T("Assigned to you · across your applications") : T("Across your applications"), 14);
        var scroll = new HorizontalScrollView(this) { HorizontalScrollBarEnabled = false };
        var choices = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        scroll.AddView(choices);
        _filters.AddView(scroll);
        foreach (var status in new[] { "new", "mitigated", "resolved", "all" })
        {
            var chip = Button(choices, ProgressName(status), () =>
            {
                _query.Status = status; ResetPage(); RenderFilters(); _ = RunAsync(ReloadAsync);
            }, _query.Status == status);
            chip.LayoutParameters = new LinearLayout.LayoutParams(-2, -2) { RightMargin = Dp(7) };
            chip.SetMinWidth(0);
            chip.SetPadding(Dp(14), 0, Dp(14), 0);
            chip.TextSize = 13;
            chip.Selected = _query.Status == status;
        }
        var active = new List<string>();
        if (!string.IsNullOrWhiteSpace(_query.Keyword)) active.Add(_query.Keyword);
        if (_query.Acknowledged.HasValue) active.Add(T(_query.Acknowledged.Value ? "Acknowledged" : "Not acknowledged"));
        if (_query.Level != null) active.Add(_query.Level.ToUpperInvariant());
        if (_query.FromDate.HasValue || _query.ToDate.HasValue) active.Add($"{_query.FromDate:yyyy-MM-dd} – {_query.ToDate:yyyy-MM-dd} UTC");
        if (active.Count > 0)
            TextButton(_filters, string.Join(" · ", active) + "  ×", () =>
            {
                _query.Keyword = null; _query.Acknowledged = null; _query.Level = null; _query.FromDate = null; _query.ToDate = null;
                ResetPage(); _ = RunAsync(ReloadAsync);
            });

    }
    private void ResetPage() { _query.Page = 1; _restorePosition = 0; _loaded = false; _recycler.ScrollToPosition(0); }

    private void RenderApplications()
    {
        _queue.Visibility = ViewStates.Gone;
        ContentScroll.Visibility = ViewStates.Visible;
        Body.RemoveAllViews();
        Text(Body, "Your workspaces", 28, true);
        Text(Body, "Choose an application to see its incidents.", 14);
        foreach (var application in _applications)
        {
            var panel = Card(Body);
            var heading = Row(panel);
            Pill(heading, application.Name[..1].ToUpperInvariant(), Resource.Color.brand_container, Resource.Color.on_brand_container);
            var name = Text(heading, application.Name, 18, true, localize: false);
            name.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1);
            Text(panel, application.CanView ? "Incident workspace" : "Report-only access", 13);
            if (application.CanView)
                TextButton(panel, "View incidents", () =>
                {
                    _queries[0] = new MobileIncidentQuery { ApplicationId = application.Id };
                    _positions[0] = 0; SwitchTab(0);
                });
            else if (application.CanCreate)
                TextButton(panel, "New incident", () => StartActivity(new Intent(this, typeof(CreateIncidentActivity)).PutExtra("applicationId", application.Id.ToString())));
        }
        if (_applications.Count == 0) Text(Body, "No applications are available to this account.");
        TextButton(Body, "Manage applications on the web", () => OpenWeb("/Applications/Applications"));
    }

    private void ShowFilters(bool focusSearch)
    {
        Sheet("Find incidents", (panel, sheet) =>
        {
            var keyword = Input(panel, "Search title, content or application", _query.Keyword);
            var apps = _applications.Where(item => item.CanView).ToArray();
            var appIndex = Array.FindIndex(apps, item => item.Id == _query.ApplicationId) + 1;
            Choice(panel, "Application", new[] { T("All applications") }.Concat(apps.Select(item => item.Name)).ToArray(), appIndex, i => appIndex = i, localizeOptions: false);
            string?[] levels = [null, "sev0", "sev1", "lte-sev1", "sev2", "lte-sev2", "sev3", "lte-sev3", "sev4", "lte-sev4"];
            var levelIndex = Math.Max(0, Array.IndexOf(levels, _query.Level));
            Choice(panel, "Severity", ["Configured visibility", "SEV0 · Critical", "SEV1 · Major", "SEV0–1", "SEV2 · Error", "SEV0–2", "SEV3 · Warning", "SEV0–3", "SEV4 · Information", "All severities"], levelIndex, i => levelIndex = i);
            var ackIndex = _query.Acknowledged.HasValue ? _query.Acknowledged.Value ? 1 : 2 : 0;
            Choice(panel, "Incident acknowledgement", ["Any", "Acknowledged", "Not acknowledged"], ackIndex, i => ackIndex = i);
            DateOnly? from = _query.FromDate, to = _query.ToDate;
            Text(panel, "Received date · UTC", 14, true);
            MaterialButton fromButton = null!, toButton = null!;
            void Date(bool start)
            {
                var value = (start ? from : to) ?? DateOnly.FromDateTime(DateTime.UtcNow);
                var picker = new DatePickerDialog(this, (_, e) =>
                {
                    var selected = DateOnly.FromDateTime(e.Date);
                    if (start) { from = selected; fromButton.Text = T("From") + "  " + selected.ToString("yyyy-MM-dd"); }
                    else { to = selected; toButton.Text = T("Through") + "  " + selected.ToString("yyyy-MM-dd"); }
                }, value.Year, value.Month - 1, value.Day);
                picker.Show();
            }
            fromButton = TextButton(panel, T("From") + "  " + (from?.ToString("yyyy-MM-dd") ?? T("Any date")), () => Date(true));
            toButton = TextButton(panel, T("Through") + "  " + (to?.ToString("yyyy-MM-dd") ?? T("Any date")), () => Date(false));
            var error = Text(panel, string.Empty, 14);
            Button(panel, "Show results", () =>
            {
                if (from.HasValue && to.HasValue && from > to) { error.Text = T("The end date must not precede the start date."); return; }
                _query.Keyword = keyword.Text?.Trim(); _query.Level = levels[levelIndex];
                _query.Acknowledged = ackIndex == 0 ? null : ackIndex == 1;
                _query.ApplicationId = appIndex == 0 ? null : apps[appIndex - 1].Id;
                _query.FromDate = from; _query.ToDate = to;
                ResetPage(); sheet.Dismiss(); _ = RunAsync(ReloadAsync);
            }, true);
            TextButton(panel, "Clear filters", () =>
            {
                _query = new MobileIncidentQuery { OnlyMine = _tab == 2, Status = _query.Status };
                ResetPage(); sheet.Dismiss(); _ = RunAsync(ReloadAsync);
            });
            if (focusSearch) keyword.RequestFocus();
        });
    }

    private void RenderIncident(LinearLayout host, MobileIncident incident)
    {
        var panel = Card(host);
        var tags = Row(panel);
        Pill(tags, incident.Level.ToUpperInvariant(), incident.Level is "sev0" or "sev1" ? Resource.Color.danger_container : incident.Level is "sev2" or "sev3" ? Resource.Color.warning_container : Resource.Color.brand_container,
            incident.Level is "sev0" or "sev1" ? Resource.Color.on_danger_container : incident.Level is "sev2" or "sev3" ? Resource.Color.on_warning_container : Resource.Color.on_brand_container);
        Pill(tags, ProgressName(incident.Status));
        Text(panel, incident.Title, 18, true, localize: false);
        Text(panel, $"{incident.ApplicationName} · #{incident.ShortId}", 13);
        var people = new LinearLayout(this) { Orientation = Orientation.Vertical };
        people.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));
        var peopleShape = new global::Android.Graphics.Drawables.GradientDrawable();
        peopleShape.SetColor(Color(Resource.Color.surface_variant));
        peopleShape.SetCornerRadius(Dp(12));
        people.Background = peopleShape;
        panel.AddView(people, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(8), BottomMargin = Dp(6) });
        Text(people, T("Owner") + "  " + (incident.AcknowledgedAtUtc.HasValue ? incident.OwnerName ?? "—" : "—"), 14);
        Text(people, T("Original on-call") + "  " + (incident.OriginalOces == null ? T("Not recorded") : incident.OriginalOces.Count == 0 ? T("No on-call engineer") : string.Join(", ", incident.OriginalOces)), 14);
        Text(panel, (incident.AcknowledgedAtUtc.HasValue ? T("Acknowledged") : T("Not acknowledged")) + " · " + incident.ReceivedAtUtc.ToLocalTime().ToString("g"), 12);
        var card = (View)panel.Parent!;
        card.Clickable = true; card.Focusable = true;
        card.ContentDescription = $"{incident.Title}. {SeverityName(incident.Level)}. {ProgressName(incident.Status)}. {T("Owner")}: {(incident.AcknowledgedAtUtc.HasValue ? incident.OwnerName ?? T("Not acknowledged") : T("Not acknowledged"))}";
        card.Click += (_, _) => StartActivity(new Intent(this, typeof(IncidentActivity)).PutExtra("incidentId", incident.Id.ToString()));
    }

    private sealed class IncidentHolder(LinearLayout view) : RecyclerView.ViewHolder(view)
    {
        public LinearLayout Panel { get; } = view;
    }
    private sealed class IncidentAdapter(MainActivity owner) : RecyclerView.Adapter
    {
        public override int ItemCount => owner._incidents.Count + 1;
        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var layout = new LinearLayout(owner) { Orientation = Orientation.Vertical };
            layout.LayoutParameters = new RecyclerView.LayoutParams(-1, -2);
            return new IncidentHolder(layout);
        }
        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            var panel = ((IncidentHolder)holder).Panel;
            panel.RemoveAllViews();
            if (position < owner._incidents.Count) { owner.RenderIncident(panel, owner._incidents[position]); return; }
            if (!owner._loaded) owner.Text(panel, "Loading incidents…", 14);
            else if (owner._incidents.Count == 0)
            {
                var card = owner.Card(panel);
                owner.Text(card, "Nothing here right now", 22, true);
                owner.Text(card, "Try another status or clear your filters.", 14);
                owner.TextButton(card, "Clear filters", () => { owner._query = new MobileIncidentQuery { OnlyMine = owner._tab == 2, Status = "all" }; owner.ResetPage(); _ = owner.RunAsync(owner.ReloadAsync); });
            }
            else if (owner._hasMore) owner.TextButton(panel, "Load more incidents", () => _ = owner.RunAsync(owner.LoadMoreAsync));
            else owner.Text(panel, "You are up to date", 13);
        }
    }
}
