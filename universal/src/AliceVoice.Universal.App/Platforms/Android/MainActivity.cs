using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace AliceVoice.AppU;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, Exported = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(new[] { Intent.ActionMain }, Categories = new[] { Intent.CategoryLeanbackLauncher })]
public class MainActivity : MauiAppCompatActivity
{
}
