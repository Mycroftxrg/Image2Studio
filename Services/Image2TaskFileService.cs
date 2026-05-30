using System.Text.Json;
using Image2Studio;

namespace Image2Studio.Services;

public sealed class Image2TaskFileService
{
    private const string LibraryIndexFileName = "image2-library-index.json";
    private const string ManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<Image2ReferenceAsset> CopyReferenceAsync(FileResult file, string taskId)
    {
        var folder = GetTaskDraftReferenceFolder(taskId);
        Directory.CreateDirectory(folder);
        var safeFileName = SanitizeFileName(string.IsNullOrWhiteSpace(file.FileName) ? "reference.png" : file.FileName);
        var path = GetUniquePath(folder, safeFileName);

        await using var input = await OpenFileResultReadStreamAsync(file);
        await using var output = File.Create(path);
        await input.CopyToAsync(output);

        return new Image2ReferenceAsset
        {
            FileName = Path.GetFileName(path),
            Path = path
        };
    }

    public async Task SaveTaskSnapshotAsync(Image2QueuedTask task, Image2Settings settings)
    {
        var folder = await EnsureTaskOutputFolderAsync(task, settings);
        await OutputFolderWriter.WriteTextAsync(folder, "prompt.txt", task.Prompt);
        await OutputFolderWriter.WriteTextAsync(folder, "parameters.json", SerializeJson(BuildParameterSnapshot(task)));
        await WriteJsonAsync(folder, "task.json", task);
        await CopyReferencesToOutputAsync(task, folder);
        await WriteManifestAsync(task, null, settings);
    }

    public async Task SaveResultAsync(Image2QueuedTask task, Image2Result result, Image2Settings settings)
    {
        var folder = await EnsureTaskOutputFolderAsync(task, settings);
        await OutputFolderWriter.WriteTextAsync(folder, "prompt.txt", task.Prompt);
        await OutputFolderWriter.WriteTextAsync(folder, "parameters.json", SerializeJson(BuildParameterSnapshot(task)));
        await OutputFolderWriter.WriteTextAsync(folder, "raw-response.json", result.RawJson);
        await WriteJsonAsync(folder, "task.json", task);
        await CopyReferencesToOutputAsync(task, folder);

        if (result.ImageBytes is { Length: > 0 } bytes)
        {
            var extension = NormalizeImageExtension(task.OutputFormat);
            var relativePath = $"result.{extension}";
            await OutputFolderWriter.WriteBytesAsync(folder, relativePath, bytes);
            task.ImagePath = CombineOutputPath(folder, relativePath);
        }
        else if (!string.IsNullOrWhiteSpace(result.ImageUrl))
        {
            task.ImageUrl = result.ImageUrl;
            await OutputFolderWriter.WriteTextAsync(folder, "result-url.txt", result.ImageUrl);
            try
            {
                using var http = new HttpClient();
                var downloadedBytes = await http.GetByteArrayAsync(result.ImageUrl);
                var extension = GuessExtensionFromUrl(result.ImageUrl, task.OutputFormat);
                var relativePath = $"result.{extension}";
                await OutputFolderWriter.WriteBytesAsync(folder, relativePath, downloadedBytes);
                task.ImagePath = CombineOutputPath(folder, relativePath);
            }
            catch
            {
                // The URL is still persisted; download failures should not hide a successful generation.
            }
        }

        await WriteJsonAsync(folder, "task.json", task);
        await WriteManifestAsync(task, result.HasImage, settings);
    }

    public async Task SaveFailureAsync(Image2QueuedTask task, string error, Image2Settings settings)
    {
        var folder = await EnsureTaskOutputFolderAsync(task, settings);
        await OutputFolderWriter.WriteTextAsync(folder, "prompt.txt", task.Prompt);
        await OutputFolderWriter.WriteTextAsync(folder, "parameters.json", SerializeJson(BuildParameterSnapshot(task)));
        await OutputFolderWriter.WriteTextAsync(folder, "error.txt", error);
        await WriteJsonAsync(folder, "task.json", task);
        await CopyReferencesToOutputAsync(task, folder);
        await WriteManifestAsync(task, false, settings);
    }

