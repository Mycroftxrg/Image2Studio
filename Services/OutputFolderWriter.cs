namespace Image2Studio.Services;

public static partial class OutputFolderWriter
{
    public static partial bool IsDocumentTreePath(string path);

    public static partial Task<string> CreateTaskFolderAsync(string rootPath, string folderName);

    public static partial Task<string?> FindDirectoryAsync(string folderPath, string folderName);

    public static partial Task WriteBytesAsync(string folderPath, string relativePath, byte[] data);

    public static partial Task WriteTextAsync(string folderPath, string relativePath, string text);

    public static partial Task CopyFileAsync(string folderPath, string relativePath, string sourcePath);

    public static partial Task<byte[]?> ReadBytesAsync(string folderPath, string relativePath);

    public static partial Task<IReadOnlyList<string>> ListDirectoriesAsync(string folderPath);

    public static partial Task<IReadOnlyList<string>> ListFilesAsync(string folderPath);

    public static partial Task<bool> DeleteFolderAsync(string folderPath);

    public static partial Task OpenFolderAsync(string folderPath);
}
