using System.Text.Json;

namespace Image2Studio.Services;

public sealed class Image2TaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _statePath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public Image2TaskStore()
    {
        var folder = Path.Combine(FileSystem.Current.AppDataDirectory, "queue");
        Directory.CreateDirectory(folder);
        _statePath = Path.Combine(folder, "image2-queue.json");
    }

    public async Task<Image2QueueState> LoadAsync()
    {
        if (!File.Exists(_statePath))
        {
            return new Image2QueueState();
        }

        await using var stream = File.OpenRead(_statePath);
        var state = await JsonSerializer.DeserializeAsync<Image2QueueState>(stream, JsonOptions);
        return state ?? new Image2QueueState();
    }

    public async Task SaveAsync(Image2QueueState state)
    {
        await _saveLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var tempPath = $"{_statePath}.{Guid.NewGuid():N}.tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions);
            }

            if (File.Exists(_statePath))
            {
                File.Delete(_statePath);
            }

            File.Move(tempPath, _statePath);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
