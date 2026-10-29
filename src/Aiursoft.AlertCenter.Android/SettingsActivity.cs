using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.AppCompat.App;

namespace Aiursoft.AlertCenter.Android;

[Activity(Label = "Account", Exported = false, Theme = "@style/AppTheme")]
public sealed class SettingsActivity : WorkspaceActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Setup("Account and settings", back: true);
        var profile = Card(Body);
        Pill(profile, Session.DisplayName.Length > 0 ? Session.DisplayName[..1].ToUpperInvariant() : "A", Resource.Color.brand_container, Resource.Color.on_brand_container);
        Text(profile, Session.DisplayName, 26, true, localize: false);
        Text(profile, new Uri(Session.Endpoint).Host, 14);
        var appearance = Card(Body);
        Text(appearance, "Appearance", 19, true);
        var prefs = GetSharedPreferences("appearance", FileCreationMode.Private)!;
        Choice(appearance, "Theme", ["Follow system", "Light", "Dark"], prefs.GetInt("theme", 0), index =>
        {
            prefs.Edit()!.PutInt("theme", index)!.Apply();
            AppCompatDelegate.DefaultNightMode = index switch { 1 => AppCompatDelegate.ModeNightNo, 2 => AppCompatDelegate.ModeNightYes, _ => AppCompatDelegate.ModeNightFollowSystem };
        });
        var languages = new[] { "", "en", "zh-CN" };
        Choice(appearance, "Language", ["Follow system", "English", "Simplified Chinese"],
            Math.Max(0, Array.IndexOf(languages, prefs.GetString("language", ""))), index =>
            {
                prefs.Edit()!.PutString("language", languages[index])!.Apply();
                UiText.ApplyLanguage(this);
                AppCompatDelegate.ApplicationLocales = AndroidX.Core.OS.LocaleListCompat.ForLanguageTags(languages[index]);
            });
        Text(appearance, "Text size follows your device settings.", 14);
        var manage = Card(Body);
        Text(manage, "Administration", 19, true);
        Text(manage, "Opens your browser. Your web permissions still apply.", 14);
        TextButton(manage, "Applications and access", () => OpenWeb("/Applications/Applications"));
        TextButton(manage, "On-call schedules", () => OpenWeb("/OnCall"));
        TextButton(manage, "Account settings", () => OpenWeb("/Manage/Index"));
        var connection = Card(Body);
        Text(connection, "Connection", 19, true);
        Text(connection, Session.Endpoint, 14);
        TextButton(connection, "Switch server", () => ConfirmSignOut(true));
        TextButton(connection, "Sign out", () => ConfirmSignOut(false));
        var about = Card(Body);
        Text(about, "About", 19, true);
        Text(about, "AlertCenter · " + PackageManager!.GetPackageInfo(PackageName!, 0)!.VersionName, 14);
        TextButton(about, "Open-source notices", () => Sheet("Open-source notices", (panel, _) =>
        {
            using var reader = new StreamReader(Assets!.Open("markdown/NOTICES.txt"));
            Text(panel, reader.ReadToEnd(), 13);
        }));
    }
    private void ConfirmSignOut(bool switchServer)
    {
        new AndroidX.AppCompat.App.AlertDialog.Builder(this).SetTitle(T(switchServer ? "Switch server?" : "Sign out?"))!
            .SetMessage(T("Your current session on this device will end."))!
            .SetNegativeButton(T("Cancel"), (_, _) => { })!
            .SetPositiveButton(T("Continue"), (_, _) =>
            {
                Session.SignOut();
                StartActivity(new Intent(this, typeof(LoginActivity)).PutExtra("editServer", switchServer).AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask));
                Finish();
            })!.Show();
    }
}
