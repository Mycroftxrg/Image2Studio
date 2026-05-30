namespace Image2Studio.Services;

public sealed class Image2RequestOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://apinebula.com";
    public string Model { get; set; } = "gpt-image-2-vip";
    public string? Group { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string Size { get; set; } = "1024x1024";
    public string Quality { get; set; } = "auto";
    public string ResponseFormat { get; set; } = "url";
    public string OutputFormat { get; set; } = "png";
    public string Background { get; set; } = "auto";
    public bool Transparent { get; set; }
    public bool UseAsyncTask { get; set; }
    public bool UseChatEndpoint { get; set; }
    public bool StreamChat { get; set; }
    public IReadOnlyList<FileResult> ReferenceImages { get; set; } = Array.Empty<FileResult>();
    public IReadOnlyList<string> ReferenceImageUrls { get; set; } = Array.Empty<string>();

    public bool HasReferenceImages =>
        ReferenceImages.Count > 0 ||
        ReferenceImageUrls.Any(url => !string.IsNullOrWhiteSpace(url));
}
