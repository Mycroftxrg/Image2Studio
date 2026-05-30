using Image2Studio.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Image2Studio;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell());
        window.Created += OnWindowCreated;
        return window;
    }

    private async void OnWindowCreated(object? sender, EventArgs e)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var settings = Image2Settings.Load();
        if (!AppUpdateService.ShouldRunAutoCheck(settings))
        {
            return;
        }

        AppUpdateService.MarkAutoCheckCompleted();
        await Task.Delay(TimeSpan.FromSeconds(3));

        try
        {
            var updateService = new AppUpdateService();
            var page = sender is Window window ? window.Page : null;
            var update = await updateService.CheckAsync(Image2Settings.Load());
            if (!update.IsUpdateAvailable || page is null)
            {
                return;
            }

            var notes = string.IsNullOrWhiteSpace(update.Notes) ? string.Empty : $"{Environment.NewLine}{Environment.NewLine}{update.Notes}";
            var confirmed = await page.DisplayAlertAsync(
                "发现新版本",
                $"当前版本：{update.CurrentVersion}{Environment.NewLine}最新版本：{update.LatestVersion}{notes}",
                "下载并安装",
                "稍后");
            if (!confirmed)
            {
                return;
            }

            var installerPath = await updateService.DownloadInstallerAsync(update);
            updateService.LaunchInstaller(installerPath);
        }
        catch
        {
            // Automatic update checks stay quiet; manual checks show detailed errors.
        }
    }
}
