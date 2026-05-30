#if ANDROID
using Android.Content;
using Android.Provider;
using Microsoft.Maui.ApplicationModel;
using System.Text;
using Android.Database;
using AndroidEnvironment = Android.OS.Environment;
using AndroidUri = Android.Net.Uri;

namespace Image2Studio.Services;

public static partial class OutputFolderWriter
{
    public static partial bool IsDocumentTreePath(string path)
    {
        return path.StartsWith("content://", StringComparison.OrdinalIgnoreCase);
    }

    public static partial Task<string> CreateTaskFolderAsync(string rootPath, string folderName)
    {
        if (!IsDocumentTreePath(rootPath))
        {
            var path = Path.Combine(rootPath, folderName);
            Directory.CreateDirectory(path);
            return Task.FromResult(path);
        }

        var root = AndroidUri.Parse(rootPath) ??
                   throw new InvalidOperationException("无法打开所选目录。");
        var resolver = Platform.AppContext.ContentResolver ??
                       throw new InvalidOperationException("无法获取 Android ContentResolver。");
        var rootDocument = NormalizeDocumentUri(root);
        if (FindChildDocument(rootDocument, folderName, DocumentsContract.Document.MimeTypeDir) is { } existing)
        {
            return Task.FromResult(existing.ToString() ?? throw new InvalidOperationException("无法获取任务文件夹地址。"));
        }

        var folder = DocumentsContract.CreateDocument(
            resolver,
            rootDocument,
            DocumentsContract.Document.MimeTypeDir,
            folderName);

        if (folder is null)
        {
            throw new InvalidOperationException("无法在所选目录中创建任务文件夹。");
        }

        return Task.FromResult(folder.ToString() ?? throw new InvalidOperationException("无法获取任务文件夹地址。"));
    }

    public static partial Task<string?> FindDirectoryAsync(string folderPath, string folderName)
    {
        if (!IsDocumentTreePath(folderPath))
        {
            var path = Path.Combine(folderPath, folderName);
            return Task.FromResult<string?>(Directory.Exists(path) ? path : null);
        }

        var parent = NormalizeDocumentUri(AndroidUri.Parse(folderPath) ??
                                          throw new InvalidOperationException("无法打开所选目录。"));
        var child = FindChildDocument(parent, folderName, DocumentsContract.Document.MimeTypeDir);
        return Task.FromResult(child?.ToString());
    }

    public static async partial Task WriteBytesAsync(string folderPath, string relativePath, byte[] data)
    {
        if (!IsDocumentTreePath(folderPath))
        {
            var path = Path.Combine(folderPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, data);
            return;
        }

        var fileUri = EnsureFile(folderPath, relativePath, GuessMimeType(relativePath));
        var resolver = Platform.AppContext.ContentResolver ??
                       throw new InvalidOperationException("无法获取 Android ContentResolver。");
        var stream = resolver.OpenOutputStream(fileUri, "wt") ??
                     throw new InvalidOperationException("无法写入所选目录。");
        await using (stream)
        {
            await stream.WriteAsync(data);
        }
    }

    public static partial Task WriteTextAsync(string folderPath, string relativePath, string text)
    {
        return WriteBytesAsync(folderPath, relativePath, Encoding.UTF8.GetBytes(text));
    }

    public static async partial Task CopyFileAsync(string folderPath, string relativePath, string sourcePath)
    {
        await WriteBytesAsync(folderPath, relativePath, await File.ReadAllBytesAsync(sourcePath));
    }

    public static async partial Task<byte[]?> ReadBytesAsync(string folderPath, string relativePath)
    {
        if (!IsDocumentTreePath(folderPath))
        {
            var path = Path.Combine(folderPath, relativePath);
            return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
        }

        var fileUri = FindRelativeDocument(folderPath, relativePath);
        if (fileUri is null)
        {
            return null;
        }

        var resolver = Platform.AppContext.ContentResolver ??
                       throw new InvalidOperationException("无法获取 Android ContentResolver。");
        var stream = resolver.OpenInputStream(fileUri);
        if (stream is null)
        {
            return null;
        }

        await using (stream)
        using (var memory = new MemoryStream())
        {
            await stream.CopyToAsync(memory);
            return memory.ToArray();
        }
    }

    public static partial Task<IReadOnlyList<string>> ListDirectoriesAsync(string folderPath)
    {
        if (!IsDocumentTreePath(folderPath))
        {
            if (!Directory.Exists(folderPath))
            {
                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            }

            return Task.FromResult<IReadOnlyList<string>>(Directory
                .EnumerateDirectories(folderPath)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .ToArray());
        }

        return Task.FromResult<IReadOnlyList<string>>(ListChildren(folderPath, directories: true));
    }

