namespace Image2Studio;

public partial class SettingsPage : ContentPage
{
    private readonly Services.AppUpdateService _updateService = new();
    private bool _isLoading;
    private readonly List<ConnectionCardState> _connectionCards = new();

    public SettingsPage()
    {
        InitializeComponent();
        LoadSettings();
        SizeChanged += OnPageSizeChanged;
    }

    private void LoadSettings()
    {
        _isLoading = true;
        var settings = Image2Settings.Load();

        ApiKeyEntry.Text = settings.ApiKey;
        BaseUrlEntry.Text = Image2Settings.NormalizeBaseUrl(settings.BaseUrl);
        SetPickerValue(ModelPicker, settings.Model);
        GroupEntry.Text = settings.Group;
        SetPickerValue(ResponseFormatPicker, settings.ResponseFormat);
        SetPickerValue(FormatPicker, settings.OutputFormat);
        SetPickerValue(BackgroundPicker, settings.Background);
        SaveLocationPicker.SelectedIndex = settings.SaveLocationIndex <= 0 ? 0 : 1;
        TransparentCheckBox.IsChecked = settings.Transparent;
        StreamChatCheckBox.IsChecked = settings.StreamChat;
        AsyncTaskCheckBox.IsChecked = settings.UseAsyncTask;
        AutoCheckUpdatesCheckBox.IsChecked = settings.AutoCheckUpdates;
        UpdateManifestUrlEntry.Text = settings.UpdateManifestUrl;
        UpdateStatusLabel.Text = $"当前版本 {Services.AppUpdateService.CurrentVersionText}";
        DeepSeekApiKeyEntry.Text = settings.DeepSeekApiKey;
        DeepSeekBaseUrlEntry.Text = settings.DeepSeekBaseUrl;
        SetPickerValue(DeepSeekModelPicker, settings.DeepSeekModel);
        DeepSeekSystemPromptEditor.Text = string.IsNullOrWhiteSpace(settings.DeepSeekSystemPrompt)
            ? Image2Settings.DefaultDeepSeekSystemPrompt
            : settings.DeepSeekSystemPrompt;
        ConfigureLongEditors();
        _connectionCards.Clear();
        _connectionCards.AddRange(ParseConnectionCards(settings.AdditionalConnectionsText));
        RenderConnectionCards();
        OutputRootEntry.Text = settings.OutputRootPath;
        OutputRootLabel.Text = string.IsNullOrWhiteSpace(settings.OutputRootPath)
            ? Image2Settings.GetDefaultOutputRootPath()
            : settings.OutputRootPath;
        StatusLabel.Text = "设置已载入。";
        _isLoading = false;
    }

    private void ConfigureLongEditors()
    {
        if (DeviceInfo.Platform == DevicePlatform.Android)
        {
            DeepSeekSystemPromptEditor.AutoSize = EditorAutoSizeOption.TextChanges;
            DeepSeekSystemPromptEditor.HeightRequest = -1;
            return;
        }

        DeepSeekSystemPromptEditor.AutoSize = EditorAutoSizeOption.Disabled;
        DeepSeekSystemPromptEditor.HeightRequest = 220;
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        SaveSettings();
        await Shell.Current.GoToAsync("..");
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        SaveSettings();
        StatusLabel.Text = $"已保存 {DateTime.Now:HH:mm}";
    }

    private async void OnResetDefaultsClicked(object? sender, EventArgs e)
    {
        var confirmed = await DisplayAlertAsync(
            "恢复默认",
            "会重置接入和高级默认值，但不会清除主页提示词。",
            "恢复",
            "取消");
        if (!confirmed)
        {
            return;
        }

        var current = Image2Settings.Load();
        var defaults = new Image2Settings
        {
            Prompt = current.Prompt,
            OutputRootPath = current.OutputRootPath
        };
        defaults.DeepSeekApiKey = current.DeepSeekApiKey;
        defaults.DeepSeekBaseUrl = current.DeepSeekBaseUrl;
        defaults.DeepSeekModel = current.DeepSeekModel;
        defaults.DeepSeekSystemPrompt = current.DeepSeekSystemPrompt;
        defaults.Save();
        LoadSettings();
        StatusLabel.Text = "已恢复默认设置。";
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        SaveSettings();
        StatusLabel.Text = "设置已自动保存。";
    }