    public async Task SaveHistoryManifestAsync(Image2HistoryEntry history, Image2Settings settings)
    {
        if (string.IsNullOrWhiteSpace(history.OutputFolder))
        {
            return;
        }

        await WriteJsonAsync(history.OutputFolder, ManifestFileName, Image2TaskManifest.FromHistory(history));
        await AddOrUpdateLibraryIndexAsync(settings, Image2LibraryIndexEntry.FromHistory(history, settings));
    }

    public async Task DeleteHistoryAsync(Image2HistoryEntry history, Image2Settings settings, bool deleteFiles)
    {
        if (deleteFiles && !string.IsNullOrWhiteSpace(history.OutputFolder))
        {
            await OutputFolderWriter.DeleteFolderAsync(history.OutputFolder);
        }

        await RemoveFromLibraryIndexAsync(settings, history);
    }

    public async Task<IReadOnlyList<Image2HistoryEntry>> ImportHistoryAsync(Image2Settings settings)
    {
        return (await ImportLibraryAsync(settings)).History;
    }

    public async Task<Image2ImportedLibrary> ImportLibraryAsync(Image2Settings settings)
    {
        var root = settings.GetResolvedOutputRootPath();
        if (string.IsNullOrWhiteSpace(root))
        {
            return new Image2ImportedLibrary(Array.Empty<Image2QueuedTask>(), Array.Empty<Image2HistoryEntry>());
        }

        var importedTasks = new List<Image2QueuedTask>();
        var imported = new List<Image2HistoryEntry>();
        foreach (var folder in await GetLibraryFoldersAsync(root))
        {
            if (await ReadHistoryFromFolderAsync(folder) is { } history)
            {
                imported.Add(history);
                continue;
            }

            if (await ReadQueuedTaskFromFolderAsync(folder) is { } task)
            {
                importedTasks.Add(task);
            }
        }

        var tasks = importedTasks
            .GroupBy(task => string.IsNullOrWhiteSpace(task.OutputFolder) ? task.Id : task.OutputFolder, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(task => task.CreatedAt)
            .ToArray();

        var historyItems = imported
            .GroupBy(item => $"{GetHistoryGroupId(item)}|{item.Version}|{item.OutputFolder}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(item => item.CreatedAt)
            .ToArray();

        return new Image2ImportedLibrary(tasks, historyItems);
    }

    public async Task<string> EnsureTaskOutputFolderAsync(Image2QueuedTask task, Image2Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(task.OutputFolder))
        {
            if (!OutputFolderWriter.IsDocumentTreePath(task.OutputFolder))
            {
                Directory.CreateDirectory(task.OutputFolder);
            }
            return task.OutputFolder;
        }

        var root = settings.GetResolvedOutputRootPath();
        var stamp = task.CreatedAt.ToString("yyyyMMdd-HHmmss");
        var versionSuffix = string.IsNullOrWhiteSpace(task.VersionGroupId) || task.VersionGroupId == task.Id
            ? string.Empty
            : $"-v{task.CreatedAt:HHmmss}";
        var folder = await OutputFolderWriter.CreateTaskFolderAsync(root, $"{stamp}-{SanitizeFileName(GetTitle(task))}{versionSuffix}");
        task.OutputFolder = folder;
        return folder;
    }

    public static FileResult[] BuildFileResults(IEnumerable<Image2ReferenceAsset> assets)
    {
        return assets
            .Where(asset => !string.IsNullOrWhiteSpace(asset.Path) && File.Exists(asset.Path))
            .Select(asset => new FileResult(asset.Path)
            {
                FileName = string.IsNullOrWhiteSpace(asset.FileName) ? Path.GetFileName(asset.Path) : asset.FileName
            })
            .Cast<FileResult>()
            .ToArray();
    }

    private static Task<Stream> OpenFileResultReadStreamAsync(FileResult file)
    {
        if (!string.IsNullOrWhiteSpace(file.FullPath) &&
            File.Exists(file.FullPath))
        {
            return Task.FromResult<Stream>(File.Open(
                file.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read));
        }

        return file.OpenReadAsync();
    }

    private static string GetTaskDraftReferenceFolder(string taskId)
    {
        return Path.Combine(FileSystem.Current.AppDataDirectory, "queue", "references", taskId);
    }

    private static string GetTitle(Image2QueuedTask task)
    {
        return string.IsNullOrWhiteSpace(task.Title) ? "未命名任务" : task.Title.Trim();
    }

    private static async Task WriteJsonAsync<T>(string folder, string relativePath, T value)
    {
        await OutputFolderWriter.WriteTextAsync(folder, relativePath, SerializeJson(value));
    }

    private async Task WriteManifestAsync(Image2QueuedTask task, bool? succeeded, Image2Settings settings)
    {
        var folder = await EnsureTaskOutputFolderAsync(task, settings);
        var manifest = Image2TaskManifest.FromTask(task, succeeded);
        await WriteJsonAsync(folder, ManifestFileName, manifest);
        await AddOrUpdateLibraryIndexAsync(settings, Image2LibraryIndexEntry.FromTask(task, succeeded, settings));
    }

    private async Task AddOrUpdateLibraryIndexAsync(Image2Settings settings, Image2LibraryIndexEntry entry)
    {
        var root = settings.GetResolvedOutputRootPath();
        var index = await ReadJsonAsync<Image2LibraryIndex>(root, LibraryIndexFileName) ?? new Image2LibraryIndex();
        index.UpdatedAt = DateTime.Now;
        index.Entries.RemoveAll(item =>
            string.Equals(item.TaskId, entry.TaskId, StringComparison.Ordinal) ||
            string.Equals(item.Folder, entry.Folder, StringComparison.Ordinal));
        index.Entries.Insert(0, entry);
        if (index.Entries.Count > 500)
        {
            index.Entries.RemoveRange(500, index.Entries.Count - 500);
        }

        await WriteJsonAsync(root, LibraryIndexFileName, index);
    }

    private async Task RemoveFromLibraryIndexAsync(Image2Settings settings, Image2HistoryEntry history)
    {
        var root = settings.GetResolvedOutputRootPath();
        var index = await ReadJsonAsync<Image2LibraryIndex>(root, LibraryIndexFileName);
        if (index?.Entries is not { Count: > 0 })
        {
            return;
        }

        var folderRef = string.IsNullOrWhiteSpace(history.OutputFolder)
            ? string.Empty
            : GetFolderReference(root, history.OutputFolder);
        index.Entries.RemoveAll(item =>
            string.Equals(item.TaskId, history.Id, StringComparison.Ordinal) ||
            string.Equals(item.VersionGroupId, GetHistoryGroupId(history), StringComparison.Ordinal) && item.Version == history.Version ||
            !string.IsNullOrWhiteSpace(folderRef) && string.Equals(item.Folder, folderRef, StringComparison.Ordinal));
        index.UpdatedAt = DateTime.Now;
        await WriteJsonAsync(root, LibraryIndexFileName, index);
    }

    private async Task<IReadOnlyList<string>> GetLibraryFoldersAsync(string root)
    {
        var folders = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = await ReadJsonAsync<Image2LibraryIndex>(root, LibraryIndexFileName);
        if (index?.Entries is { Count: > 0 })
        {
            foreach (var entry in index.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Folder))
                {
                    continue;
                }

                var folder = await ResolveFolderPathAsync(root, entry.Folder);
                if (!string.IsNullOrWhiteSpace(folder) && seen.Add(folder))
                {
                    folders.Add(folder);
                }
            }
        }