    public static partial Task<IReadOnlyList<string>> ListFilesAsync(string folderPath)
    {
        if (!IsDocumentTreePath(folderPath))
        {
            if (!Directory.Exists(folderPath))
            {
                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            }

            return Task.FromResult<IReadOnlyList<string>>(Directory
                .EnumerateFiles(folderPath)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .ToArray());
        }

        return Task.FromResult<IReadOnlyList<string>>(ListChildren(folderPath, directories: false));
    }

    public static partial Task<bool> DeleteFolderAsync(string folderPath)
    {
        if (!IsDocumentTreePath(folderPath))
        {
            if (!Directory.Exists(folderPath))
            {
                return Task.FromResult(false);
            }

            Directory.Delete(folderPath, recursive: true);
            return Task.FromResult(true);
        }

        var resolver = Platform.AppContext.ContentResolver ??
                       throw new InvalidOperationException("无法获取 Android ContentResolver。");
        var folderUri = NormalizeDocumentUri(AndroidUri.Parse(folderPath) ??
                                             throw new InvalidOperationException("无法打开所选目录。"));
        return Task.FromResult(DocumentsContract.DeleteDocument(resolver, folderUri));
    }

    public static partial Task OpenFolderAsync(string folderPath)
    {
        if (!IsDocumentTreePath(folderPath))
        {
            OpenPhysicalFolder(folderPath);
            return Task.CompletedTask;
        }

        var uri = NormalizeDocumentUri(AndroidUri.Parse(folderPath) ??
                                       throw new InvalidOperationException("无法打开所选目录。"));
        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(uri, DocumentsContract.Document.MimeTypeDir);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        if (TryStartActivity(intent))
        {
            return Task.CompletedTask;
        }

        var treeIntent = new Intent(Intent.ActionOpenDocumentTree);
        treeIntent.PutExtra("android.provider.extra.INITIAL_URI", uri);
        treeIntent.AddFlags(ActivityFlags.GrantReadUriPermission |
                            ActivityFlags.GrantWriteUriPermission |
                            ActivityFlags.GrantPrefixUriPermission |
                            ActivityFlags.NewTask);
        if (TryStartActivity(treeIntent))
        {
            return Task.CompletedTask;
        }

        throw new InvalidOperationException("没有可打开此文件夹的文件管理器。");
    }

