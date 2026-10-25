using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.DrawerLayout.Widget;
using AndroidX.Core.View;
using Aiursoft.AiurProtocol.Exceptions;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AlertCenter.Android.Oidc;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.TextField;
using AlertDialog = AndroidX.AppCompat.App.AlertDialog;

namespace Aiursoft.AlertCenter.Android;

public abstract class WorkspaceActivity : AppCompatActivity
{
    protected AppSession Session => ((AlertCenterApplication)Application!).Session;
    protected LinearLayout Body = null!;
    protected MaterialToolbar Toolbar = null!;
    protected LinearLayout Navigation = null!;
    protected DrawerLayout? Drawer;
    private ProgressBar _progress = null!;
    private bool _busy;
    protected bool IsBusy => _busy;
    protected ScrollView ContentScroll = null!;

    protected override void OnResume()
    {
        base.OnResume();
        if (!Session.IsAuthenticated) ReturnToLogin();
    }

    protected void Setup(string title, bool back = false, bool drawer = false)
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Resources!.GetColor(Resource.Color.app_bar, Theme));
        root.SetOnApplyWindowInsetsListener(new SystemBarInsetListener(0, 0, 0, 0, true, true));
        Toolbar = new MaterialToolbar(this) { Title = title };
        Toolbar.SetBackgroundColor(Resources.GetColor(Resource.Color.app_bar, Theme));
        Toolbar.SetTitleTextColor(Resources.GetColor(Resource.Color.on_app_bar, Theme));
        root.AddView(Toolbar, new LinearLayout.LayoutParams(-1, Dp(56)));
        _progress = new ProgressBar(this, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal)
        { Indeterminate = true, Visibility = ViewStates.Invisible };
        root.AddView(_progress, new LinearLayout.LayoutParams(-1, Dp(3)));
        var scroll = new ScrollView(this) { FillViewport = true };
        ContentScroll = scroll;
        scroll.SetBackgroundColor(Resources.GetColor(Resource.Color.surface_canvas, Theme));
        Body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        Body.SetPadding(Dp(20), Dp(16), Dp(20), Dp(24));
        scroll.AddView(Body, new ViewGroup.LayoutParams(-1, -2));
        root.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        if (drawer)
        {
            Drawer = new DrawerLayout(this);
            Drawer.AddView(root, new DrawerLayout.LayoutParams(-1, -1));
            var navScroll = new ScrollView(this);
            navScroll.SetBackgroundColor(Resources.GetColor(Resource.Color.surface, Theme));
            Navigation = new LinearLayout(this) { Orientation = Orientation.Vertical };
            Navigation.SetPadding(Dp(16), Dp(48), Dp(16), Dp(24));
            navScroll.AddView(Navigation);
            Drawer.AddView(navScroll, new DrawerLayout.LayoutParams(Dp(300), -1) { Gravity = (int)GravityFlags.Start });
            SetContentView(Drawer);
            Toolbar.SetNavigationIcon(Resource.Drawable.ic_menu);
            Toolbar.NavigationContentDescription = "Open navigation";
            Toolbar.NavigationClick += (_, _) => Drawer.OpenDrawer((int)GravityFlags.Start);
        }
        else
        {
            SetContentView(root);
            if (back)
            {
                Toolbar.SetNavigationIcon(Resource.Drawable.ic_arrow_back);
                Toolbar.NavigationContentDescription = "Back";
                Toolbar.NavigationClick += (_, _) => Finish();
            }
        }
        WindowCompat.GetInsetsController(Window!, root)!.AppearanceLightStatusBars = false;
    }

    protected int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density + .5f);
    protected TextView Text(LinearLayout parent, string value, float size = 16)
    {
        var view = new TextView(this) { Text = value, TextSize = size };
        view.SetTextColor(Resources!.GetColor(Resource.Color.text_primary, Theme));
        view.SetTextIsSelectable(true);
        view.SetPadding(0, Dp(5), 0, Dp(8));
        parent.AddView(view, new LinearLayout.LayoutParams(-1, -2));
        return view;
    }
    protected MaterialButton Button(LinearLayout parent, string label, Action action)
    {
        var button = new MaterialButton(this) { Text = label };
        button.SetAllCaps(false);
        parent.AddView(button, new LinearLayout.LayoutParams(-1, -2));
        button.Click += (_, _) => { if (!_busy) action(); };
        return button;
    }
    protected TextInputEditText Input(LinearLayout parent, string label, string? value = null, bool multiline = false)
    {
        var layout = new TextInputLayout(this) { Hint = label, BoxBackgroundMode = TextInputLayout.BoxBackgroundOutline };
        var input = new TextInputEditText(this) { Text = value ?? string.Empty };
        input.SetSingleLine(!multiline);
        if (multiline) input.SetMinLines(3);
        layout.AddView(input, new LinearLayout.LayoutParams(-1, -2));
        parent.AddView(layout, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(10), BottomMargin = Dp(6) });
        return input;
    }
    protected Spinner Select(LinearLayout parent, string label, string[] options, int selected = 0)
    {
        Text(parent, label, 13);
        var spinner = new Spinner(this) { Adapter = new ArrayAdapter<string>(this,
            global::Android.Resource.Layout.SimpleSpinnerDropDownItem, options) };
        spinner.SetSelection(Math.Max(0, selected));
        parent.AddView(spinner, new LinearLayout.LayoutParams(-1, Dp(48)));
        return spinner;
    }
    protected static string ProgressName(string status) => status switch
    {
        "new" => "Active", "mitigated" => "Mitigated", "resolved" => "Resolved", _ => "All progress"
    };
    protected virtual Task ReloadAsync() => Task.CompletedTask;
    protected async Task RunAsync(Func<Task> action, View? control = null)
    {
        if (_busy || IsFinishing || IsDestroyed) return;
        _busy = true;
        _progress.Visibility = ViewStates.Visible;
        if (control != null) control.Enabled = false;
        try { await action(); }
        catch (Exception exception)
        {
            if (IsFinishing || IsDestroyed) return;
            if (exception is AlertCenterAuthenticationRequiredException ||
                exception is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
            {
                Session.SignOut();
                ReturnToLogin();
                return;
            }
            var response = exception switch
            {
                AiurUnexpectedServerResponseException unexpected => unexpected.Response,
                AiurServerException server => server.Response,
                _ => null
            };
            if (response?.Code == Code.WrongKey)
            {
                Session.SignOut();
                ReturnToLogin();
                return;
            }
            if (response?.Code == Code.Conflict)
                new AlertDialog.Builder(this).SetTitle("Incident changed")!
                    .SetMessage("Another engineer changed this incident. Refresh before trying again. Your draft is kept.")!
                    .SetPositiveButton("Refresh", (_, _) => _ = RunAsync(ReloadAsync))!.SetNegativeButton("Cancel", (_, _) => { })!.Show();
            else
                new AlertDialog.Builder(this).SetTitle("Could not complete the request")!
                    .SetMessage(exception is HttpRequestException ? "Check your connection and try refreshing. A submitted action may already have completed." : exception.Message)!
                    .SetPositiveButton("OK", (_, _) => { })!.Show();
        }
        finally
        {
            _busy = false;
            if (!IsDestroyed)
            {
                _progress.Visibility = ViewStates.Invisible;
                if (control != null) control.Enabled = true;
            }
        }
    }
    private void ReturnToLogin()
    {
        StartActivity(new Intent(this, typeof(LoginActivity)).AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask));
        Finish();
    }
}
