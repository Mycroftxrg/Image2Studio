namespace Image2Studio;

public sealed class Image2Settings
{
    public const string DefaultApiKey = "";
    public const string DefaultBaseUrl = "https://apinebula.com";
    public const string DefaultModel = "gpt-image-2-vip";
    public const string DefaultGroup = "vip_image2";
    public const string DefaultPrompt = "";
    public const string DefaultDeepSeekBaseUrl = "https://api.deepseek.com";
    public const string DefaultDeepSeekModel = "deepseek-chat";
    public const string DefaultWindowsUpdateManifestUrl = "https://raw.githubusercontent.com/Mycroftxrg/Image2Studio/main/latest-windows.json";
    public const string DefaultAndroidUpdateManifestUrl = "https://raw.githubusercontent.com/Mycroftxrg/Image2Studio/main/latest-android.json";
    public const string DefaultUpdateManifestUrl = DefaultWindowsUpdateManifestUrl;
    public const string DefaultDeepSeekSystemPrompt = """
你是 Image2 Studio 的专业图像生成提示词扩写助手。
目标：把用户的简短想法扩写成可直接用于高质量图像生成的完整提示词。
要求：
1. 仅输出扩写后的提示词，不要解释、不要标题、不要编号、不要 Markdown。
2. 保留用户的核心主体、动作、风格、用途和限制，不擅自更换主题。
3. 补足画面主体、环境、构图、镜头、材质、光线、色彩、氛围、细节层次、质量描述。
4. 如果用户提示词很短，扩写成一段完整中文提示词；如果用户已有英文/中英混合表达，保持其主要语言风格。
5. 不添加与图像无关的对话、免责声明、操作步骤或负面提示词，除非用户已经明确要求。
6. 如果用户意图是参考图改图，要强调保留参考图主体结构、身份、构图或产品特征，并自然补充需要变化的部分。
""";
    private const string LegacyDefaultPrompt = "创造一张高质量产品海报：未来感玻璃香水瓶放在浅色石材台面上，背景有柔和自然光和淡蓝色丝带，商业摄影，细节清晰。";

    public string ApiKey { get; set; } = DefaultApiKey;
    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string Model { get; set; } = DefaultModel;
    public string Group { get; set; } = DefaultGroup;
    public string Prompt { get; set; } = DefaultPrompt;
    public string Size { get; set; } = "1024x1024";
    public string Quality { get; set; } = "auto";
    public string ResponseFormat { get; set; } = "url";
    public string OutputFormat { get; set; } = "png";
    public string Background { get; set; } = "auto";
    public bool Transparent { get; set; }
    public bool StreamChat { get; set; } = true;
    public bool UseAsyncTask { get; set; }
    public int SaveLocationIndex { get; set; }
    public string OutputRootPath { get; set; } = string.Empty;
    public string AdditionalConnectionsText { get; set; } = string.Empty;
    public string DeepSeekApiKey { get; set; } = string.Empty;
    public string DeepSeekBaseUrl { get; set; } = DefaultDeepSeekBaseUrl;
    public string DeepSeekModel { get; set; } = DefaultDeepSeekModel;
    public string DeepSeekSystemPrompt { get; set; } = DefaultDeepSeekSystemPrompt;
    public bool AutoCheckUpdates { get; set; } = true;
    public string UpdateManifestUrl { get; set; } = DefaultUpdateManifestUrl;