    private static void OpenPhysicalFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException("找不到要打开的文件夹。");
        }

        if (TryCreateExternalStorageDocumentUri(folderPath) is { } documentUri)
        {
            var viewIntent = new Intent(Intent.ActionView);
            viewIntent.SetDataAndType(documentUri, DocumentsContract.Document.MimeTypeDir);
            viewIntent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
            if (TryStartActivity(viewIntent))
            {
                return;
            }
        }

        if (TryCreateExternalStorageTreeUri(folderPath) is { } treeUri)
        {
            var treeIntent = new Intent(Intent.ActionOpenDocumentTree);
            treeIntent.PutExtra("android.provider.extra.INITIAL_URI", treeUri);
            treeIntent.AddFlags(ActivityFlags.GrantReadUriPermission |
                                ActivityFlags.GrantWriteUriPermission |
                                ActivityFlags.GrantPrefixUriPermission |
                                ActivityFlags.NewTask);
            if (TryStartActivity(treeIntent))
            {
                return;
            }
        }

        throw new InvalidOperationException("没有可打开此文件夹的文件管理器。");
    }

    private static bool TryStartActivity(Intent intent)
    {
        var activity = Platform.CurrentActivity;
        if (activity is null)
        {
            return false;
        }

        try
        {
            activity.StartActivity(intent);
            return true;
        }
        catch (ActivityNotFoundException)
        {
            return false;
        }
        catch (Java.Lang.SecurityException)
        {
            return false;
        }
    }

    private static AndroidUri? TryCreateExternalStorageDocumentUri(string folderPath)
    {
        return TryCreateExternalStorageUri(folderPath, tree: false);
    }

    private static AndroidUri? TryCreateExternalStorageTreeUri(string folderPath)
    {
        return TryCreateExternalStorageUri(folderPath, tree: true);
    }

    private static AndroidUri? TryCreateExternalStorageUri(string folderPath, bool tree)
    {
        var rootPath = AndroidEnvironment.ExternalStorageDirectory?.AbsolutePath;
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        var normalizedRoot = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(folderPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            !normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relativePath = normalizedPath.Length == normalizedRoot.Length
            ? string.Empty
            : normalizedPath[(normalizedRoot.Length + 1)..].Replace('\\', '/');
        var documentId = string.IsNullOrWhiteSpace(relativePath) ? "primary:" : $"primary:{relativePath}";
        return tree
            ? DocumentsContract.BuildTreeDocumentUri("com.android.externalstorage.documents", documentId)
            : DocumentsContract.BuildDocumentUri("com.android.externalstorage.documents", documentId);
    }

    private static AndroidUri EnsureFile(string folderPath, string relativePath, string mimeType)
    {
        var current = NormalizeDocumentUri(AndroidUri.Parse(folderPath) ??
                                           throw new InvalidOperationException("无法打开所选目录。"));
        var resolver = Platform.AppContext.ContentResolver ??
                       throw new InvalidOperationException("无法获取 Android ContentResolver。");
        var parts = relativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length - 1; index++)
        {
            current = FindChildDocument(current, parts[index], DocumentsContract.Document.MimeTypeDir) ??
                      DocumentsContract.CreateDocument(
                          resolver,
                          current,
                          DocumentsContract.Document.MimeTypeDir,
                          parts[index]) ??
                      throw new InvalidOperationException("无法创建子文件夹。");
        }

        var fileName = parts.LastOrDefault() ?? "file";
        return FindChildDocument(current, fileName, null) ??
               DocumentsContract.CreateDocument(
                   resolver,
                   current,
                   mimeType,
                   fileName) ??
               throw new InvalidOperationException("无法创建文件。");
    }

    private static AndroidUri? FindRelativeDocument(string folderPath, string relativePath)
    {
        var current = NormalizeDocumentUri(AndroidUri.Parse(folderPath) ??
                                           throw new InvalidOperationException("无法打开所选目录。"));
        var parts = relativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            current = FindChildDocument(current, part, null);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static AndroidUri? FindChildDocument(AndroidUri parentUri, string displayName, string? mimeType)
    {
        var resolver = Platform.AppContext.ContentResolver ??
                       throw new InvalidOperationException("无法获取 Android ContentResolver。");
        parentUri = NormalizeDocumentUri(parentUri);
        var parentDocumentId = DocumentsContract.GetDocumentId(parentUri);
        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(parentUri, parentDocumentId);
        if (childrenUri is null)
        {
            return null;
        }

        ICursor? cursor = null;
        try
        {
            cursor = resolver.Query(
                childrenUri,
                new[] { DocumentsContract.Document.ColumnDocumentId, DocumentsContract.Document.ColumnDisplayName, DocumentsContract.Document.ColumnMimeType },
                null,
                null,
                null);

            while (cursor?.MoveToNext() == true)
            {
                var name = cursor.GetString(1);
                var type = cursor.GetString(2);
                if (!string.Equals(name, displayName, StringComparison.Ordinal) ||
                    (mimeType is not null && !string.Equals(type, mimeType, StringComparison.Ordinal)))
                {
                    continue;
                }

                var documentId = cursor.GetString(0);
                return DocumentsContract.BuildDocumentUriUsingTree(parentUri, documentId);
            }
        }
        finally
        {
            cursor?.Close();
            cursor?.Dispose();
        }

        return null;
    }

    private static IReadOnlyList<string> ListChildren(string folderPath, bool directories)
    {
        var parentUri = NormalizeDocumentUri(AndroidUri.Parse(folderPath) ??
                                             throw new InvalidOperationException("无法打开所选目录。"));
        var resolver = Platform.AppContext.ContentResolver ??
                       throw new InvalidOperationException("无法获取 Android ContentResolver。");
        var parentDocumentId = DocumentsContract.GetDocumentId(parentUri);
        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(parentUri, parentDocumentId);
        if (childrenUri is null)
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();
        ICursor? cursor = null;
        try
        {
            cursor = resolver.Query(
                childrenUri,
                new[]
                {
                    DocumentsContract.Document.ColumnDocumentId,
                    DocumentsContract.Document.ColumnDisplayName,
                    DocumentsContract.Document.ColumnMimeType
                },
                null,
                null,
                null);

            while (cursor?.MoveToNext() == true)
            {
                var documentId = cursor.GetString(0);
                var name = cursor.GetString(1);
                var type = cursor.GetString(2);
                var isDirectory = string.Equals(type, DocumentsContract.Document.MimeTypeDir, StringComparison.Ordinal);
                if (directories == isDirectory && !string.IsNullOrWhiteSpace(name))
                {
                    if (directories && !string.IsNullOrWhiteSpace(documentId))
                    {
                        values.Add(DocumentsContract.BuildDocumentUriUsingTree(parentUri, documentId)?.ToString() ?? name);
                    }
                    else
                    {
                        values.Add(name);
                    }
                }
            }
        }
        finally
        {
            cursor?.Close();
            cursor?.Dispose();
        }

        return values;
    }

    private static AndroidUri NormalizeDocumentUri(AndroidUri uri)
    {
        try
        {
            _ = DocumentsContract.GetDocumentId(uri);
            return uri;
        }
        catch
        {
            return DocumentsContract.BuildDocumentUriUsingTree(uri, DocumentsContract.GetTreeDocumentId(uri)) ?? uri;
        }
    }

    private static string GuessMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => "application/json",
            ".txt" => "text/plain",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".png" => "image/png",
            _ => "application/octet-stream"
        };
    }
}
#endif
