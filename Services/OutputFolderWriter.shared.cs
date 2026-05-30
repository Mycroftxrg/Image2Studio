using System.Text;

namespace Image2Studio.Services;

public static partial class OutputFolderWriter
{
#if !ANDROID
    public static partial bool IsDocumentTreePath(string path) => false;

    public static partial Task<string> CreateTaskFolderAsync(string rootPath, string folderName)
    {
        var path = Path.Combine(rootPath, folderName);
        Directory.CreateDirectory(path);
        return Task.FromResult(path);
    }

    public static partial Task<string?> FindDirectoryAsync(string folderPath, string folderName)
    {
        var path = Path.Combine(folderPath, folderName);
        return Task.FromResult<string?>(Directory.Exists(path) ? path : null);
    }

    public static async partial Task WriteBytesAsync(string folderPath, string relativePath, byte[] data)
    {
        var path = Path.Combine(folderPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, data);
    }

    public static async partial Task WriteTextAsync(string folderPath, string relativePath, string text)
    {
        var path = Path.Combine(folderPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text, Encoding.UTF8);
    }

    public static partial Task CopyFileAsync(string folderPath, string relativePath, string sourcePath)
    {
        var path = Path.Combine(folderPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(sourcePath, path, overwrite: true);
        return Task.CompletedTask;
    }

    public static async partial Task<byte[]?> ReadBytesAsync(string folderPath, string relativePath)
    {
        var path = Path.Combine(folderPath, relativePath);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
    }

    public static partial Task<IReadOnlyList<string>> ListDirectoriesAsync(string folderPath)
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

    public static partial Task<IReadOnlyList<string>> ListFilesAsync(string folderPath)
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

    public static partial Task<bool> DeleteFolderAsync(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            return Task.FromResult(false);
        }

        Directory.Delete(folderPath, recursive: true);
        return Task.FromResult(true);
    }

    public static partial Task OpenFolderAsync(string folderPath)
    {
        var path = Directory.Exists(folderPath)
            ? folderPath
            : File.Exists(folderPath)
                ? Path.GetDirectoryName(folderPath)
                : null;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new DirectoryNotFoundException("找不到要打开的文件夹。");
        }

        return Launcher.Default.OpenAsync(new Uri(path));
    }
#endif
}
