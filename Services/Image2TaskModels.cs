using System.Text.Json.Serialization;

namespace Image2Studio.Services;

public enum Image2TaskStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Stopped
}

public enum Image2ConnectionAssignmentMode
{
    Auto,
    Manual
}

public sealed class Image2ReferenceAsset
{
    public string FileName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

public sealed class Image2QueuedTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string VersionGroupId { get; set; } = string.Empty;
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
    public Image2TaskStatus Status { get; set; } = Image2TaskStatus.Queued;
    public string StatusDetail { get; set; } = "等待中";
    public string? AssignedConnectionId { get; set; }
    public string? AssignedConnectionTitle { get; set; }
    public string? OutputFolder { get; set; }
    public string? ImagePath { get; set; }
    public string? ImageUrl { get; set; }
    public string? ErrorMessage { get; set; }
    public string? AssistantText { get; set; }
    public string? RawJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    [JsonIgnore]
    public bool CanEdit => Status is Image2TaskStatus.Queued or Image2TaskStatus.Stopped or Image2TaskStatus.Failed;
}

public sealed class Image2HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceTaskId { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
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
    public string? OutputFolder { get; set; }
    public string? ImagePath { get; set; }
    public string? ImageUrl { get; set; }
    public string? AssignedConnectionId { get; set; }
    public string? AssignedConnectionTitle { get; set; }
    public string? AssistantText { get; set; }
    public string? RawJson { get; set; }
    public bool Succeeded { get; set; }
    public string StatusText { get; set; } = "生图成功";
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public sealed class Image2QueueState
{
    public List<Image2QueuedTask> Queue { get; set; } = new();
    public List<Image2HistoryEntry> History { get; set; } = new();
}
