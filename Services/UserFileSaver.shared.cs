namespace Image2Studio.Services;

public static partial class UserFileSaver
{
#if !WINDOWS && !ANDROID
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
        var path = Path.Combine(FileSystem.Current.AppDataDirectory, suggestedFileName);
        await File.WriteAllBytesAsync(path, data);
        return new SavedFile(path, false);
    }

    public static partial Task<string?> PickFolderAsync(string title)
    {
        return Task.FromResult<string?>(null);
    }

    public static partial Task<string?> EnsureManagedOutputRootAsync(string folderName)
    {
        return Task.FromResult<string?>(null);
    }
#endif
}