    public static Image2Settings Load()
    {
        return new Image2Settings
        {
            ApiKey = Preferences.Default.Get("image2_api_key", DefaultApiKey),
            BaseUrl = NormalizeBaseUrl(Preferences.Default.Get("image2_base_url", DefaultBaseUrl)),
            Model = Preferences.Default.Get("image2_model", DefaultModel),
            Group = Preferences.Default.Get("image2_group", DefaultGroup),
            Prompt = NormalizePrompt(Preferences.Default.Get("image2_prompt", DefaultPrompt)),
            Size = Preferences.Default.Get("image2_size", "1024x1024"),
            Quality = Preferences.Default.Get("image2_quality", "auto"),
            ResponseFormat = Preferences.Default.Get("image2_response_format", "url"),
            OutputFormat = Preferences.Default.Get("image2_output_format", "png"),
            Background = Preferences.Default.Get("image2_background", "auto"),
            Transparent = Preferences.Default.Get("image2_transparent", false),
            StreamChat = Preferences.Default.Get("image2_stream_chat", true),
            UseAsyncTask = Preferences.Default.Get("image2_async_task", false),
            SaveLocationIndex = Preferences.Default.Get("image2_save_location", 0),
            OutputRootPath = Preferences.Default.Get("image2_output_root", string.Empty),
            AdditionalConnectionsText = Preferences.Default.Get("image2_additional_connections", string.Empty),
            DeepSeekApiKey = Preferences.Default.Get("deepseek_api_key", string.Empty),
            DeepSeekBaseUrl = Preferences.Default.Get("deepseek_base_url", DefaultDeepSeekBaseUrl),
            DeepSeekModel = Preferences.Default.Get("deepseek_model", DefaultDeepSeekModel),
            DeepSeekSystemPrompt = Preferences.Default.Get("deepseek_system_prompt", DefaultDeepSeekSystemPrompt),
            AutoCheckUpdates = Preferences.Default.Get("auto_check_updates", true),
            UpdateManifestUrl = NormalizeUpdateManifestUrl(Preferences.Default.Get("update_manifest_url", GetDefaultUpdateManifestUrl()))
        };
    }

    public void Save()
    {
        Preferences.Default.Set("image2_api_key", ApiKey ?? string.Empty);
        Preferences.Default.Set("image2_base_url", NormalizeBaseUrl(BaseUrl));
        Preferences.Default.Set("image2_model", string.IsNullOrWhiteSpace(Model) ? DefaultModel : Model.Trim());
        Preferences.Default.Set("image2_group", Group ?? string.Empty);
        Preferences.Default.Set("image2_prompt", Prompt ?? string.Empty);
        Preferences.Default.Set("image2_size", string.IsNullOrWhiteSpace(Size) ? "1024x1024" : Size);
        Preferences.Default.Set("image2_quality", string.IsNullOrWhiteSpace(Quality) ? "auto" : Quality);
        Preferences.Default.Set("image2_response_format", string.IsNullOrWhiteSpace(ResponseFormat) ? "url" : ResponseFormat);
        Preferences.Default.Set("image2_output_format", string.IsNullOrWhiteSpace(OutputFormat) ? "png" : OutputFormat);
        Preferences.Default.Set("image2_background", string.IsNullOrWhiteSpace(Background) ? "auto" : Background);
        Preferences.Default.Set("image2_transparent", Transparent);
        Preferences.Default.Set("image2_stream_chat", StreamChat);
        Preferences.Default.Set("image2_async_task", UseAsyncTask);
        Preferences.Default.Set("image2_save_location", SaveLocationIndex);
        Preferences.Default.Set("image2_output_root", OutputRootPath ?? string.Empty);
        Preferences.Default.Set("image2_additional_connections", AdditionalConnectionsText ?? string.Empty);
        Preferences.Default.Set("deepseek_api_key", DeepSeekApiKey ?? string.Empty);
        Preferences.Default.Set("deepseek_base_url", string.IsNullOrWhiteSpace(DeepSeekBaseUrl) ? DefaultDeepSeekBaseUrl : DeepSeekBaseUrl.Trim().TrimEnd('/'));
        Preferences.Default.Set("deepseek_model", string.IsNullOrWhiteSpace(DeepSeekModel) ? DefaultDeepSeekModel : DeepSeekModel.Trim());
        Preferences.Default.Set("deepseek_system_prompt", string.IsNullOrWhiteSpace(DeepSeekSystemPrompt) ? DefaultDeepSeekSystemPrompt : DeepSeekSystemPrompt.Trim());
        Preferences.Default.Set("auto_check_updates", AutoCheckUpdates);
        Preferences.Default.Set("update_manifest_url", NormalizeUpdateManifestUrl(UpdateManifestUrl));
    }

