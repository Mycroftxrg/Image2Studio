#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Microsoft.Maui.ApplicationModel;
using System.Runtime.Versioning;
using AndroidEnvironment = Android.OS.Environment;

namespace Image2Studio.Services;

public static partial class UserFileSaver
{
    private const int SaveFileRequestCode = 7324;
    private const int PickFolderRequestCode = 7325;
    private const string GalleryFolderName = "Image2 Studio";
    private static TaskCompletionSource<SavedFile?>? _pendingSave;
    private static byte[]? _pendingData;
    private static TaskCompletionSource<string?>? _pendingFolder;

    private static async partial Task<SavedFile?> SaveToDefaultLocationAsync(
        string suggestedFileName,
        string mimeType,
        byte[] data)
    {
        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return await SaveImageToGalleryFolderAsync(suggestedFileName, mimeType, data);
        }

        var path = Path.Combine(FileSystem.Current.AppDataDirectory, suggestedFileName);
        await File.WriteAllBytesAsync(path, data);
        return new SavedFile(path, false);
    }

    private static async partial Task<SavedFile?> SaveWithPickerAsync(
        string suggestedFileName,
        string mimeType,
        byte[] data)
    {
        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("找不到当前 Android Activity。");
        if (_pendingSave is not null)
        {
            throw new InvalidOperationException("已有一个保存操作正在进行。");
        }

        _pendingData = data;
        _pendingSave = new TaskCompletionSource<SavedFile?>();

        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType);
        intent.PutExtra(Intent.ExtraTitle, suggestedFileName);
        activity.StartActivityForResult(intent, SaveFileRequestCode);

        return await _pendingSave.Task;
    }

    private static async Task<SavedFile?> SaveImageToGalleryFolderAsync(
        string suggestedFileName,
        string mimeType,
        byte[] data)
    {
        var resolver = Platform.CurrentActivity?.ContentResolver ??
                       Android.App.Application.Context?.ContentResolver ??
                       throw new InvalidOperationException("无法获取 Android ContentResolver。");

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            return await SaveImageToMediaStoreAsync(resolver, suggestedFileName, mimeType, data);
        }

        var pictures = AndroidEnvironment.GetExternalStoragePublicDirectory(AndroidEnvironment.DirectoryPictures)?.AbsolutePath
                       ?? FileSystem.Current.AppDataDirectory;
        var folder = Path.Combine(pictures, GalleryFolderName);
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, suggestedFileName);
        await File.WriteAllBytesAsync(path, data);
        return new SavedFile(path, false);
    }

    [SupportedOSPlatform("android29.0")]
    private static async Task<SavedFile?> SaveImageToMediaStoreAsync(
        ContentResolver resolver,
        string suggestedFileName,
        string mimeType,
        byte[] data)
    {
        var externalImagesUri = MediaStore.Images.Media.ExternalContentUri ??
                                throw new InvalidOperationException("无法打开系统图片库。");

        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, suggestedFileName);
        values.Put(MediaStore.IMediaColumns.MimeType, mimeType);
        values.Put(MediaStore.IMediaColumns.RelativePath, $"{AndroidEnvironment.DirectoryPictures}/{GalleryFolderName}");
        values.Put(MediaStore.IMediaColumns.IsPending, 1);

        var uri = resolver.Insert(externalImagesUri, values) ??
                  throw new InvalidOperationException("无法在相册中创建图片。");

        try
        {
            var outputStream = resolver.OpenOutputStream(uri) ??
                               throw new InvalidOperationException("无法打开相册保存位置。");
            await using (outputStream)
            {
                await outputStream.WriteAsync(data);
            }

            values.Clear();
            values.Put(MediaStore.IMediaColumns.IsPending, 0);
            resolver.Update(uri, values, null, null);
            return new SavedFile($"相册/{GalleryFolderName}/{suggestedFileName}", false);
        }
        catch
        {
            resolver.Delete(uri, null, null);
            throw;
        }
    }

    public static async Task<bool> TryHandleActivityResultAsync(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode == PickFolderRequestCode)
        {
            var folderCompletion = _pendingFolder;
            _pendingFolder = null;
            if (folderCompletion is null)
            {
                return true;
            }

            if (resultCode != Result.Ok || data?.Data is null)
            {
                folderCompletion.TrySetResult(null);
                return true;
            }

            var uri = data.Data!;
            var flags = data.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission);
            try
            {
                Platform.CurrentActivity?.ContentResolver?.TakePersistableUriPermission(uri, flags);
            }
            catch
            {
                // Some document providers do not support persistable grants.
            }

            folderCompletion.TrySetResult(uri.ToString());
            return true;
        }

        if (requestCode != SaveFileRequestCode)
        {
            return false;
        }

        var completion = _pendingSave;
        var bytes = _pendingData;
        _pendingSave = null;
        _pendingData = null;

        if (completion is null)
        {
            return true;
        }

        if (resultCode != Result.Ok || data?.Data is null || bytes is null)
        {
            completion.TrySetResult(null);
            return true;
        }

        try
        {
            var uri = data.Data!;
            var resolver = Platform.CurrentActivity?.ContentResolver ??
                           Android.App.Application.Context?.ContentResolver ??
                           throw new InvalidOperationException("无法获取 Android ContentResolver。");

            var outputStream = resolver.OpenOutputStream(uri) ??
                               throw new InvalidOperationException("无法打开保存位置。");
            await using (outputStream)
            {
                await outputStream.WriteAsync(bytes);
            }
            completion.TrySetResult(new SavedFile(uri.ToString() ?? "content://saved-file", true));
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }

        return true;
    }

    public static async partial Task<string?> PickFolderAsync(string title)
    {
        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("找不到当前 Android Activity。");
        if (_pendingFolder is not null)
        {
            throw new InvalidOperationException("已有一个文件夹选择操作正在进行。");
        }

        _pendingFolder = new TaskCompletionSource<string?>();
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission |
                        ActivityFlags.GrantWriteUriPermission |
                        ActivityFlags.GrantPersistableUriPermission |
                        ActivityFlags.GrantPrefixUriPermission);
        activity.StartActivityForResult(intent, PickFolderRequestCode);
        return await _pendingFolder.Task;
    }

    public static partial Task<string?> EnsureManagedOutputRootAsync(string folderName)
    {
        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("找不到当前 Android Activity。");

        if (OperatingSystem.IsAndroidVersionAtLeast(30) && !AndroidEnvironment.IsExternalStorageManager)
        {
            var packageUri = Android.Net.Uri.Parse($"package:{activity.PackageName}");
            var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission, packageUri);
            try
            {
                activity.StartActivity(intent);
            }
            catch
            {
                activity.StartActivity(new Intent(Settings.ActionManageAllFilesAccessPermission));
            }

            return Task.FromResult<string?>(null);
        }

        var documents = AndroidEnvironment.GetExternalStoragePublicDirectory(AndroidEnvironment.DirectoryDocuments)?.AbsolutePath;
        if (string.IsNullOrWhiteSpace(documents))
        {
            documents = Path.Combine(AndroidEnvironment.ExternalStorageDirectory?.AbsolutePath ?? FileSystem.Current.AppDataDirectory, "Documents");
        }

        var safeFolderName = string.IsNullOrWhiteSpace(folderName) ? "Image2 Studio Library" : folderName.Trim();
        var path = Path.Combine(documents, safeFolderName);
        Directory.CreateDirectory(path);
        return Task.FromResult<string?>(path);
    }
}
#endif
