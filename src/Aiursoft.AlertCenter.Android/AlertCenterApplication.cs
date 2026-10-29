using Android.App;
using Android.Runtime;

[assembly: UsesPermission(Android.Manifest.Permission.Internet)]

namespace Aiursoft.AlertCenter.Android;

[Application]
public sealed class AlertCenterApplication(nint handle, JniHandleOwnership ownership)
    : Application(handle, ownership)
{
    public AppSession Session { get; private set; } = null!;

    public override void OnCreate()
    {
        base.OnCreate();
        AndroidX.AppCompat.App.AppCompatDelegate.DefaultNightMode =
            GetSharedPreferences("appearance", global::Android.Content.FileCreationMode.Private)!
                .GetInt("theme", 0) switch
            {
                1 => AndroidX.AppCompat.App.AppCompatDelegate.ModeNightNo,
                2 => AndroidX.AppCompat.App.AppCompatDelegate.ModeNightYes,
                _ => AndroidX.AppCompat.App.AppCompatDelegate.ModeNightFollowSystem
            };
        AndroidX.AppCompat.App.AppCompatDelegate.ApplicationLocales = AndroidX.Core.OS.LocaleListCompat.ForLanguageTags(
            GetSharedPreferences("appearance", global::Android.Content.FileCreationMode.Private)!.GetString("language", "")!);
        UiText.ApplyLanguage(this);
        Session = new AppSession(this);
    }
}
