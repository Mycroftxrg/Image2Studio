using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using Android.Views;
using Image2Studio.Services;
using MauiApplication = Microsoft.Maui.Controls.Application;

namespace Image2Studio;

[Activity(Theme = "@style/Image2StudioTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private bool? _lastKnownDarkMode;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ApplySystemBars();
        ReapplySystemBarsAfterLayout();

        if (MauiApplication.Current is not null)
        {
            MauiApplication.Current.RequestedThemeChanged += OnRequestedThemeChanged;
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        ApplySystemBars();
        ReapplySystemBarsAfterLayout();
    }

    protected override void OnDestroy()
    {
        if (MauiApplication.Current is not null)
        {
            MauiApplication.Current.RequestedThemeChanged -= OnRequestedThemeChanged;
        }

        base.OnDestroy();
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplySystemBars(newConfig);
        ReapplySystemBarsAfterLayout(newConfig);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        _ = HandleActivityResultAsync(requestCode, resultCode, data);
    }

    private static async Task HandleActivityResultAsync(int requestCode, Result resultCode, Intent? data)
    {
        try
        {
            await UserFileSaver.TryHandleActivityResultAsync(requestCode, resultCode, data);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("Image2Studio", ex.ToString());
        }
    }

    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
    {
        _lastKnownDarkMode = e.RequestedTheme == AppTheme.Dark;
        ApplySystemBars();
        ReapplySystemBarsAfterLayout();
    }

    private void ReapplySystemBarsAfterLayout(Configuration? configuration = null)
    {
        Window?.DecorView?.PostDelayed(
            () => ApplySystemBars(configuration),
            120);
    }

    private void ApplySystemBars(Configuration? configuration = null)
    {
        if (Window is null)
        {
            return;
        }

        var darkMode = ResolveDarkMode(configuration);
        var barColor = darkMode
            ? Android.Graphics.Color.ParseColor("#0B1117")
            : Android.Graphics.Color.ParseColor("#F6F8FB");
        Window.SetBackgroundDrawable(new Android.Graphics.Drawables.ColorDrawable(barColor));
        Window.DecorView.SetBackgroundColor(barColor);
        Window.AddFlags(WindowManagerFlags.DrawsSystemBarBackgrounds);
        Window.ClearFlags(WindowManagerFlags.TranslucentStatus | WindowManagerFlags.TranslucentNavigation);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            Window.SetDecorFitsSystemWindows(true);
        }

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
        {
            Window.StatusBarContrastEnforced = false;
            Window.NavigationBarContrastEnforced = false;
        }

        Window.SetStatusBarColor(barColor);
        Window.SetNavigationBarColor(barColor);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            var mask = (int)(WindowInsetsControllerAppearance.LightStatusBars | WindowInsetsControllerAppearance.LightNavigationBars);
            var appearance = darkMode ? 0 : mask;
            Window.InsetsController?.SetSystemBarsAppearance(appearance, mask);
            return;
        }

        var flags = SystemUiFlags.LayoutStable;
        if (!darkMode)
        {
            flags |= SystemUiFlags.LightStatusBar;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                flags |= SystemUiFlags.LightNavigationBar;
            }
        }

        Window.DecorView.SystemUiFlags = flags;
    }

    private bool ResolveDarkMode(Configuration? configuration)
    {
        if (configuration is not null)
        {
            _lastKnownDarkMode = (configuration.UiMode & UiMode.NightMask) == UiMode.NightYes;
            return _lastKnownDarkMode.Value;
        }

        if (MauiApplication.Current?.RequestedTheme == AppTheme.Dark)
        {
            _lastKnownDarkMode = true;
            return true;
        }

        if (MauiApplication.Current?.RequestedTheme == AppTheme.Light)
        {
            _lastKnownDarkMode = false;
            return false;
        }

        if ((Resources?.Configuration?.UiMode & UiMode.NightMask) == UiMode.NightYes)
        {
            _lastKnownDarkMode = true;
            return true;
        }

        return _lastKnownDarkMode ?? false;
    }
}