    private void OnSettingsChanged(object? sender, CheckedChangedEventArgs e) => OnSettingsChanged(sender, EventArgs.Empty);

    private void SaveSettings()
    {
        var current = Image2Settings.Load();
        var settings = new Image2Settings
        {
            ApiKey = ApiKeyEntry.Text?.Trim() ?? string.Empty,
            BaseUrl = Image2Settings.NormalizeBaseUrl(BaseUrlEntry.Text),
            Model = SelectedPickerValue(ModelPicker, Image2Settings.DefaultModel),
            Group = string.IsNullOrWhiteSpace(GroupEntry.Text) ? Image2Settings.DefaultGroup : GroupEntry.Text.Trim(),
            Prompt = current.Prompt,
            Size = current.Size,
            Quality = current.Quality,
            ResponseFormat = SelectedPickerValue(ResponseFormatPicker, "url"),
            OutputFormat = SelectedPickerValue(FormatPicker, "png"),
            Background = SelectedPickerValue(BackgroundPicker, "auto"),
            Transparent = TransparentCheckBox.IsChecked,
            StreamChat = StreamChatCheckBox.IsChecked,
            UseAsyncTask = AsyncTaskCheckBox.IsChecked,
            AutoCheckUpdates = AutoCheckUpdatesCheckBox.IsChecked,
            UpdateManifestUrl = string.IsNullOrWhiteSpace(UpdateManifestUrlEntry.Text)
                ? Image2Settings.DefaultUpdateManifestUrl
                : UpdateManifestUrlEntry.Text.Trim(),
            SaveLocationIndex = SaveLocationPicker.SelectedIndex <= 0 ? 0 : 1,
            AdditionalConnectionsText = SerializeConnectionCards(),
            OutputRootPath = OutputRootEntry.Text?.Trim() ?? string.Empty,
            DeepSeekApiKey = DeepSeekApiKeyEntry.Text?.Trim() ?? string.Empty,
            DeepSeekBaseUrl = string.IsNullOrWhiteSpace(DeepSeekBaseUrlEntry.Text)
                ? Image2Settings.DefaultDeepSeekBaseUrl
                : DeepSeekBaseUrlEntry.Text.Trim(),
            DeepSeekModel = SelectedPickerValue(DeepSeekModelPicker, Image2Settings.DefaultDeepSeekModel),
            DeepSeekSystemPrompt = string.IsNullOrWhiteSpace(DeepSeekSystemPromptEditor.Text)
                ? Image2Settings.DefaultDeepSeekSystemPrompt
                : DeepSeekSystemPromptEditor.Text.Trim()
        };

        settings.Save();
        OutputRootLabel.Text = settings.GetResolvedOutputRootPath();
    }

    private async void OnCheckUpdatesClicked(object? sender, EventArgs e)
    {
        SaveSettings();

        if (!OperatingSystem.IsWindows())
        {
            await DisplayAlertAsync("检查更新", "当前更新功能只处理 Windows 安装包。", "知道了");
            return;
        }

        await CheckUpdatesAsync(showUpToDateMessage: true);
    }

    private async Task CheckUpdatesAsync(bool showUpToDateMessage)
    {
        try
        {
            UpdateStatusLabel.Text = "正在检查更新...";
            var update = await _updateService.CheckAsync(Image2Settings.Load());
            if (!update.IsUpdateAvailable)
            {
                UpdateStatusLabel.Text = $"当前已是最新版本 {update.CurrentVersion}";
                if (showUpToDateMessage)
                {
                    await DisplayAlertAsync("检查更新", $"当前已是最新版本 {update.CurrentVersion}。", "知道了");
                }

                return;
            }

            UpdateStatusLabel.Text = $"发现新版本 {update.LatestVersion}";
            var notes = string.IsNullOrWhiteSpace(update.Notes) ? string.Empty : $"{Environment.NewLine}{Environment.NewLine}{update.Notes}";
            var confirmed = await DisplayAlertAsync(
                "发现新版本",
                $"当前版本：{update.CurrentVersion}{Environment.NewLine}最新版本：{update.LatestVersion}{notes}",
                "下载并安装",
                "稍后");

            if (!confirmed)
            {
                return;
            }

            var progress = new Progress<double>(value =>
            {
                UpdateStatusLabel.Text = $"正在下载更新 {Math.Clamp(value, 0, 1):P0}";
            });
            var installerPath = await _updateService.DownloadInstallerAsync(update, progress);
            UpdateStatusLabel.Text = "更新已下载，正在启动安装程序...";
            _updateService.LaunchInstaller(installerPath);
        }
        catch (Exception ex)
        {
            UpdateStatusLabel.Text = "检查更新失败。";
            await DisplayAlertAsync("检查更新失败", ex.Message, "知道了");
        }
    }

