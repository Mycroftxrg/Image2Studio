namespace Image2Studio.Services;

public sealed record SavedFile(string Location, bool UsedPicker);

public static partial class UserFileSaver
{
    public static async Task<SavedFile?> SaveAsync(
        string suggestedFileName,
        string mimeType,
        byte[] data,
        bool askForLocation)
    {
        if (askForLocation)
        {
            return await SaveWithPickerAsync(suggestedFileName, mimeType, data);
        }

        return await SaveToDefaultLocationAsync(suggestedFileName, mimeType, data);
    }

    private static partial Task<SavedFile?> SaveToDefaultLocationAsync(
        string suggestedFileName,
        string mimeType,
        byte[] data);

    private static partial Task<SavedFile?> SaveWithPickerAsync(
        string suggestedFileName,
        string mimeType,
        byte[] data);

    public static partial Task<string?> PickFolderAsync(string title);

    public static partial Task<string?> EnsureManagedOutputRootAsync(string folderName);
}
