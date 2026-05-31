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
        var settings = Image2Settings.Load();
        if (!AppUpdateService.ShouldRunAutoCheck(settings))
        {
            return;
        }

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

            var updateNotes = await updateService.GetUpdateNotesAsync(update);
            var confirmed = await UpdatePromptPage.ShowAsync(page, update, updateNotes);
            if (!confirmed)
            {
                return;
            }

            var installerPath = await updateService.DownloadInstallerAsync(update);
            updateService.LaunchInstallerForUpdate(installerPath);
        }
        catch (MissingPlatformUpdateAssetException)
        {
            // Release metadata may be staged for another platform first. Manual checks show this message.
        }
        catch (Exception ex)
        {
            var page = sender is Window window ? window.Page : null;
            if (page is not null)
            {
                await page.DisplayAlertAsync("自动更新失败", ex.Message, "知道了");
            }
        }
    }
}