    private void OnAddConnectionCardClicked(object? sender, EventArgs e)
    {
        _connectionCards.Add(new ConnectionCardState($"接口 {_connectionCards.Count + 2}", string.Empty));
        RenderConnectionCards();
        SaveSettings();
        StatusLabel.Text = "已添加接口卡片。";
    }

    private void RenderConnectionCards()
    {
        ConnectionCardsStack.Children.Clear();
        if (_connectionCards.Count == 0)
        {
            ConnectionCardsStack.Children.Add(CreateCaptionLabel("未添加额外 API Key。"));
            return;
        }

        for (var index = 0; index < _connectionCards.Count; index++)
        {
            ConnectionCardsStack.Children.Add(CreateConnectionCard(index, _connectionCards[index]));
        }
    }

    private View CreateConnectionCard(int index, ConnectionCardState card)
    {
        var titleEntry = new Entry
        {
            Text = card.Title,
            Placeholder = "接口标题",
            BindingContext = index
        };
        titleEntry.TextChanged += OnConnectionTitleChanged;

        var keyEntry = new Entry
        {
            Text = card.ApiKey,
            Placeholder = "API Key",
            IsPassword = true,
            BindingContext = index
        };
        keyEntry.TextChanged += OnConnectionKeyChanged;

        var removeButton = new Button
        {
            Text = "删除",
            Padding = new Thickness(12, 8),
            BackgroundColor = ResourceColor("ControlBg"),
            TextColor = ResourceColor("TextStrong"),
            BindingContext = index
        };
        removeButton.Pressed += OnButtonPressed;
        removeButton.Released += OnButtonReleased;
        removeButton.Clicked += OnRemoveConnectionCardClicked;

        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 10
        };
        header.Add(new Label
        {
            Text = $"API {index + 2}",
            FontFamily = "OpenSansSemibold",
            FontSize = 14,
            TextColor = ResourceColor("TextStrong"),
            VerticalTextAlignment = TextAlignment.Center
        }, 0, 0);
        header.Add(removeButton, 1, 0);

        var stack = new VerticalStackLayout { Spacing = 9 };
        stack.Children.Add(header);
        stack.Children.Add(new Border
        {
            Padding = new Thickness(12, 0),
            BackgroundColor = ResourceColor("PanelBg"),
            Content = titleEntry
        });
        stack.Children.Add(new Border
        {
            Padding = new Thickness(12, 0),
            BackgroundColor = ResourceColor("PanelBg"),
            Content = keyEntry
        });
        stack.Children.Add(CreateCaptionLabel("复用默认接口的 Base URL、模型和 Group。"));

