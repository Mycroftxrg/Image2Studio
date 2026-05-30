using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Image2Studio.Services;

public sealed record DeepSeekPromptExpansion(string Prompt, string? Reasoning);

public sealed class DeepSeekPromptExpander
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly HttpClient _httpClient;

    public DeepSeekPromptExpander(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(3);
    }

    public async Task<DeepSeekPromptExpansion> ExpandAsync(Image2Settings settings, string prompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.DeepSeekApiKey))
        {
            throw new InvalidOperationException("请先在设置中填写 DeepSeek API Key。");
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException("请先填写需要扩写的提示词。");
        }

        var baseUrl = NormalizeBaseUrl(settings.DeepSeekBaseUrl);
        var endpoint = $"{baseUrl}/chat/completions";
        var model = string.IsNullOrWhiteSpace(settings.DeepSeekModel)
            ? Image2Settings.DefaultDeepSeekModel
            : settings.DeepSeekModel.Trim();

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["stream"] = false,
            ["max_tokens"] = 2200,
            ["messages"] = new object[]
            {
                new
                {
                    role = "system",
                    content = string.IsNullOrWhiteSpace(settings.DeepSeekSystemPrompt)
                        ? Image2Settings.DefaultDeepSeekSystemPrompt
                        : settings.DeepSeekSystemPrompt.Trim()
                },
                new
                {
                    role = "user",
                    content = BuildUserPrompt(prompt)
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.DeepSeekApiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"DeepSeek 扩写失败：{(int)response.StatusCode} {response.ReasonPhrase}\n{TrimForMessage(raw)}");
        }

        return ExtractContent(raw);
    }

    private static DeepSeekPromptExpansion ExtractContent(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("DeepSeek 返回中没有 choices。");
        }

        var choice = choices[0];
        if (!choice.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var contentElement))
        {
            throw new InvalidOperationException("DeepSeek 返回中没有最终文本。");
        }

        var content = contentElement.GetString()?.Trim() ?? string.Empty;
        content = StripCodeFence(content).Trim();
        var reasoning = message.TryGetProperty("reasoning_content", out var reasoningElement)
            ? reasoningElement.GetString()?.Trim()
            : null;

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("DeepSeek 返回了空提示词。");
        }

        return new DeepSeekPromptExpansion(content, string.IsNullOrWhiteSpace(reasoning) ? null : reasoning);
    }

    private static string BuildUserPrompt(string prompt)
    {
        return $"""
               原始提示词如下：
               {prompt.Trim()}
               """;
    }

    private static string NormalizeBaseUrl(string? value)
    {
        var input = string.IsNullOrWhiteSpace(value)
            ? Image2Settings.DefaultDeepSeekBaseUrl
            : value.Trim().TrimEnd('/');

        if (input.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return input[..^"/chat/completions".Length].TrimEnd('/');
        }

        return input;
    }

    private static string StripCodeFence(string value)
    {
        var match = Regex.Match(value, "^```(?:text|prompt|markdown)?\\s*(.*?)\\s*```$", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : value;
    }

    private static string TrimForMessage(string value)
    {
        value = value.Trim();
        return value.Length <= 600 ? value : value[..600] + "...";
    }
}