    public IReadOnlyList<Image2ConnectionProfile> GetConnectionProfiles()
    {
        var profiles = new List<Image2ConnectionProfile>
        {
            new(
                "primary",
                "默认接口",
                ApiKey?.Trim() ?? string.Empty,
                NormalizeBaseUrl(BaseUrl),
                string.IsNullOrWhiteSpace(Model) ? DefaultModel : Model.Trim(),
                string.IsNullOrWhiteSpace(Group) ? DefaultGroup : Group.Trim())
        };

        var index = 1;
        foreach (var rawLine in (AdditionalConnectionsText ?? string.Empty)
                     .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = rawLine.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            var title = $"接口 {index + 1}";
            var apiKey = parts[0];
            if (parts.Length >= 2)
            {
                title = string.IsNullOrWhiteSpace(parts[0]) ? title : parts[0];
                apiKey = parts[1];
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                continue;
            }

            profiles.Add(new Image2ConnectionProfile(
                $"extra-{index}",
                title,
                apiKey.Trim(),
                NormalizeBaseUrl(BaseUrl),
                string.IsNullOrWhiteSpace(Model) ? DefaultModel : Model.Trim(),
                string.IsNullOrWhiteSpace(Group) ? DefaultGroup : Group.Trim()));
            index++;
        }

        return profiles;
    }

    public static string GetDefaultOutputRootPath()
    {
        return Path.Combine(FileSystem.Current.AppDataDirectory, "Image2 Studio Library");
    }

    public string GetResolvedOutputRootPath()
    {
        return string.IsNullOrWhiteSpace(OutputRootPath)
            ? GetDefaultOutputRootPath()
            : OutputRootPath.Trim();
    }

    public static void SavePrompt(string prompt)
    {
        Preferences.Default.Set("image2_prompt", prompt ?? string.Empty);
    }

    public static void SaveQuickImageDefaults(string size, string quality)
    {
        Preferences.Default.Set("image2_size", string.IsNullOrWhiteSpace(size) ? "1024x1024" : size);
        Preferences.Default.Set("image2_quality", string.IsNullOrWhiteSpace(quality) ? "auto" : quality);
    }

    public static string NormalizeBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultBaseUrl;
        }

        var input = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            return input;
        }

        var path = uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        return path is "/v1/images/generations" or "/v1/images/edits" or "/v1/chat/completions" or "/v1/image-tasks/generations" or "/v1/image-tasks/edits"
            ? $"{uri.Scheme}://{uri.Authority}"
            : input;
    }

    public static string GetDefaultUpdateManifestUrl()
    {
        return DeviceInfo.Platform == DevicePlatform.Android
            ? DefaultAndroidUpdateManifestUrl
            : DefaultWindowsUpdateManifestUrl;
    }

    private static string NormalizeUpdateManifestUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return GetDefaultUpdateManifestUrl();
        }

        var trimmed = value.Trim();
        return string.Equals(trimmed, "https://raw.githubusercontent.com/Mycroftxrg/Image2Studio/main/latest.json", StringComparison.OrdinalIgnoreCase)
            ? GetDefaultUpdateManifestUrl()
            : trimmed;
    }

    private static string NormalizePrompt(string? prompt)
    {
        return string.Equals(prompt, LegacyDefaultPrompt, StringComparison.Ordinal)
            ? string.Empty
            : prompt ?? string.Empty;
    }
}

public sealed record Image2ConnectionProfile(
    string Id,
    string Title,
    string ApiKey,
    string BaseUrl,
    string Model,
    string Group);