        return new Border
        {
            Padding = 12,
            BackgroundColor = ResourceColor("PanelAlt"),
            Content = stack
        };
    }

    private void OnConnectionTitleChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isLoading || sender is not Entry { BindingContext: int index } || index < 0 || index >= _connectionCards.Count)
        {
            return;
        }

        _connectionCards[index] = _connectionCards[index] with { Title = e.NewTextValue ?? string.Empty };
        SaveSettings();
        StatusLabel.Text = "接口卡片已保存。";
    }

    private void OnConnectionKeyChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isLoading || sender is not Entry { BindingContext: int index } || index < 0 || index >= _connectionCards.Count)
        {
            return;
        }

        _connectionCards[index] = _connectionCards[index] with { ApiKey = e.NewTextValue ?? string.Empty };
        SaveSettings();
        StatusLabel.Text = "接口卡片已保存。";
    }

    private void OnRemoveConnectionCardClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: int index } || index < 0 || index >= _connectionCards.Count)
        {
            return;
        }

        _connectionCards.RemoveAt(index);
        RenderConnectionCards();
        SaveSettings();
        StatusLabel.Text = "已删除接口卡片。";
    }

    private string SerializeConnectionCards()
    {
        return string.Join(
            Environment.NewLine,
            _connectionCards
                .Select((card, index) =>
                {
                    var title = string.IsNullOrWhiteSpace(card.Title) ? $"接口 {index + 2}" : card.Title.Trim();
                    var apiKey = card.ApiKey.Trim();
                    return string.IsNullOrWhiteSpace(apiKey) ? string.Empty : $"{title} | {apiKey}";
                })
                .Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static IReadOnlyList<ConnectionCardState> ParseConnectionCards(string? text)
    {
        var cards = new List<ConnectionCardState>();
        var index = 0;
        foreach (var rawLine in (text ?? string.Empty)
                     .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = rawLine.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            var title = $"接口 {index + 2}";
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

            cards.Add(new ConnectionCardState(title, apiKey.Trim()));
            index++;
        }

        return cards;
    }

    private async void OnPickOutputFolderClicked(object? sender, EventArgs e)
    {
        var folder = await Image2Studio.Services.UserFileSaver.PickFolderAsync("选择 Image2 Studio 自动保存文件夹");
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        OutputRootEntry.Text = folder;
        SaveSettings();
        var imported = await new Image2Studio.Services.Image2TaskFileService().ImportLibraryAsync(Image2Settings.Load());
        var total = imported.Queue.Count + imported.History.Count;
        StatusLabel.Text = total == 0
            ? "自动保存文件夹已更新，未发现可导入任务。"
            : $"自动保存文件夹已更新，发现队列 {imported.Queue.Count} 个、历史 {imported.History.Count} 条，返回主页后自动合并。";
    }

    private async void OnButtonPressed(object? sender, EventArgs e)
    {
        if (sender is VisualElement element && element.IsEnabled)
        {
            await element.ScaleToAsync(0.965, 96, Easing.SinOut);
        }
    }

    private async void OnButtonReleased(object? sender, EventArgs e)
    {
        if (sender is VisualElement element)
        {
            await element.ScaleToAsync(1, 190, Easing.SpringOut);
        }
    }

    private void OnPageSizeChanged(object? sender, EventArgs e)
    {
        if (Width <= 0)
        {
            return;
        }

        if (DeviceInfo.Platform == DevicePlatform.Android)
        {
            SettingsRoot.Padding = new Thickness(14, 16, 14, 24);
            return;
        }

        SettingsRoot.Padding = Width > 920
            ? new Thickness(28, 22)
            : new Thickness(18);
    }

    private static string SelectedPickerValue(Picker picker, string fallback)
    {
        return picker.SelectedItem?.ToString() ?? fallback;
    }

    private static void SetPickerValue(Picker picker, string value)
    {
        var index = picker.Items.IndexOf(value);
        picker.SelectedIndex = index >= 0 ? index : 0;
    }

    private static Label CreateCaptionLabel(string text)
    {
        var label = new Label
        {
            Text = text,
            FontSize = 12,
            TextColor = ResourceColor("TextMuted")
        };

        if (Application.Current?.Resources.TryGetValue("Caption", out var value) == true && value is Style style)
        {
            label.Style = style;
        }

        return label;
    }

    private static Color ResourceColor(string key)
    {
        var themeKey = Application.Current?.RequestedTheme == AppTheme.Dark ? $"{key}Dark" : key;
        if (Application.Current?.Resources.TryGetValue(themeKey, out var themeValue) == true && themeValue is Color themeColor)
        {
            return themeColor;
        }

        if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color)
        {
            return color;
        }

        return Colors.Transparent;
    }

    private sealed record ConnectionCardState(string Title, string ApiKey);
}
