using System.Text.Json;

namespace Image2Studio.Services;

public sealed record Image2Result(
    string Endpoint,
    string Prompt,
    string? ImageUrl,
    byte[]? ImageBytes,
    string? AssistantText,
    string RawJson,
    JsonElement? ParsedJson)
{
    public bool HasImage => ImageBytes is { Length: > 0 } || !string.IsNullOrWhiteSpace(ImageUrl);
}
