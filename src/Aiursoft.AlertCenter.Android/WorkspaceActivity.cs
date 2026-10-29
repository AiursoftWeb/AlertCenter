using Android.Content;
using Android.Content.Res;
using Orientation = Android.Widget.Orientation;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using Aiursoft.AiurProtocol.Exceptions;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AlertCenter.Android.Oidc;
using Google.Android.Material.AppBar;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.Snackbar;
using Google.Android.Material.TextField;
using AlertDialog = AndroidX.AppCompat.App.AlertDialog;

namespace Aiursoft.AlertCenter.Android;

public abstract class WorkspaceActivity : AppCompatActivity
{
    protected AppSession Session => ((AlertCenterApplication)Application!).Session;
    protected LinearLayout Body = null!;
    protected LinearLayout Root = null!;
    protected LinearLayout Dock = null!;
    protected FrameLayout Content = null!;
    protected MaterialToolbar Toolbar = null!;
    protected ScrollView ContentScroll = null!;
    private ProgressBar _progress = null!;
    private LinearLayout _error = null!;
    private LinearLayout? _sheetError;
    private bool _busy;
    private readonly List<View> _actions = [];
    protected bool IsBusy => _busy;
    protected bool IsDark => (Resources!.Configuration!.UiMode & UiMode.NightMask) == UiMode.NightYes;
    protected static string T(string value) => UiText.Get(value);
    protected Color Color(int resource) => Resources!.GetColor(resource, Theme);
    protected int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density + .5f);

    protected override void OnResume()
    {
        base.OnResume();
        UiText.ApplyLanguage(this);
        if (!Session.IsAuthenticated) ReturnToLogin();
    }

    protected void Setup(string title, bool back = false, bool drawer = false)
    {
        Root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        Root.SetBackgroundColor(Color(Resource.Color.surface_canvas));
        Root.SetOnApplyWindowInsetsListener(new SystemBarInsetListener(0, 0, 0, 0, true, true));
        Toolbar = new MaterialToolbar(this) { Title = T(title) };
        Toolbar.SetBackgroundColor(Color(Resource.Color.surface_canvas));
        Toolbar.SetTitleTextColor(Color(Resource.Color.text_primary));
        Toolbar.SetNavigationIconTint(Color(Resource.Color.text_primary));
        Root.AddView(Toolbar, new LinearLayout.LayoutParams(-1, Dp(64)));
        _progress = new ProgressBar(this, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal)
        { Indeterminate = true, Visibility = ViewStates.Invisible };
        _progress.IndeterminateTintList = ColorStateList.ValueOf(Color(Resource.Color.brand_primary));
        Root.AddView(_progress, new LinearLayout.LayoutParams(-1, Dp(2)));
        _error = new LinearLayout(this) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
        _error.SetPadding(Dp(20), Dp(8), Dp(20), Dp(8));
        _error.SetBackgroundColor(Color(Resource.Color.warning_container));
        Root.AddView(_error);
        Content = new FrameLayout(this);
        ContentScroll = new ScrollView(this) { FillViewport = true };
        Body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        Body.SetPadding(Dp(20), Dp(8), Dp(20), Dp(24));
        ContentScroll.AddView(Body, new ViewGroup.LayoutParams(-1, -2));
        Content.AddView(ContentScroll, new FrameLayout.LayoutParams(-1, -1));
        Root.AddView(Content, new LinearLayout.LayoutParams(-1, 0, 1));
        Dock = new LinearLayout(this) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
        Dock.SetPadding(Dp(20), Dp(10), Dp(20), Dp(10));
        Dock.SetBackgroundColor(Color(Resource.Color.surface));
        Root.AddView(Dock, new LinearLayout.LayoutParams(-1, -2));
        SetContentView(Root);
        if (back)
        {
            Toolbar.SetNavigationIcon(Resource.Drawable.ic_arrow_back);
            Toolbar.NavigationContentDescription = T("Back");
            Toolbar.NavigationClick += (_, _) => RequestLeave();
            OnBackPressedDispatcher.AddCallback(this, new LeaveCallback(this));
        }
        WindowCompat.GetInsetsController(Window!, Root)!.AppearanceLightStatusBars = !IsDark;
        WindowCompat.GetInsetsController(Window!, Root)!.AppearanceLightNavigationBars = !IsDark;
        Window!.SetSoftInputMode(SoftInput.AdjustResize);
    }

    protected virtual bool HasUnsavedChanges => false;
    private void RequestLeave()
    {
        if (_busy) return;
        if (!HasUnsavedChanges) { Finish(); return; }
        new AlertDialog.Builder(this).SetTitle(T("Discard your draft?"))!
            .SetMessage(T("Your changes have not been submitted."))!
            .SetNegativeButton(T("Keep editing"), (_, _) => { })!
            .SetPositiveButton(T("Discard"), (_, _) => Finish())!.Show();
    }
    private sealed class LeaveCallback(WorkspaceActivity activity) : AndroidX.Activity.OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed() => activity.RequestLeave();
    }

    protected LinearLayout Row(LinearLayout parent)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        parent.AddView(row, new LinearLayout.LayoutParams(-1, -2));
        return row;
    }

    protected LinearLayout Card(LinearLayout parent, int tint = 0)
    {
        var card = new MaterialCardView(this) { Radius = Dp(20), CardElevation = 0, StrokeWidth = 0 };
        card.SetCardBackgroundColor(Color(tint == 0 ? Resource.Color.surface : tint));
        var panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        panel.SetPadding(Dp(18), Dp(16), Dp(18), Dp(16));
        card.AddView(panel);
        parent.AddView(card, new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(14) });
        return panel;
    }

    protected TextView Text(LinearLayout parent, string value, float size = 16, bool bold = false, bool secondary = false, bool localize = true)
    {
        var view = new TextView(this) { Text = localize ? T(value) : value, TextSize = size };
        view.SetTextColor(Color(secondary || size <= 14 ? Resource.Color.text_secondary : Resource.Color.text_primary));
        if (bold) view.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal), TypefaceStyle.Normal);
        view.SetPadding(0, Dp(3), 0, Dp(5));
        parent.AddView(view, new LinearLayout.LayoutParams(-1, -2));
        return view;
    }

    protected TextView Pill(LinearLayout parent, string value, int tint = 0, int ink = 0)
    {
        var text = new TextView(this) { Text = T(value), TextSize = 12 };
        text.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal), TypefaceStyle.Normal);
        text.SetTextColor(Color(ink == 0 ? Resource.Color.text_secondary : ink));
        text.SetPadding(Dp(9), Dp(5), Dp(9), Dp(5));
        var shape = new GradientDrawable();
        shape.SetColor(Color(tint == 0 ? Resource.Color.surface_variant : tint));
        shape.SetCornerRadius(Dp(8));
        text.Background = shape;
        parent.AddView(text, new LinearLayout.LayoutParams(-2, -2) { RightMargin = Dp(6), BottomMargin = Dp(8) });
        return text;
    }

    protected MaterialButton Button(LinearLayout parent, string label, Action action, bool primary = false)
    {
        var button = new MaterialButton(this) { Text = T(label), CornerRadius = Dp(14) };
        button.SetMinHeight(Dp(48));
        button.SetAllCaps(false);
        button.SetTextColor(Color(primary ? Resource.Color.on_brand_primary : Resource.Color.brand_primary));
        button.BackgroundTintList = ColorStateList.ValueOf(Color(primary ? Resource.Color.brand_primary : Resource.Color.brand_container));
        button.Elevation = 0;
        parent.AddView(button, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(3), BottomMargin = Dp(3) });
        button.Click += (_, _) => { if (!_busy) action(); };
        _actions.RemoveAll(item => !item.IsAttachedToWindow);
        _actions.Add(button);
        return button;
    }

    protected MaterialButton TextButton(LinearLayout parent, string label, Action action)
    {
        var button = Button(parent, label, action);
        button.BackgroundTintList = ColorStateList.ValueOf(global::Android.Graphics.Color.Transparent);
        return button;
    }

    protected TextInputEditText Input(LinearLayout parent, string label, string? value = null, bool multiline = false)
    {
        var layout = new TextInputLayout(this) { Hint = T(label), BoxBackgroundMode = TextInputLayout.BoxBackgroundOutline };
        layout.SetBoxCornerRadii(Dp(12), Dp(12), Dp(12), Dp(12));
        var input = new TextInputEditText(this) { Text = value ?? string.Empty, TextSize = 16 };
        input.SetSingleLine(!multiline);
        if (multiline) { input.SetMinLines(4); input.Gravity = GravityFlags.Top; }
        layout.AddView(input, new LinearLayout.LayoutParams(-1, -2));
        parent.AddView(layout, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(10), BottomMargin = Dp(8) });
        return input;
    }

    protected MaterialButton Choice(LinearLayout parent, string label, string[] options, int selected, Action<int> changed, bool localizeOptions = true)
    {
        string Option(int index) => localizeOptions ? T(options[index]) : options[index];
        var button = Button(parent, T(label) + "  ·  " + Option(Math.Clamp(selected, 0, options.Length - 1)), () => { });
        button.BackgroundTintList = ColorStateList.ValueOf(Color(Resource.Color.surface_variant));
        button.SetTextColor(Color(Resource.Color.text_primary));
        button.TextAlignment = TextAlignment.ViewStart;
        button.Click += (_, _) =>
        {
            if (_busy) return;
            new AlertDialog.Builder(this).SetTitle(T(label))!
                .SetSingleChoiceItems(options.Select(value => localizeOptions ? T(value) : value).ToArray(), selected, (sender, args) =>
                {
                    selected = args.Which;
                    button.Text = T(label) + "  ·  " + Option(selected);
                    changed(selected);
                    ((AlertDialog)sender!).Dismiss();
                })!.SetNegativeButton(T("Cancel"), (_, _) => { })!.Show();
        };
        return button;
    }

    protected BottomSheetDialog Sheet(string title, Action<LinearLayout, BottomSheetDialog> build)
    {
        var dialog = new BottomSheetDialog(this);
        var scroll = new ScrollView(this) { FillViewport = true };
        var panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        panel.SetPadding(Dp(24), Dp(16), Dp(24), Dp(32));
        panel.SetBackgroundColor(Color(Resource.Color.surface));
        Text(panel, title, 23, true);
        var feedback = new LinearLayout(this) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
        feedback.SetBackgroundColor(Color(Resource.Color.warning_container));
        feedback.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));
        panel.AddView(feedback);
        _sheetError = feedback;
        dialog.DismissEvent += (_, _) => { if (ReferenceEquals(_sheetError, feedback)) _sheetError = null; };
        scroll.AddView(panel);
        build(panel, dialog);
        dialog.SetContentView(scroll);
        dialog.Show();
        dialog.Window?.SetSoftInputMode(SoftInput.AdjustResize);
        return dialog;
    }

    protected void ToastSuccess(string value)
    {
        var snackbar = Snackbar.Make(Root, T(value), Snackbar.LengthLong);
        if (Dock.Visibility == ViewStates.Visible) snackbar.SetAnchorView(Dock);
        snackbar.Show();
    }
    protected void ShowMarkdownPreview(string value)
    {
        MarkdownView? preview = null;
        var sheet = Sheet("Preview", (panel, _) =>
        {
            if (string.IsNullOrWhiteSpace(value)) { Text(panel, "Nothing to preview yet.", 14); return; }
            preview = new MarkdownView(this, value, IsDark, Session.Endpoint);
            panel.AddView(preview, new LinearLayout.LayoutParams(-1, Dp(60)));
        });
        sheet.DismissEvent += (_, _) => { preview?.StopLoading(); preview?.Destroy(); preview?.Dispose(); };
    }
    protected void OpenWeb(string path)
    {
        try { StartActivity(new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(Session.Endpoint.TrimEnd('/') + path))); }
        catch (ActivityNotFoundException) { ToastSuccess("No browser is available."); }
    }
    protected static string ProgressName(string status) => T(status switch
    {
        "new" => "Unmitigated", "mitigated" => "Mitigated", "resolved" => "Resolved", _ => "All"
    });
    protected static string SeverityName(string level) => level.ToUpperInvariant() + " · " + T(level switch
    {
        "sev0" => "Critical", "sev1" => "Major", "sev2" => "Error", "sev3" => "Warning", _ => "Information"
    });
    protected virtual Task ReloadAsync() => Task.CompletedTask;
    protected async Task RunAsync(Func<Task> action, View? control = null)
    {
        if (_busy || IsFinishing || IsDestroyed) return;
        _busy = true;
        _progress.Visibility = ViewStates.Visible;
        _error.Visibility = ViewStates.Gone;
        if (_sheetError != null) _sheetError.Visibility = ViewStates.Gone;
        foreach (var item in _actions.Where(item => item.IsAttachedToWindow)) item.Enabled = false;
        if (control != null) control.Enabled = false;
        try { await action(); }
        catch (Exception exception)
        {
            if (IsFinishing || IsDestroyed) return;
            if (exception is AlertCenterAuthenticationRequiredException ||
                exception is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
            { Session.SignOut(); ReturnToLogin(); return; }
            var response = exception switch
            {
                AiurUnexpectedServerResponseException unexpected => unexpected.Response,
                AiurServerException server => server.Response, _ => null
            };
            if (response?.Code == Code.WrongKey) { Session.SignOut(); ReturnToLogin(); return; }
            var message = response?.Code switch
            {
                Code.Conflict => "This incident changed. Refresh before submitting again. Your draft is kept.",
                Code.Unauthorized => "You no longer have permission to do this. Refresh to check your access.",
                Code.NotFound => "This item is no longer available.",
                _ => exception is HttpRequestException
                    ? "Could not connect. Existing information may be out of date. A submitted action may already have completed."
                    : response?.Message ?? "Could not complete the request. Please try again."
            };
            var error = _sheetError ?? _error;
            error.RemoveAllViews();
            Text(error, message, 14);
            TextButton(error, "Refresh", () => _ = RunAsync(ReloadAsync));
            error.Visibility = ViewStates.Visible;
            if (_sheetError == null) ToastSuccess("Request failed. Your input is kept.");
        }
        finally
        {
            _busy = false;
            if (!IsDestroyed)
            {
                _progress.Visibility = ViewStates.Invisible;
                _actions.RemoveAll(item => !item.IsAttachedToWindow);
                foreach (var item in _actions) item.Enabled = true;
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