        foreach (var directory in await OutputFolderWriter.ListDirectoriesAsync(root))
        {
            var folder = await ResolveFolderPathAsync(root, directory);
            if (!string.IsNullOrWhiteSpace(folder) && seen.Add(folder))
            {
                folders.Add(folder);
            }
        }

        return folders;
    }

    private static async Task<T?> ReadJsonAsync<T>(string folder, string relativePath)
    {
        var bytes = await OutputFolderWriter.ReadBytesAsync(folder, relativePath);
        if (bytes is null || bytes.Length == 0)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private async Task<Image2HistoryEntry?> ReadHistoryFromFolderAsync(string folder)
    {
        var manifest = await ReadJsonAsync<Image2TaskManifest>(folder, ManifestFileName);
        if (manifest is not null)
        {
            return manifest.ToHistory(folder);
        }

        var task = await ReadJsonAsync<Image2QueuedTask>(folder, "task.json");
        if (task is null || task.Status is Image2TaskStatus.Queued or Image2TaskStatus.Running)
        {
            return null;
        }

        task.OutputFolder ??= folder;
        return new Image2HistoryEntry
        {
            SourceTaskId = string.IsNullOrWhiteSpace(task.VersionGroupId) ? task.Id : task.VersionGroupId,
            Version = 1,
            Title = task.Title,
            Prompt = task.Prompt,
            Mode = task.Mode,
            Size = task.Size,
            Quality = task.Quality,
            ResponseFormat = task.ResponseFormat,
            OutputFormat = task.OutputFormat,
            Background = task.Background,
            Transparent = task.Transparent,
            UseAsyncTask = task.UseAsyncTask,
            UseChatEndpoint = task.UseChatEndpoint,
            StreamChat = task.StreamChat,
            AssignmentMode = task.AssignmentMode,
            RequestedConnectionId = task.RequestedConnectionId,
            RequestedConnectionTitle = task.RequestedConnectionTitle,
            ReferenceImages = task.ReferenceImages.Select(CloneReference).ToList(),
            ReferenceImageUrls = task.ReferenceImageUrls.ToList(),
            OutputFolder = task.OutputFolder,
            ImagePath = task.ImagePath,
            ImageUrl = task.ImageUrl,
            AssignedConnectionId = task.AssignedConnectionId,
            AssignedConnectionTitle = task.AssignedConnectionTitle,
            AssistantText = task.AssistantText,
            RawJson = task.RawJson,
            Succeeded = task.Status == Image2TaskStatus.Completed,
            StatusText = task.Status == Image2TaskStatus.Completed ? "生图成功" : "生图失败",
            ErrorMessage = task.ErrorMessage,
            CreatedAt = task.CompletedAt ?? task.UpdatedAt
        };
    }

    private async Task<Image2QueuedTask?> ReadQueuedTaskFromFolderAsync(string folder)
    {
        var task = await ReadJsonAsync<Image2QueuedTask>(folder, "task.json");
        if (task is null || task.Status is Image2TaskStatus.Completed or Image2TaskStatus.Failed)
        {
            return null;
        }

        task.OutputFolder ??= folder;
        task.VersionGroupId = string.IsNullOrWhiteSpace(task.VersionGroupId) ? task.Id : task.VersionGroupId;
        if (task.Status == Image2TaskStatus.Running)
        {
            task.Status = Image2TaskStatus.Stopped;
            task.StatusDetail = "导入时发现上次仍在运行，已暂停等待重试";
        }

        return task;
    }

    private static string SerializeJson<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static object BuildParameterSnapshot(Image2QueuedTask task)
    {
        return new
        {
            task.Id,
            task.VersionGroupId,
            task.Title,
            task.Mode,
            task.Size,
            task.Quality,
            task.ResponseFormat,
            task.OutputFormat,
            task.Background,
            task.Transparent,
            task.UseAsyncTask,
            task.UseChatEndpoint,
            task.StreamChat,
            task.AssignmentMode,
            task.RequestedConnectionTitle,
            task.AssignedConnectionTitle,
            ReferenceImageCount = task.ReferenceImages.Count,
            ReferenceImageUrlCount = task.ReferenceImageUrls.Count,
            task.CreatedAt,
            task.StartedAt,
            task.CompletedAt,
            task.Status,
            task.StatusDetail
        };
    }

    private static async Task CopyReferencesToOutputAsync(Image2QueuedTask task, string folder)
    {
        foreach (var reference in task.ReferenceImages)
        {
            if (!File.Exists(reference.Path))
            {
                continue;
            }

            await OutputFolderWriter.CopyFileAsync(folder, Path.Combine("references", SanitizeFileName(reference.FileName)), reference.Path);
        }

        if (task.ReferenceImageUrls.Count > 0)
        {
            await OutputFolderWriter.WriteTextAsync(folder, Path.Combine("references", "reference-urls.txt"), string.Join(Environment.NewLine, task.ReferenceImageUrls));
        }
    }

    private static string CombineOutputPath(string folder, string relativePath)
    {
        return OutputFolderWriter.IsDocumentTreePath(folder)
            ? $"{folder}/{relativePath.Replace('\\', '/')}"
            : Path.Combine(folder, relativePath);
    }

    private static async Task<string?> ResolveFolderPathAsync(string root, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        if (OutputFolderWriter.IsDocumentTreePath(root))
        {
            if (folder.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
            {
                return folder;
            }

            return await OutputFolderWriter.FindDirectoryAsync(root, folder.Trim('/'));
        }

        return Path.IsPathRooted(folder) ? folder : Path.Combine(root, folder);
    }

    private static string GetFolderReference(string root, string folder)
    {
        if (OutputFolderWriter.IsDocumentTreePath(root))
        {
            return folder;
        }

        try
        {
            return Path.GetRelativePath(root, folder);
        }
        catch
        {
            return folder;
        }
    }

    private static string GetHistoryGroupId(Image2HistoryEntry history)
    {
        return string.IsNullOrWhiteSpace(history.SourceTaskId) ? history.Id : history.SourceTaskId;
    }

    private static Image2ReferenceAsset CloneReference(Image2ReferenceAsset asset)
    {
        return new Image2ReferenceAsset
        {
            FileName = asset.FileName,
            Path = asset.Path
        };
    }

    private static string GetUniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        if (!File.Exists(path))
        {
            return path;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; ; index++)
        {
            path = Path.Combine(folder, $"{name}-{index}{extension}");
            if (!File.Exists(path))
            {
                return path;
            }
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "task" : sanitized;
    }

    private static string NormalizeImageExtension(string value)
    {
        return value.Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "jpg" => "jpg",
            "jpeg" => "jpg",
            "webp" => "webp",
            _ => "png"
        };
    }

    private static string GuessExtensionFromUrl(string imageUrl, string fallback)
    {
        if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
        {
            var extension = Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToLowerInvariant();
            if (extension is "png" or "jpg" or "jpeg" or "webp")
            {
                return extension == "jpeg" ? "jpg" : extension;
            }
        }

        return NormalizeImageExtension(fallback);
    }

    private sealed class Image2LibraryIndex
    {
        public string App { get; set; } = "Image2 Studio";
        public int Version { get; set; } = 1;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public List<Image2LibraryIndexEntry> Entries { get; set; } = new();
    }

    private sealed class Image2LibraryIndexEntry
    {
        public string TaskId { get; set; } = string.Empty;
        public string VersionGroupId { get; set; } = string.Empty;
        public int Version { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Folder { get; set; } = string.Empty;
        public string StatusText { get; set; } = string.Empty;
        public bool? Succeeded { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        public static Image2LibraryIndexEntry FromTask(Image2QueuedTask task, bool? succeeded, Image2Settings settings)
        {
            var root = settings.GetResolvedOutputRootPath();
            return new Image2LibraryIndexEntry
            {
                TaskId = task.Id,
                VersionGroupId = string.IsNullOrWhiteSpace(task.VersionGroupId) ? task.Id : task.VersionGroupId,
                Version = 0,
                Title = task.Title,
                Folder = string.IsNullOrWhiteSpace(task.OutputFolder) ? string.Empty : GetFolderReference(root, task.OutputFolder),
                StatusText = task.StatusDetail,
                Succeeded = succeeded,
                UpdatedAt = DateTime.Now
            };
        }

        public static Image2LibraryIndexEntry FromHistory(Image2HistoryEntry history, Image2Settings settings)
        {
            var root = settings.GetResolvedOutputRootPath();
            return new Image2LibraryIndexEntry
            {
                TaskId = history.Id,
                VersionGroupId = GetHistoryGroupId(history),
                Version = history.Version,
                Title = history.Title,
                Folder = string.IsNullOrWhiteSpace(history.OutputFolder) ? string.Empty : GetFolderReference(root, history.OutputFolder),
                StatusText = history.StatusText,
                Succeeded = history.Succeeded,
                UpdatedAt = history.CreatedAt
            };
        }
    }

    private sealed class Image2TaskManifest
    {
        public string Id { get; set; } = string.Empty;
        public string VersionGroupId { get; set; } = string.Empty;
        public int Version { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string Mode { get; set; } = "Chat";
        public string Size { get; set; } = "1024x1024";
        public string Quality { get; set; } = "auto";
        public string ResponseFormat { get; set; } = "url";
        public string OutputFormat { get; set; } = "png";
        public string Background { get; set; } = "auto";
        public bool Transparent { get; set; }
        public bool UseAsyncTask { get; set; }
        public bool UseChatEndpoint { get; set; } = true;
        public bool StreamChat { get; set; } = true;
        public Image2ConnectionAssignmentMode AssignmentMode { get; set; } = Image2ConnectionAssignmentMode.Auto;
        public string? RequestedConnectionId { get; set; }
        public string? RequestedConnectionTitle { get; set; }
        public List<Image2ReferenceAsset> ReferenceImages { get; set; } = new();
        public List<string> ReferenceImageUrls { get; set; } = new();
        public string? ImagePath { get; set; }
        public string? ImageUrl { get; set; }
        public string? AssignedConnectionId { get; set; }
        public string? AssignedConnectionTitle { get; set; }
        public string? AssistantText { get; set; }
        public string? RawJson { get; set; }
        public bool? Succeeded { get; set; }
        public string StatusText { get; set; } = "任务已保存";
        public string? ErrorMessage { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public static Image2TaskManifest FromTask(Image2QueuedTask task, bool? succeeded)
        {
            return new Image2TaskManifest
            {
                Id = task.Id,
                VersionGroupId = string.IsNullOrWhiteSpace(task.VersionGroupId) ? task.Id : task.VersionGroupId,
                Version = 0,
                Title = task.Title,
                Prompt = task.Prompt,
                Mode = task.Mode,
                Size = task.Size,
                Quality = task.Quality,
                ResponseFormat = task.ResponseFormat,
                OutputFormat = task.OutputFormat,
                Background = task.Background,
                Transparent = task.Transparent,
                UseAsyncTask = task.UseAsyncTask,
                UseChatEndpoint = task.UseChatEndpoint,
                StreamChat = task.StreamChat,
                AssignmentMode = task.AssignmentMode,
                RequestedConnectionId = task.RequestedConnectionId,
                RequestedConnectionTitle = task.RequestedConnectionTitle,
                ReferenceImages = task.ReferenceImages.Select(CloneReference).ToList(),
                ReferenceImageUrls = task.ReferenceImageUrls.ToList(),
                ImagePath = task.ImagePath,
                ImageUrl = task.ImageUrl,
                AssignedConnectionId = task.AssignedConnectionId,
                AssignedConnectionTitle = task.AssignedConnectionTitle,
                AssistantText = task.AssistantText,
                RawJson = task.RawJson,
                Succeeded = succeeded,
                StatusText = succeeded switch
                {
                    true => "生图成功",
                    false => "生图失败",
                    _ => task.StatusDetail
                },
                ErrorMessage = task.ErrorMessage,
                CreatedAt = task.CompletedAt ?? task.UpdatedAt
            };
        }

        public static Image2TaskManifest FromHistory(Image2HistoryEntry history)
        {
            return new Image2TaskManifest
            {
                Id = history.Id,
                VersionGroupId = GetHistoryGroupId(history),
                Version = history.Version,
                Title = history.Title,
                Prompt = history.Prompt,
                Mode = history.Mode,
                Size = history.Size,
                Quality = history.Quality,
                ResponseFormat = history.ResponseFormat,
                OutputFormat = history.OutputFormat,
                Background = history.Background,
                Transparent = history.Transparent,
                UseAsyncTask = history.UseAsyncTask,
                UseChatEndpoint = history.UseChatEndpoint,
                StreamChat = history.StreamChat,
                AssignmentMode = history.AssignmentMode,
                RequestedConnectionId = history.RequestedConnectionId,
                RequestedConnectionTitle = history.RequestedConnectionTitle,
                ReferenceImages = history.ReferenceImages.Select(CloneReference).ToList(),
                ReferenceImageUrls = history.ReferenceImageUrls.ToList(),
                ImagePath = history.ImagePath,
                ImageUrl = history.ImageUrl,
                AssignedConnectionId = history.AssignedConnectionId,
                AssignedConnectionTitle = history.AssignedConnectionTitle,
                AssistantText = history.AssistantText,
                RawJson = history.RawJson,
                Succeeded = history.Succeeded,
                StatusText = history.StatusText,
                ErrorMessage = history.ErrorMessage,
                CreatedAt = history.CreatedAt
            };
        }

        public Image2HistoryEntry ToHistory(string folder)
        {
            return new Image2HistoryEntry
            {
                Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
                SourceTaskId = string.IsNullOrWhiteSpace(VersionGroupId) ? Id : VersionGroupId,
                Version = Version <= 0 ? 1 : Version,
                Title = Title,
                Prompt = Prompt,
                Mode = Mode,
                Size = Size,
                Quality = Quality,
                ResponseFormat = ResponseFormat,
                OutputFormat = OutputFormat,
                Background = Background,
                Transparent = Transparent,
                UseAsyncTask = UseAsyncTask,
                UseChatEndpoint = UseChatEndpoint,
                StreamChat = StreamChat,
                AssignmentMode = AssignmentMode,
                RequestedConnectionId = RequestedConnectionId,
                RequestedConnectionTitle = RequestedConnectionTitle,
                ReferenceImages = ReferenceImages.Select(CloneReference).ToList(),
                ReferenceImageUrls = ReferenceImageUrls.ToList(),
                OutputFolder = folder,
                ImagePath = ImagePath,
                ImageUrl = ImageUrl,
                AssignedConnectionId = AssignedConnectionId,
                AssignedConnectionTitle = AssignedConnectionTitle,
                AssistantText = AssistantText,
                RawJson = RawJson,
                Succeeded = Succeeded == true,
                StatusText = string.IsNullOrWhiteSpace(StatusText) ? (Succeeded == true ? "生图成功" : "生图失败") : StatusText,
                ErrorMessage = ErrorMessage,
                CreatedAt = CreatedAt
            };
        }
    }
}

public sealed record Image2ImportedLibrary(
    IReadOnlyList<Image2QueuedTask> Queue,
    IReadOnlyList<Image2HistoryEntry> History);
