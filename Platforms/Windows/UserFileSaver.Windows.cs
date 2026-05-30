#if WINDOWS
using Image2Studio.Services;
using Microsoft.Maui.Platform;
using Windows.Storage.Pickers;

namespace Image2Studio.Services;

public static partial class UserFileSaver
{
    private static async partial Task<SavedFile?> SaveToDefaultLocationAsync(
        string suggestedFileName,
        string mimeType,
        byte[] data)
    {
        var path = Path.Combine(FileSystem.Current.AppDataDirectory, suggestedFileName);
        await File.WriteAllBytesAsync(path, data);
        return new SavedFile(path, false);
    }

    private static async partial Task<SavedFile?> SaveWithPickerAsync(
        string suggestedFileName,
        string mimeType,
        byte[] data)
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedFileName)
        };

        var extension = Path.GetExtension(suggestedFileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".bin";
        }

        picker.FileTypeChoices.Add(GetPickerLabel(mimeType, extension), new List<string> { extension });

        var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as MauiWinUIWindow;
        var hwnd = window?.WindowHandle ?? IntPtr.Zero;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return null;
        }

        await File.WriteAllBytesAsync(file.Path, data);
        return new SavedFile(file.Path, true);
    }

    private static string GetPickerLabel(string mimeType, string extension)
    {
        if (mimeType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return "JSON";
        }

        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return $"{extension.TrimStart('.').ToUpperInvariant()} 图片";
        }

        return $"{extension.TrimStart('.').ToUpperInvariant()} 文件";
    }

    public static async partial Task<string?> PickFolderAsync(string title)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };
        picker.FileTypeFilter.Add("*");

        var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as MauiWinUIWindow;
        var hwnd = window?.WindowHandle ?? IntPtr.Zero;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public static partial Task<string?> EnsureManagedOutputRootAsync(string folderName)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = FileSystem.Current.AppDataDirectory;
        }

        var path = Path.Combine(root, string.IsNullOrWhiteSpace(folderName) ? "Image2 Studio Library" : folderName);
        Directory.CreateDirectory(path);
        return Task.FromResult<string?>(path);
    }
}
#endif
