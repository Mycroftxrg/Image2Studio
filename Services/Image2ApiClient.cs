using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Image2Studio.Services;

public sealed partial class Image2ApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly HttpClient _httpClient;

    public Image2ApiClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(10);
    }

    public Task<Image2Result> GenerateImageAsync(Image2RequestOptions options, CancellationToken cancellationToken = default)
    {
        ValidateCommonOptions(options);

        if (options.UseAsyncTask)
        {
            return RunAsyncTaskAsync(options, cancellationToken);
        }

        if (options.UseChatEndpoint)
        {
            return ChatAsync(options, cancellationToken);
        }

        if (options.HasReferenceImages)
        {
            return EditImageAsync(options, cancellationToken);
        }

        return CreateGenerationAsync(options, cancellationToken);
    }

    private async Task<Image2Result> CreateGenerationAsync(Image2RequestOptions options, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = options.Model.Trim(),
            ["prompt"] = options.Prompt.Trim(),
            ["size"] = options.Size,
            ["quality"] = options.Quality,
            ["n"] = 1,
            ["response_format"] = options.ResponseFormat,
            ["output_format"] = options.OutputFormat
        };

        AddOptionalImageParameters(body, options);
        var request = CreateJsonRequest(HttpMethod.Post, options, "/v1/images/generations", body);
        var raw = await SendForStringAsync(request, cancellationToken);
        return ParseImageResult("images/generations", options.Prompt, raw);
    }

    private async Task<Image2Result> ChatAsync(Image2RequestOptions options, CancellationToken cancellationToken)
    {
        var content = await BuildChatMessageContentAsync(options, cancellationToken);
        var body = new Dictionary<string, object?>
        {
            ["model"] = options.Model.Trim(),
            ["stream"] = options.StreamChat,
            ["messages"] = new object[]
            {
                new
                {
                    role = "user",
                    content
                }
            }
        };

        AddGroupIfPresent(body, options);
        var request = CreateJsonRequest(HttpMethod.Post, options, "/v1/chat/completions", body);
        if (options.StreamChat)
        {
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        var raw = await SendForStringAsync(request, cancellationToken);
        return ParseChatResult(options.Prompt, raw);
    }

    private static async Task<object> BuildChatMessageContentAsync(
        Image2RequestOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.HasReferenceImages)
        {
            return options.Prompt.Trim();
        }

        var parts = new List<object>
        {
            new
            {
                type = "text",
                text = options.Prompt.Trim()
            }
        };

        foreach (var referenceImage in options.ReferenceImages)
        {
            parts.Add(CreateChatImagePart(await BuildReferenceImageDataUrlAsync(referenceImage, cancellationToken)));
        }

        foreach (var referenceImageUrl in NormalizeReferenceImageUrls(options.ReferenceImageUrls))
        {
            parts.Add(CreateChatImagePart(referenceImageUrl));
        }

        return parts.ToArray();
    }

    private async Task<Image2Result> EditImageAsync(Image2RequestOptions options, CancellationToken cancellationToken)
    {
        if (!options.HasReferenceImages)
        {
            throw new InvalidOperationException("请选择至少一张参考图。");
        }

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(options.Model.Trim(), Encoding.UTF8), "model");
        form.Add(new StringContent(options.Prompt.Trim(), Encoding.UTF8), "prompt");
        form.Add(new StringContent(options.Size, Encoding.UTF8), "size");
        form.Add(new StringContent(options.Quality, Encoding.UTF8), "quality");
        form.Add(new StringContent(options.ResponseFormat, Encoding.UTF8), "response_format");
        form.Add(new StringContent(options.OutputFormat, Encoding.UTF8), "output_format");
        form.Add(new StringContent("high", Encoding.UTF8), "input_fidelity");

        if (!string.Equals(options.Background, "auto", StringComparison.OrdinalIgnoreCase))
        {
            form.Add(new StringContent(options.Background, Encoding.UTF8), "background");
        }

        if (options.Transparent)
        {
            form.Add(new StringContent("true", Encoding.UTF8), "transparent");
        }

        if (!string.IsNullOrWhiteSpace(options.Group))
        {
            form.Add(new StringContent(options.Group.Trim(), Encoding.UTF8), "group");
        }

        var imageStreams = new List<Stream>();
        try
        {
            foreach (var referenceImage in options.ReferenceImages)
            {
                var imageStream = await referenceImage.OpenReadAsync();
                imageStreams.Add(imageStream);
                AddImageContent(
                    form,
                    new StreamContent(imageStream),
                    referenceImage.FileName,
                    GuessContentType(referenceImage.FileName));
            }

            foreach (var referenceImageUrl in NormalizeReferenceImageUrls(options.ReferenceImageUrls))
            {
                var downloaded = await DownloadReferenceImageAsync(referenceImageUrl, cancellationToken);
                AddImageContent(
                    form,
                    new ByteArrayContent(downloaded.Bytes),
                    downloaded.FileName,
                    downloaded.ContentType);
            }

            var request = CreateRequest(HttpMethod.Post, options, "/v1/images/edits");
            request.Content = form;

            var raw = await SendForStringAsync(request, cancellationToken);
            return ParseImageResult("images/edits", options.Prompt, raw);
        }
        finally
        {
            foreach (var imageStream in imageStreams)
            {
                await imageStream.DisposeAsync();
            }
        }
    }

    private async Task<Image2Result> RunAsyncTaskAsync(Image2RequestOptions options, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = options.Model.Trim(),
            ["prompt"] = options.Prompt.Trim(),
            ["size"] = options.Size,
            ["quality"] = options.Quality
        };

        if (!string.IsNullOrWhiteSpace(options.Group))
        {
            body["group"] = options.Group.Trim();
        }

        if (!options.HasReferenceImages)
        {
            var request = CreateJsonRequest(HttpMethod.Post, options, "/v1/image-tasks/generations", body);
            var createdRaw = await SendForStringAsync(request, cancellationToken);
            return await PollTaskAsync(options, "image-tasks/generations", createdRaw, cancellationToken);
        }

        var images = new List<object>();
        foreach (var referenceImage in options.ReferenceImages)
        {
            images.Add(new
            {
                image_url = await BuildReferenceImageDataUrlAsync(referenceImage, cancellationToken)
            });
        }

        foreach (var referenceImageUrl in NormalizeReferenceImageUrls(options.ReferenceImageUrls))
        {
            images.Add(new
            {
                image_url = referenceImageUrl
            });
        }

        body["images"] = images.ToArray();

        var editRequest = CreateJsonRequest(HttpMethod.Post, options, "/v1/image-tasks/edits", body);
        var editCreatedRaw = await SendForStringAsync(editRequest, cancellationToken);
        return await PollTaskAsync(options, "image-tasks/edits", editCreatedRaw, cancellationToken);
    }

    private async Task<Image2Result> PollTaskAsync(
        Image2RequestOptions options,
        string endpoint,
        string createdRaw,
        CancellationToken cancellationToken)
    {
        using var createdDocument = JsonDocument.Parse(createdRaw);
        var createdRoot = createdDocument.RootElement.Clone();
        var taskId = ExtractTaskId(createdRoot);
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return ParseImageResult(endpoint, options.Prompt, createdRaw);
        }

        var rawHistory = new StringBuilder();
        rawHistory.AppendLine(createdRaw);

        var transientReadFailures = 0;
        for (var attempt = 1; attempt <= 120; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(attempt <= 6 ? 2 : 5), cancellationToken);

            var taskRequest = CreateRequest(HttpMethod.Get, options, $"/v1/image-tasks/{Uri.EscapeDataString(taskId)}?detail=true");
            string taskRaw;
            try
            {
                taskRaw = await SendForStringAsync(taskRequest, cancellationToken);
                transientReadFailures = 0;
            }
            catch (HttpRequestException ex) when (IsPrematureResponseEnd(ex) && transientReadFailures < 8)
            {
                transientReadFailures++;
                rawHistory.AppendLine();
                rawHistory.AppendLine($"[poll transient read failure {transientReadFailures}] {ex.Message}");
                continue;
            }

            rawHistory.AppendLine();
            rawHistory.AppendLine(taskRaw);

            using var taskDocument = JsonDocument.Parse(taskRaw);
            var taskRoot = taskDocument.RootElement.Clone();
            var status = GetStringProperty(taskRoot, "status");

            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = ParseImageResult(endpoint, options.Prompt, taskRaw);
                return parsed with { RawJson = rawHistory.ToString() };
            }

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = ParseImageResult(endpoint, options.Prompt, taskRaw);
                return parsed with { RawJson = rawHistory.ToString() };
            }
        }

        throw new TimeoutException($"异步任务 {taskId} 在 10 分钟内未完成。");
    }

    private static void AddOptionalImageParameters(IDictionary<string, object?> body, Image2RequestOptions options)
    {
        if (!string.Equals(options.Background, "auto", StringComparison.OrdinalIgnoreCase))
        {
            body["background"] = options.Background;
        }

        if (options.Transparent)
        {
            body["transparent"] = true;
        }

        AddGroupIfPresent(body, options);
    }

    private static void AddGroupIfPresent(IDictionary<string, object?> body, Image2RequestOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Group))
        {
            body["group"] = options.Group.Trim();
        }
    }

    private HttpRequestMessage CreateJsonRequest(HttpMethod method, Image2RequestOptions options, string path, object body)
    {
        var request = CreateRequest(method, options, path);
        var json = JsonSerializer.Serialize(body, JsonOptions);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Image2RequestOptions options, string path)
    {
        var request = new HttpRequestMessage(method, BuildRequestUri(options.BaseUrl, path));
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptEncoding.ParseAdd("identity");
        request.Headers.ConnectionClose = true;
        return request;
    }

    private static Uri BuildRequestUri(string baseUrlOrEndpoint, string path)
    {
        var input = baseUrlOrEndpoint.Trim().TrimEnd('/');
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("接口地址必须是完整 URL，例如 https://apinebula.com");
        }

        var lowerPath = uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        var normalizedBase = lowerPath switch
        {
            "/v1/images/generations" or
            "/v1/images/edits" or
            "/v1/chat/completions" or
            "/v1/image-tasks/generations" or
            "/v1/image-tasks/edits" => $"{uri.Scheme}://{uri.Authority}",
            _ when lowerPath.StartsWith("/v1/image-tasks/", StringComparison.OrdinalIgnoreCase) => $"{uri.Scheme}://{uri.Authority}",
            "" or "/" => $"{uri.Scheme}://{uri.Authority}",
            _ => input
        };

        return new Uri($"{normalizedBase.TrimEnd('/')}{path}");
    }

    private async Task<string> SendForStringAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex) when (IsPrematureResponseEnd(ex))
        {
            throw new HttpRequestException(
                "连接在服务端返回结果前提前关闭。这个接口生成时间较长时容易发生，请使用“聊天”模式并开启 stream=true，或在“文生图”模式勾选“使用异步任务接口”。",
                ex);
        }

        using (response)
        {
            var raw = await ReadResponseBodyLenientAsync(response, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var detail = string.IsNullOrWhiteSpace(raw) ? response.ReasonPhrase : raw;
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.StatusCode}: {detail}");
            }

            return PrettyJsonOrRaw(raw);
        }
    }

    private static async Task<string> ReadResponseBodyLenientAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        Exception? readException = null;

        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            }
            catch (IOException ex) when (memory.Length > 0)
            {
                readException = ex;
                break;
            }
            catch (HttpIOException ex) when (memory.Length > 0)
            {
                readException = ex;
                break;
            }

            if (read == 0)
            {
                break;
            }

            memory.Write(buffer, 0, read);
        }

        var raw = Encoding.UTF8.GetString(memory.ToArray());
        if (readException is not null && !LooksLikeCompletePayload(raw))
        {
            throw new HttpRequestException(
                "响应读取过程中连接提前关闭，且已收到的内容不完整。",
                readException);
        }

        return raw;
    }

    private static Image2Result ParseImageResult(string endpoint, string prompt, string rawJson)
    {
        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement.Clone();

        var imageUrl = FindFirstImageUrl(root);
        var imageBytes = TryFindFirstImageBytes(root);
        var assistantText = ExtractText(root);

        if (imageBytes is null && imageUrl is null && assistantText is not null)
        {
            imageUrl = ExtractMarkdownImageUrl(assistantText);
        }

        return new Image2Result(endpoint, prompt, imageUrl, imageBytes, assistantText, rawJson, root);
    }

    private static Image2Result ParseChatResult(string prompt, string rawJson)
    {
        if (LooksLikeServerSentEvents(rawJson))
        {
            return ParseChatStreamResult(prompt, rawJson);
        }

        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement.Clone();
        var assistantText = ExtractText(root);
        var imageUrl = FindFirstImageUrl(root) ?? ExtractMarkdownImageUrl(assistantText);
        var imageBytes = TryFindFirstImageBytes(root);

        return new Image2Result("chat/completions", prompt, imageUrl, imageBytes, assistantText, rawJson, root);
    }

    private static Image2Result ParseChatStreamResult(string prompt, string rawEvents)
    {
        var assistantText = new StringBuilder();
        string? imageUrl = null;
        byte[]? imageBytes = null;

        foreach (var line in rawEvents.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var payload = trimmed[5..].Trim();
            if (payload.Length == 0 || payload.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                var text = ExtractText(root);
                if (!string.IsNullOrEmpty(text))
                {
                    assistantText.Append(text);
                }

                imageUrl ??= FindFirstImageUrl(root);
                imageBytes ??= TryFindFirstImageBytes(root);
            }
            catch (JsonException)
            {
                assistantText.Append(payload);
            }
        }

        var textResult = assistantText.ToString();
        imageUrl ??= ExtractMarkdownImageUrl(textResult);

        return new Image2Result(
            "chat/completions stream",
            prompt,
            imageUrl,
            imageBytes,
            string.IsNullOrWhiteSpace(textResult) ? null : textResult,
            rawEvents,
            null);
    }

    private static string? FindFirstImageUrl(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (LooksLikeImageUrl(value) ||
                        (LooksLikeImageUrlField(property.Name) && LooksLikeHttpUrl(value)))
                    {
                        return value;
                    }
                }

                var nested = FindFirstImageUrl(property.Value);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindFirstImageUrl(item);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static byte[]? TryFindFirstImageBytes(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && LooksLikeImageBase64Field(property.Name))
                {
                    var bytes = TryDecodeBase64Image(property.Value.GetString());
                    if (bytes is not null)
                    {
                        return bytes;
                    }
                }

                var nested = TryFindFirstImageBytes(property.Value);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = TryFindFirstImageBytes(item);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string? ExtractText(JsonElement root)
    {
        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content))
            {
                return JsonContentToString(content);
            }

            if (first.TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("content", out var deltaContent))
            {
                return JsonContentToString(deltaContent);
            }
        }

        if (root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array &&
            data.GetArrayLength() > 0)
        {
            var first = data[0];
            if (first.TryGetProperty("revised_prompt", out var revisedPrompt))
            {
                return revisedPrompt.GetString();
            }
        }

        return null;
    }

    private static string? ExtractTaskId(JsonElement root)
    {
        return GetStringProperty(root, "task_id") ?? GetStringProperty(root, "id");
    }

    private static string? GetStringProperty(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? JsonContentToString(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return content.ToString();
        }

        var builder = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object &&
                part.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                builder.AppendLine(text.GetString());
            }
            else if (part.ValueKind == JsonValueKind.String)
            {
                builder.AppendLine(part.GetString());
            }
        }

        return builder.Length == 0 ? content.ToString() : builder.ToString().Trim();
    }

    private static string PrettyJsonOrRaw(string raw)
    {
        if (LooksLikeServerSentEvents(raw))
        {
            return raw;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(document.RootElement, JsonOptions);
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private static bool LooksLikeServerSentEvents(string raw)
    {
        var trimmed = raw.TrimStart();
        return trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("\ndata:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("\r\ndata:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeCompletePayload(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (LooksLikeServerSentEvents(raw))
        {
            return raw.Contains("[DONE]", StringComparison.OrdinalIgnoreCase) ||
                   ExtractMarkdownImageUrl(raw) is not null ||
                   LooksLikeImageUrl(raw);
        }

        try
        {
            using var _ = JsonDocument.Parse(raw);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsPrematureResponseEnd(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpIOException ||
                current.Message.Contains("response ended prematurely", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("ResponseEnded", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string GuessContentType(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/png"
        };
    }

    private static string GuessExtensionFromContentType(string? contentType)
    {
        return contentType?.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".png"
        };
    }

    private static object CreateChatImagePart(string imageUrl)
    {
        return new
        {
            type = "image_url",
            image_url = new
            {
                url = imageUrl
            }
        };
    }

    private static IEnumerable<string> NormalizeReferenceImageUrls(IEnumerable<string> referenceImageUrls)
    {
        return referenceImageUrls
            .SelectMany(value => (value ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => !string.IsNullOrWhiteSpace(value));
    }

    private static async Task<string> BuildReferenceImageDataUrlAsync(
        FileResult referenceImage,
        CancellationToken cancellationToken)
    {
        await using var stream = await referenceImage.OpenReadAsync();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);

        if (memory.Length == 0)
        {
            throw new InvalidOperationException("参考图文件为空。");
        }

        var contentType = GuessContentType(referenceImage.FileName);
        return $"data:{contentType};base64,{Convert.ToBase64String(memory.ToArray())}";
    }

    private async Task<(byte[] Bytes, string FileName, string ContentType)> DownloadReferenceImageAsync(
        string imageUrl,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("参考图 URL 必须是 http 或 https 图片地址。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"下载参考图失败：HTTP {(int)response.StatusCode} {response.StatusCode}");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException("参考图 URL 返回了空文件。");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(contentType) || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            contentType = GuessContentType(uri.AbsolutePath);
        }

        var fileName = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
        {
            fileName = $"reference{GuessExtensionFromContentType(contentType)}";
        }

        return (bytes, fileName, contentType);
    }

    private static void AddImageContent(
        MultipartFormDataContent form,
        HttpContent content,
        string fileName,
        string contentType)
    {
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(content, "image", fileName);
    }

    private static bool LooksLikeImageBase64Field(string fieldName)
    {
        return fieldName.Equals("b64_json", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Equals("image_base64", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Equals("base64", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[]? TryDecodeBase64Image(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var data = value.Trim();
        var commaIndex = data.IndexOf(',');
        if (data.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && commaIndex > -1)
        {
            data = data[(commaIndex + 1)..];
        }

        try
        {
            return Convert.FromBase64String(data);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool LooksLikeImageUrl(string? value)
    {
        if (!LooksLikeHttpUrl(value))
        {
            return false;
        }

        var lower = value!.ToLowerInvariant();
        return lower.Contains(".png") ||
               lower.Contains(".jpg") ||
               lower.Contains(".jpeg") ||
               lower.Contains(".webp") ||
               lower.Contains("/image") ||
               lower.Contains("pubimage");
    }

    private static bool LooksLikeHttpUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return true;
    }

    private static bool LooksLikeImageUrlField(string fieldName)
    {
        return fieldName.Equals("url", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Equals("uri", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Equals("image", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Equals("image_url", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Equals("imageUrl", StringComparison.Ordinal) ||
               fieldName.Equals("output_url", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Equals("result_url", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Equals("public_url", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("image", StringComparison.OrdinalIgnoreCase) && fieldName.Contains("url", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractMarkdownImageUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = MarkdownImageRegex().Match(text);
        return match.Success ? match.Groups["url"].Value : null;
    }

    private static void ValidateCommonOptions(Image2RequestOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("请输入 API key。");
        }

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new InvalidOperationException("请输入接口地址。");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new InvalidOperationException("请输入模型名。");
        }

        if (string.IsNullOrWhiteSpace(options.Prompt))
        {
            throw new InvalidOperationException("请输入提示词。");
        }
    }

    [GeneratedRegex(@"!\[[^\]]*\]\((?<url>https?://[^)\s]+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MarkdownImageRegex();
}
