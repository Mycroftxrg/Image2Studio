using Image2Studio.Services;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;

namespace Image2Studio;

public partial class MainPage : ContentPage
{
    private readonly Image2ApiClient _client = new();
    private readonly DeepSeekPromptExpander _promptExpander = new();
    private readonly Image2TaskStore _taskStore = new();
    private readonly Image2TaskFileService _fileService = new();
    private readonly Image2QueueState _state = new();
    private readonly Dictionary<string, CancellationTokenSource> _runningTasks = new();
    private readonly HashSet<string> _busyConnections = new();
    private readonly List<ReferenceImageItem> _referenceImages = new();
    private readonly List<Image2ConnectionProfile> _connections = new();
    private readonly Dictionary<string, List<WeakReference<ScrollView>>> _historyDetailViews = new();

    private Image2Result? _lastResult;
    private Image2QueuedTask? _selectedTask;
    private Image2QueuedTask? _lastRenderedTask;
    private string? _homeDetailTaskId;
    private string? _homeDetailHistoryId;
    private ScrollView? _queueDetailsView;
    private ScrollView? _historyDetailsView;
    private Image2Settings _settings = Image2Settings.Load();
    private WorkMode _mode = WorkMode.Chat;
    private bool _isLoadingSettings;
    private bool _isLoadingTask;
    private bool _queueRunning;
    private bool _stateLoaded;
    private bool _isRefreshingQueueState;
    private IDispatcherTimer? _connectionRefreshTimer;
    private bool _isDispatchingQueue;

    public MainPage()
    {
        InitializeComponent();
        SizeChanged += OnPageSizeChanged;
        Loaded += OnPageLoaded;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        StartConnectionStatusRefresh();
        if (!_stateLoaded)
        {
            await LoadStateAsync();
            return;
        }

        RefreshSettingsOnly();
    }

    private enum WorkMode
    {
        Generate,
        Edit,
        Chat,
        DirectEdit
    }

    private sealed record ReferenceImageItem(
        FileResult File,
        byte[] PreviewBytes,
        Image2ReferenceAsset? Asset);

    private sealed record ResultImageSaveRequest(
        string? ImagePath,
        string? ImageUrl,
        string Title,
        string OutputFormat);

    private sealed record HistoryFolderCreateRequest(string HistoryId);

    private async Task LoadStateAsync()
    {
        _settings = Image2Settings.Load();
        if (DeviceInfo.Platform == DevicePlatform.Android && string.IsNullOrWhiteSpace(_settings.OutputRootPath))
        {
            await EnsureAndroidOutputFolderAsync();
            _settings = Image2Settings.Load();
        }

        RefreshConnections();

        var loaded = await _taskStore.LoadAsync();
        _state.Queue.Clear();
        _state.Queue.AddRange(loaded.Queue.Select(NormalizeLoadedTask));
        _state.History.Clear();
        _state.History.AddRange(loaded.History.Select(NormalizeLoadedHistory));
        await SafeImportHistoryFromOutputFolderAsync();
        await MigrateTerminalQueueItemsToHistoryAsync();

        ApplySettingsToHome();
        RenderQueue();
        RenderHistory();
        UpdateQueueSummary();
        DispatchQueue();
        _stateLoaded = true;
    }

    private void RefreshSettingsOnly()
    {
        _settings = Image2Settings.Load();
        RefreshConnections();
        ApplySettingsToHome();
        RefreshConnectionStatus();
        RenderQueue();
        RenderHistory();
        UpdateQueueSummary();
    }

    private async Task EnsureAndroidOutputFolderAsync()
    {
        while (string.IsNullOrWhiteSpace(Image2Settings.Load().OutputRootPath))
        {
            await DisplayAlertAsync(
                "选择保存文件夹",
                "首次使用需要指定任务数据和生图结果的保存文件夹。",
                "选择");

            var folder = await UserFileSaver.PickFolderAsync("选择 Image2 Studio 保存文件夹");
            if (string.IsNullOrWhiteSpace(folder))
            {
                SetStatus("需要先指定保存文件夹");
                continue;
            }

            var settings = Image2Settings.Load();
            settings.OutputRootPath = folder;
            settings.Save();
        }
    }

    private static Image2QueuedTask NormalizeLoadedTask(Image2QueuedTask task)
    {
        if (string.IsNullOrWhiteSpace(task.VersionGroupId))
        {
            task.VersionGroupId = task.Id;
        }

        if (task.Status == Image2TaskStatus.Running)
        {
            task.Status = Image2TaskStatus.Stopped;
            task.StatusDetail = "上次关闭时仍在运行，已暂停等待重试";
        }

        return task;
    }

    private static Image2HistoryEntry NormalizeLoadedHistory(Image2HistoryEntry history)
    {
        if (string.IsNullOrWhiteSpace(history.SourceTaskId))
        {
            history.SourceTaskId = history.Id;
        }

        if (history.Version <= 0)
        {
            history.Version = 1;
        }

        if (string.IsNullOrWhiteSpace(history.StatusText))
        {
            history.StatusText = history.Succeeded ? "生图成功" : "生图失败";
        }

        return history;
    }

    private async Task SafeImportHistoryFromOutputFolderAsync()
    {
        try
        {
            await ImportHistoryFromOutputFolderAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"读取保存文件夹失败：{ex.Message}");
        }
    }

    private async Task ImportHistoryFromOutputFolderAsync()
    {
        var imported = await _fileService.ImportLibraryAsync(_settings);
        if (imported.Queue.Count == 0 && imported.History.Count == 0)
        {
            return;
        }

        var added = 0;
        foreach (var task in imported.Queue.Select(NormalizeLoadedTask))
        {
            if (_state.Queue.Any(existing =>
                    existing.Id == task.Id ||
                    !string.IsNullOrWhiteSpace(existing.OutputFolder) &&
                    string.Equals(existing.OutputFolder, task.OutputFolder, StringComparison.Ordinal)))
            {
                continue;
            }

            _state.Queue.Add(task);
            added++;
        }

        foreach (var history in imported.History.Select(NormalizeLoadedHistory))
        {
            if (_state.History.Any(existing => IsSameHistoryVersion(existing, history)))
            {
                continue;
            }

            _state.History.Add(history);
            added++;
        }

        if (added == 0)
        {
            return;
        }

        _state.History.Sort((left, right) => right.CreatedAt.CompareTo(left.CreatedAt));
        await _taskStore.SaveAsync(_state);
        SetStatus($"已从保存文件夹导入 {added} 条任务/历史");
    }

    private static bool IsSameHistoryVersion(Image2HistoryEntry left, Image2HistoryEntry right)
    {
        return GetHistoryGroupId(left) == GetHistoryGroupId(right) &&
               left.Version == right.Version &&
               (string.Equals(left.OutputFolder, right.OutputFolder, StringComparison.Ordinal) ||
                string.Equals(left.Prompt, right.Prompt, StringComparison.Ordinal) &&
                string.Equals(left.RawJson, right.RawJson, StringComparison.Ordinal));
    }

    private void RefreshConnections()
    {
        _connections.Clear();
        _connections.AddRange(_settings.GetConnectionProfiles());
        ConnectionPicker.Items.Clear();
        foreach (var connection in _connections)
        {
            ConnectionPicker.Items.Add(connection.Title);
        }

        if (ConnectionPicker.SelectedIndex < 0 && ConnectionPicker.Items.Count > 0)
        {
            ConnectionPicker.SelectedIndex = 0;
        }

        ConnectionSummaryLabel.Text = _connections.Count == 0
            ? "接口：未配置"
            : $"接口：{_connections.Count} 个 / 空闲 {_connections.Count - _busyConnections.Count}";
    }

    private void ApplySettingsToHome()
    {
        _isLoadingSettings = true;
        PromptEditor.Text = _settings.Prompt;
        TaskTitleEntry.Text = "";
        AssignmentModePicker.SelectedIndex = 0;
        SetPickerValue(SizePicker, _settings.Size);
        SetPickerValue(QualityPicker, _settings.Quality);
        _isLoadingSettings = false;
        SetMode(WorkMode.Chat);
        UpdateSettingsSummary();
        UpdatePlatformChrome();
        UpdateReferenceSummary();
        SetStatus("准备就绪：创建任务会加入队列，完成后自动归档到历史。");
    }

    private void UpdateSettingsSummary()
    {
        var modeText = _settings.StreamChat ? "流式聊天" : "非流式聊天";
        SettingsSummaryLabel.Text = $"{_settings.Model} / {_connections.Count} 个接口 / {modeText}";
        UpdateStatusPanel();
    }

    private async void OnOpenSettingsClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync(nameof(SettingsPage));
    }

    private async void OnOpenCreateTaskClicked(object? sender, EventArgs e)
    {
        ResetEditorForNewTask();
        await Navigation.PushAsync(CreateTaskEditorPage(null));
    }

    private void ResetEditorForNewTask()
    {
        _isLoadingTask = true;
        _selectedTask = null;
        TaskTitleEntry.Text = string.Empty;
        PromptEditor.Text = _settings.Prompt;
        AssignmentModePicker.SelectedIndex = 0;
        SetConnectionPicker(null);
        SetPickerValue(SizePicker, _settings.Size);
        SetPickerValue(QualityPicker, _settings.Quality);
        ReferenceUrlEditor.Text = string.Empty;
        _referenceImages.Clear();
        _isLoadingTask = false;
        SetMode(WorkMode.Chat);
        UpdateReferenceSummary();
    }

    private void OnModeGenerateClicked(object? sender, EventArgs e) => SetMode(WorkMode.Generate);

    private void OnModeEditClicked(object? sender, EventArgs e) => SetMode(WorkMode.Edit);

    private void OnModeChatClicked(object? sender, EventArgs e) => SetMode(WorkMode.Chat);

    private void SetMode(WorkMode mode)
    {
        _mode = mode;
        ReferencePanel.IsVisible = mode == WorkMode.Edit;
        SetModeButtonState(ModeGenerateButton, mode == WorkMode.Generate);
        SetModeButtonState(ModeEditButton, mode == WorkMode.Edit);
        SetModeButtonState(ModeChatButton, mode == WorkMode.Chat);
    }

    private static void SetModeButtonState(Button button, bool selected)
    {
        button.BackgroundColor = ResourceColor(selected ? "Primary" : "ControlBg");
        button.TextColor = ResourceColor(selected ? "White" : "TextStrong");
    }

    private void OnPromptChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isLoadingSettings || _isLoadingTask)
        {
            return;
        }

        Image2Settings.SavePrompt(PromptEditor.Text ?? string.Empty);
    }

    private void OnQuickSettingChanged(object? sender, EventArgs e)
    {
        if (_isLoadingSettings || _isLoadingTask)
        {
            return;
        }

        Image2Settings.SaveQuickImageDefaults(
            SelectedPickerValue(SizePicker, _settings.Size),
            SelectedPickerValue(QualityPicker, _settings.Quality));
        _settings = Image2Settings.Load();
        UpdateSettingsSummary();
    }

    private void OnCurrentTaskChanged(object? sender, EventArgs e)
    {
        if (_isLoadingSettings || _isLoadingTask)
        {
            return;
        }
    }

    private void OnAssignmentModeChanged(object? sender, EventArgs e)
    {
        var manual = AssignmentModePicker.SelectedIndex == 1;
        ConnectionPicker.IsEnabled = manual;
        OnCurrentTaskChanged(sender, e);
    }

    private async void OnPickReferenceClicked(object? sender, EventArgs e)
    {
        try
        {
            var results = await FilePicker.Default.PickMultipleAsync(new PickOptions
            {
                PickerTitle = "选择参考图片",
                FileTypes = FilePickerFileType.Images
            });

            var selected = (results ?? Enumerable.Empty<FileResult?>())
                .Where(result => result is not null)
                .Cast<FileResult>()
                .ToArray();
            if (selected.Length == 0)
            {
                return;
            }

            var items = await Task.WhenAll(selected.Select(file => CreateReferenceImageItemAsync(file, null)));
            _referenceImages.AddRange(items);
            UpdateReferenceSummary();
            SetMode(WorkMode.Edit);
            SetStatus($"已添加 {selected.Length} 张参考图");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("选择失败", ex.Message, "知道了");
        }
    }

    private static async Task<ReferenceImageItem> CreateReferenceImageItemAsync(FileResult file, Image2ReferenceAsset? asset)
    {
        await using var input = await file.OpenReadAsync();
        using var memory = new MemoryStream();
        var buffer = new byte[64 * 1024];
        const long previewByteLimit = 12 * 1024 * 1024;
        int read;
        while (memory.Length < previewByteLimit && (read = await input.ReadAsync(buffer)) > 0)
        {
            var remaining = (int)Math.Min(read, previewByteLimit - memory.Length);
            await memory.WriteAsync(buffer.AsMemory(0, remaining));
        }

        return new ReferenceImageItem(file, memory.ToArray(), asset);
    }

    private void OnClearReferenceClicked(object? sender, EventArgs e)
    {
        _referenceImages.Clear();
        ReferenceUrlEditor.Text = "";
        UpdateReferenceSummary();
        SetStatus("已清除参考图");
    }

    private void OnReferenceUrlsChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateReferenceSummary();
    }

    private void OnRemoveReferenceImageClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: int index } || index < 0 || index >= _referenceImages.Count)
        {
            return;
        }

        _referenceImages.RemoveAt(index);
        UpdateReferenceSummary();
    }

    private void OnRemoveReferenceUrlClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: int index })
        {
            return;
        }

        var remaining = ParseReferenceImageUrls(ReferenceUrlEditor?.Text).ToList();
        if (index < 0 || index >= remaining.Count)
        {
            return;
        }

        remaining.RemoveAt(index);
        if (ReferenceUrlEditor is not null)
        {
            ReferenceUrlEditor.Text = string.Join(Environment.NewLine, remaining);
        }
        UpdateReferenceSummary();
    }

    private async void OnAddQueueClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PromptEditor.Text))
        {
            await DisplayAlertAsync("缺少提示词", "请先输入提示词，再加入队列。", "知道了");
            return;
        }

        var task = await CreateTaskFromEditorAsync(Guid.NewGuid().ToString("N"));
        _state.Queue.Add(task);
        await PersistAndRenderAsync($"已加入队列：{task.Title}");
        DispatchQueue();
    }

    private async void OnUpdateSelectedTaskClicked(object? sender, EventArgs e)
    {
        if (_selectedTask is null || !_selectedTask.CanEdit)
        {
            SetStatus("只能更新等待、停止或失败的任务");
            return;
        }

        var index = _state.Queue.FindIndex(task => task.Id == _selectedTask.Id);
        if (index < 0)
        {
            return;
        }

        var updated = await CreateTaskFromEditorAsync(_selectedTask.Id);
        updated.CreatedAt = _selectedTask.CreatedAt;
        updated.VersionGroupId = string.IsNullOrWhiteSpace(_selectedTask.VersionGroupId) ? _selectedTask.Id : _selectedTask.VersionGroupId;
        updated.OutputFolder = _selectedTask.OutputFolder;
        updated.Status = _selectedTask.Status == Image2TaskStatus.Failed ? Image2TaskStatus.Queued : _selectedTask.Status;
        updated.StatusDetail = updated.Status == Image2TaskStatus.Queued ? BuildWaitingStatus(updated) : _selectedTask.StatusDetail;
        _state.Queue[index] = updated;
        _selectedTask = updated;
        await PersistAndRenderAsync($"已更新任务：{updated.Title}");
        DispatchQueue();
    }

    private async Task<Image2QueuedTask> CreateTaskFromEditorAsync(string taskId)
    {
        var title = string.IsNullOrWhiteSpace(TaskTitleEntry.Text)
            ? $"任务 {DateTime.Now:HHmmss}"
            : TaskTitleEntry.Text.Trim();
        var references = new List<Image2ReferenceAsset>();
        foreach (var item in _referenceImages)
        {
            references.Add(item.Asset ?? await _fileService.CopyReferenceAsync(item.File, taskId));
        }

        var referenceUrls = ParseReferenceImageUrls(ReferenceUrlEditor.Text).ToList();
        var hasMultipleReferenceImages = references.Count + referenceUrls.Count > 1;
        var manual = AssignmentModePicker.SelectedIndex == 1;
        var selectedConnection = GetSelectedConnection();

        return new Image2QueuedTask
        {
            Id = taskId,
            VersionGroupId = taskId,
            Title = title,
            Prompt = PromptEditor.Text?.Trim() ?? "",
            Mode = _mode.ToString(),
            Size = SelectedPickerValue(SizePicker, _settings.Size),
            Quality = SelectedPickerValue(QualityPicker, _settings.Quality),
            ResponseFormat = _settings.ResponseFormat,
            OutputFormat = _settings.OutputFormat,
            Background = _settings.Background,
            Transparent = _settings.Transparent,
            UseAsyncTask = _settings.UseAsyncTask || hasMultipleReferenceImages,
            UseChatEndpoint = _mode == WorkMode.Chat || _mode == WorkMode.Edit,
            StreamChat = _settings.StreamChat || _mode == WorkMode.Edit,
            ReferenceImages = references,
            ReferenceImageUrls = referenceUrls,
            AssignmentMode = manual ? Image2ConnectionAssignmentMode.Manual : Image2ConnectionAssignmentMode.Auto,
            RequestedConnectionId = manual ? selectedConnection?.Id : null,
            RequestedConnectionTitle = manual ? selectedConnection?.Title : null,
            Status = Image2TaskStatus.Queued,
            StatusDetail = manual && selectedConnection is not null
                ? $"等待指定接口：{selectedConnection.Title}"
                : "等待调度",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
    }

    private Image2ConnectionProfile? GetSelectedConnection()
    {
        var index = ConnectionPicker.SelectedIndex;
        return index >= 0 && index < _connections.Count ? _connections[index] : _connections.FirstOrDefault();
    }

    private async void OnStartQueueClicked(object? sender, EventArgs e)
    {
        _queueRunning = true;
        SetStatus("队列已开启：会自动分配空闲接口");
        RefreshConnectionStatus();
        DispatchQueue();
        await PersistAndRenderAsync();
    }

    private async void OnStopQueueClicked(object? sender, EventArgs e)
    {
        _queueRunning = false;
        foreach (var task in _state.Queue.Where(task => task.Status == Image2TaskStatus.Queued))
        {
            task.StatusDetail = BuildWaitingStatus(task);
            task.UpdatedAt = DateTime.Now;
        }

        RefreshConnectionStatus();
        await PersistAndRenderAsync("队列已暂停：不再启动新任务，已请求中的任务会继续完成");
    }

    private void DispatchQueue()
    {
        if (_isDispatchingQueue || !_queueRunning || _connections.Count == 0)
        {
            return;
        }

        _isDispatchingQueue = true;
        try
        {
            foreach (var task in _state.Queue.Where(task => task.Status == Image2TaskStatus.Queued).ToArray())
            {
                var connection = PickConnectionForTask(task);
                if (connection is null)
                {
                    task.StatusDetail = BuildWaitingStatus(task);
                    continue;
                }

                if (!TryMarkTaskRunning(task, connection))
                {
                    task.StatusDetail = BuildWaitingStatus(task);
                    continue;
                }

                _ = RunTaskAsync(task, connection);
            }

            RenderQueue();
            UpdateQueueSummary();
        }
        finally
        {
            _isDispatchingQueue = false;
        }
    }

    private Image2ConnectionProfile? PickConnectionForTask(Image2QueuedTask task)
    {
        if (task.AssignmentMode == Image2ConnectionAssignmentMode.Manual)
        {
            var requested = _connections.FirstOrDefault(connection => connection.Id == task.RequestedConnectionId);
            task.RequestedConnectionTitle = requested?.Title ?? task.RequestedConnectionTitle;
            return requested is not null && !_busyConnections.Contains(requested.Id) ? requested : null;
        }

        return _connections.FirstOrDefault(connection => !_busyConnections.Contains(connection.Id));
    }

    private bool TryMarkTaskRunning(Image2QueuedTask task, Image2ConnectionProfile connection)
    {
        if (task.Status != Image2TaskStatus.Queued ||
            _runningTasks.ContainsKey(task.Id) ||
            _busyConnections.Contains(connection.Id))
        {
            return false;
        }

        _busyConnections.Add(connection.Id);
        task.Status = Image2TaskStatus.Running;
        task.AssignedConnectionId = connection.Id;
        task.AssignedConnectionTitle = connection.Title;
        task.StatusDetail = $"正在调用：{connection.Title}";
        task.StartedAt ??= DateTime.Now;
        task.UpdatedAt = DateTime.Now;
        return true;
    }

    private void StartConnectionStatusRefresh()
    {
        if (_connectionRefreshTimer is not null)
        {
            return;
        }

        _connectionRefreshTimer = Dispatcher.CreateTimer();
        _connectionRefreshTimer.Interval = TimeSpan.FromSeconds(1.5);
        _connectionRefreshTimer.Tick += async (_, _) =>
        {
            await RefreshQueueAndHistoryAsync();
        };
        _connectionRefreshTimer.Start();
    }

    private async Task RefreshQueueAndHistoryAsync()
    {
        if (_isRefreshingQueueState)
        {
            return;
        }

        _isRefreshingQueueState = true;
        try
        {
            await MigrateTerminalQueueItemsToHistoryAsync(showStatus: false);
            RefreshConnectionStatus();
            RenderQueue();
            RenderHistory();
            RefreshOpenDetailPages();
            UpdateQueueSummary();
            if (_queueRunning)
            {
                DispatchQueue();
            }
        }
        catch (Exception ex)
        {
            SetStatus($"刷新队列状态失败：{ex.Message}");
        }
        finally
        {
            _isRefreshingQueueState = false;
        }
    }

    private void RefreshConnectionStatus()
    {
        SyncBusyConnectionsFromRunningTasks();

        foreach (var task in _state.Queue.Where(task => task.Status == Image2TaskStatus.Queued))
        {
            task.StatusDetail = BuildWaitingStatus(task);
        }

        UpdateQueueSummary();
        RenderQueue();
        RenderHomeDetail();
    }

    private void SyncBusyConnectionsFromRunningTasks()
    {
        var activeConnectionIds = _state.Queue
            .Where(task =>
                (task.Status == Image2TaskStatus.Running || _runningTasks.ContainsKey(task.Id)) &&
                !string.IsNullOrWhiteSpace(task.AssignedConnectionId))
            .Select(task => task.AssignedConnectionId!)
            .ToHashSet(StringComparer.Ordinal);
        _busyConnections.RemoveWhere(connectionId => !activeConnectionIds.Contains(connectionId));
        foreach (var connectionId in activeConnectionIds)
        {
            _busyConnections.Add(connectionId);
        }
    }

    private string BuildWaitingStatus(Image2QueuedTask task)
    {
        if (!_queueRunning)
        {
            return "已入队，等待运行队列";
        }

        if (_connections.Count == 0)
        {
            return "等待接口配置";
        }

        if (task.AssignmentMode == Image2ConnectionAssignmentMode.Manual)
        {
            if (string.IsNullOrWhiteSpace(task.RequestedConnectionId))
            {
                return "等待选择指定接口";
            }

            var requested = _connections.FirstOrDefault(connection => connection.Id == task.RequestedConnectionId);
            if (requested is null)
            {
                return "指定接口不存在，请重新选择";
            }

            return _busyConnections.Contains(requested.Id)
                ? $"等待指定接口空闲：{requested.Title}"
                : $"等待指定接口：{requested.Title}";
        }

        var idleCount = _connections.Count(connection => !_busyConnections.Contains(connection.Id));
        return idleCount > 0 ? $"等待调度（空闲接口 {idleCount} 个）" : "等待空闲接口";
    }

    private async Task RunTaskAsync(Image2QueuedTask task, Image2ConnectionProfile connection)
    {
        if (_runningTasks.ContainsKey(task.Id))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _runningTasks[task.Id] = cts;
        if (task.Status != Image2TaskStatus.Running ||
            !string.Equals(task.AssignedConnectionId, connection.Id, StringComparison.Ordinal))
        {
            _busyConnections.Add(connection.Id);
            task.Status = Image2TaskStatus.Running;
            task.AssignedConnectionId = connection.Id;
            task.AssignedConnectionTitle = connection.Title;
            task.StatusDetail = $"正在调用：{connection.Title}";
            task.StartedAt = DateTime.Now;
            task.UpdatedAt = DateTime.Now;
        }
        GenerationStatusNotifier.ShowGenerating();
        await PersistAndRenderAsync();

        try
        {
            var saveWarning = await TrySaveTaskSnapshotAsync(task);
            cts.Token.ThrowIfCancellationRequested();
            var result = await _client.GenerateImageAsync(BuildOptionsForTask(task, connection), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            task.Status = result.HasImage ? Image2TaskStatus.Completed : Image2TaskStatus.Failed;
            task.StatusDetail = result.HasImage ? $"生图成功：{connection.Title}" : $"生图失败：未识别到图片字段";
            task.CompletedAt = DateTime.Now;
            task.UpdatedAt = DateTime.Now;
            task.ImageUrl = result.ImageUrl;
            task.AssistantText = result.AssistantText;
            task.RawJson = result.RawJson;
            if (result.HasImage)
            {
                var resultSaveWarning = await TrySaveResultAsync(task, result);
                saveWarning = CombineWarnings(saveWarning, resultSaveWarning);
                if (!string.IsNullOrWhiteSpace(resultSaveWarning))
                {
                    task.StatusDetail = $"生图成功，但自动保存失败：{resultSaveWarning}";
                    task.ErrorMessage = resultSaveWarning;
                }
            }
            else
            {
                saveWarning = CombineWarnings(saveWarning, await TrySaveFailureAsync(task, result.RawJson));
            }

            var history = await AddHistoryAsync(task, result.HasImage, result.HasImage ? saveWarning : CombineWarnings("未识别到图片字段", saveWarning));
            MoveFinishedTaskOutOfQueue(task, history);
            _lastResult = result;
            _lastRenderedTask = task;
            await RenderResultAsync(result, task);
            GenerationStatusNotifier.ShowCompleted(result.HasImage);
        }
        catch (OperationCanceledException)
        {
            task.Status = Image2TaskStatus.Stopped;
            task.StatusDetail = "已停止，可重试";
            task.UpdatedAt = DateTime.Now;
            task.RawJson = "用户停止任务";
            var saveWarning = await TrySaveFailureAsync(task, "用户停止任务");
            var history = await AddHistoryAsync(task, false, CombineWarnings("用户停止任务", saveWarning));
            MoveFinishedTaskOutOfQueue(task, history);
            GenerationStatusNotifier.ShowCompleted(false);
        }
        catch (Exception ex)
        {
            task.Status = Image2TaskStatus.Failed;
            task.ErrorMessage = ex.Message;
            task.StatusDetail = $"生图失败：{ex.Message}";
            task.CompletedAt = DateTime.Now;
            task.UpdatedAt = DateTime.Now;
            task.RawJson = ex.ToString();
            RawJsonEditor.Text = ex.ToString();
            var saveWarning = await TrySaveFailureAsync(task, ex.ToString());
            var history = await AddHistoryAsync(task, false, CombineWarnings(ex.Message, saveWarning));
            MoveFinishedTaskOutOfQueue(task, history);
            GenerationStatusNotifier.ShowCompleted(false);
        }
        finally
        {
            _runningTasks.Remove(task.Id);
            SyncBusyConnectionsFromRunningTasks();
            await PersistAndRenderAsync();
            DispatchQueue();
        }
    }

    private Image2RequestOptions BuildOptionsForTask(Image2QueuedTask task, Image2ConnectionProfile connection)
    {
        return new Image2RequestOptions
        {
            ApiKey = connection.ApiKey,
            BaseUrl = Image2Settings.NormalizeBaseUrl(connection.BaseUrl),
            Model = connection.Model,
            Group = connection.Group,
            Prompt = task.Prompt,
            Size = task.Size,
            Quality = task.Quality,
            ResponseFormat = task.ResponseFormat,
            OutputFormat = task.OutputFormat,
            Background = task.Background,
            Transparent = task.Transparent,
            UseAsyncTask = task.UseAsyncTask,
            UseChatEndpoint = task.UseChatEndpoint,
            StreamChat = task.StreamChat,
            ReferenceImages = Image2TaskFileService.BuildFileResults(task.ReferenceImages),
            ReferenceImageUrls = task.ReferenceImageUrls
        };
    }

    private async Task<string?> TrySaveTaskSnapshotAsync(Image2QueuedTask task)
    {
        try
        {
            await _fileService.SaveTaskSnapshotAsync(task, _settings);
            return null;
        }
        catch (Exception ex)
        {
            return $"任务快照保存失败：{ex.Message}";
        }
    }

    private async Task<string?> TrySaveResultAsync(Image2QueuedTask task, Image2Result result)
    {
        try
        {
            await _fileService.SaveResultAsync(task, result, _settings);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task<string?> TrySaveFailureAsync(Image2QueuedTask task, string error)
    {
        try
        {
            await _fileService.SaveFailureAsync(task, error, _settings);
            return null;
        }
        catch (Exception ex)
        {
            return $"失败信息保存失败：{ex.Message}";
        }
    }

    private static string? CombineWarnings(params string?[] values)
    {
        var parts = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct()
            .ToArray();
        return parts.Length == 0 ? null : string.Join("；", parts);
    }

    private async Task<Image2HistoryEntry> AddHistoryAsync(Image2QueuedTask task, bool succeeded, string? error)
    {
        var versionGroupId = string.IsNullOrWhiteSpace(task.VersionGroupId) ? task.Id : task.VersionGroupId;
        task.VersionGroupId = versionGroupId;
        var version = _state.History
            .Where(item => GetHistoryGroupId(item) == versionGroupId)
            .Select(item => item.Version)
            .DefaultIfEmpty(0)
            .Max() + 1;

        var history = new Image2HistoryEntry
        {
            SourceTaskId = versionGroupId,
            Version = version,
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
            Succeeded = succeeded,
            StatusText = succeeded ? "生图成功" : "生图失败",
            ErrorMessage = error,
            CreatedAt = DateTime.Now
        };

        _state.History.Insert(0, history);

        if (_state.History.Count > 80)
        {
            _state.History.RemoveRange(80, _state.History.Count - 80);
        }

        try
        {
            await _taskStore.SaveAsync(_state);
            await _fileService.SaveHistoryManifestAsync(history, _settings);
        }
        catch (Exception ex)
        {
            task.ErrorMessage = CombineWarnings(task.ErrorMessage, $"历史保存失败：{ex.Message}");
            task.StatusDetail = CombineWarnings(task.StatusDetail, "历史保存失败，稍后会再次尝试") ?? task.StatusDetail;
        }

        return history;
    }

    private async Task MigrateTerminalQueueItemsToHistoryAsync(bool showStatus = true)
    {
        var terminalTasks = _state.Queue
            .Where(task =>
                !_runningTasks.ContainsKey(task.Id) &&
                task.Status is Image2TaskStatus.Completed or Image2TaskStatus.Failed)
            .ToArray();
        if (terminalTasks.Length == 0)
        {
            return;
        }

        foreach (var task in terminalTasks)
        {
            var succeeded = task.Status == Image2TaskStatus.Completed;
            var error = succeeded
                ? task.ErrorMessage
                : CombineWarnings(task.ErrorMessage, task.StatusDetail);
            var history = FindExistingHistoryForFinishedTask(task) ?? await AddHistoryAsync(task, succeeded, error);
            MoveFinishedTaskOutOfQueue(task, history);
        }

        await PersistAndRenderAsync(showStatus ? "已整理旧队列状态" : null);
    }

    private Image2HistoryEntry? FindExistingHistoryForFinishedTask(Image2QueuedTask task)
    {
        var groupId = string.IsNullOrWhiteSpace(task.VersionGroupId) ? task.Id : task.VersionGroupId;
        return _state.History.FirstOrDefault(history =>
            GetHistoryGroupId(history) == groupId &&
            string.Equals(history.Title, task.Title, StringComparison.Ordinal) &&
            string.Equals(history.Prompt, task.Prompt, StringComparison.Ordinal) &&
            string.Equals(history.RawJson, task.RawJson, StringComparison.Ordinal) &&
            history.Succeeded == (task.Status == Image2TaskStatus.Completed));
    }

    private void MoveFinishedTaskOutOfQueue(Image2QueuedTask task, Image2HistoryEntry? history)
    {
        _state.Queue.RemoveAll(item => item.Id == task.Id);

        if (_selectedTask?.Id == task.Id)
        {
            _selectedTask = null;
        }

        if (_homeDetailTaskId == task.Id)
        {
            _homeDetailTaskId = null;
            _homeDetailHistoryId = history?.Id;
        }
    }

    private static Image2ReferenceAsset CloneReference(Image2ReferenceAsset asset)
    {
        return new Image2ReferenceAsset { FileName = asset.FileName, Path = asset.Path };
    }

    private async void OnQueueTaskSelectClicked(object? sender, EventArgs e)
    {
        if (FindTaskFromSender(sender) is not { } task)
        {
            return;
        }

        _selectedTask = task;
        await LoadTaskIntoEditorAsync(task);
        RenderQueue();
    }

    private async Task LoadTaskIntoEditorAsync(Image2QueuedTask task)
    {
        _isLoadingTask = true;
        TaskTitleEntry.Text = task.Title;
        PromptEditor.Text = task.Prompt;
        SetPickerValue(SizePicker, task.Size);
        SetPickerValue(QualityPicker, task.Quality);
        AssignmentModePicker.SelectedIndex = task.AssignmentMode == Image2ConnectionAssignmentMode.Manual ? 1 : 0;
        SetConnectionPicker(task.RequestedConnectionId ?? task.AssignedConnectionId);
        SetMode(Enum.TryParse<WorkMode>(task.Mode, out var mode) ? mode : WorkMode.Chat);
        ReferenceUrlEditor.Text = string.Join(Environment.NewLine, task.ReferenceImageUrls);
        _referenceImages.Clear();
        foreach (var asset in task.ReferenceImages)
        {
            if (!File.Exists(asset.Path))
            {
                continue;
            }

            var file = new FileResult(asset.Path)
            {
                FileName = asset.FileName
            };
            _referenceImages.Add(await CreateReferenceImageItemAsync(file, asset));
        }

        _isLoadingTask = false;
        UpdateReferenceSummary();
    }

    private void SetConnectionPicker(string? connectionId)
    {
        var index = _connections.FindIndex(connection => connection.Id == connectionId);
        ConnectionPicker.SelectedIndex = index >= 0 ? index : 0;
    }

    private async void OnQueueTaskMoveUpClicked(object? sender, EventArgs e)
    {
        MoveTask(sender, -1);
        await PersistAndRenderAsync();
        DispatchQueue();
    }

    private async void OnQueueTaskMoveDownClicked(object? sender, EventArgs e)
    {
        MoveTask(sender, 1);
        await PersistAndRenderAsync();
        DispatchQueue();
    }

    private void MoveTask(object? sender, int delta)
    {
        if (FindTaskFromSender(sender) is not { } task || task.Status == Image2TaskStatus.Running)
        {
            return;
        }

        var index = _state.Queue.IndexOf(task);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _state.Queue.Count)
        {
            return;
        }

        _state.Queue.RemoveAt(index);
        _state.Queue.Insert(target, task);
    }

    private async void OnQueueTaskDeleteClicked(object? sender, EventArgs e)
    {
        if (FindTaskFromSender(sender) is not { } task || task.Status == Image2TaskStatus.Running)
        {
            SetStatus("运行中的任务需要先停止");
            return;
        }

        _state.Queue.Remove(task);
        if (_selectedTask?.Id == task.Id)
        {
            _selectedTask = null;
        }

        await PersistAndRenderAsync("已删除任务");
    }

    private async void OnQueueTaskStopClicked(object? sender, EventArgs e)
    {
        if (FindTaskFromSender(sender) is not { } task)
        {
            return;
        }

        if (_runningTasks.TryGetValue(task.Id, out var cts))
        {
            cts.Cancel();
            task.StatusDetail = "正在停止...";
            task.UpdatedAt = DateTime.Now;
            await PersistAndRenderAsync();
        }
    }

    private async void OnQueueTaskRetryClicked(object? sender, EventArgs e)
    {
        if (FindTaskFromSender(sender) is not { } task || task.Status == Image2TaskStatus.Running)
        {
            return;
        }

        task.Status = Image2TaskStatus.Queued;
        task.StatusDetail = BuildWaitingStatus(task);
        task.ErrorMessage = null;
        _queueRunning = true;
        await PersistAndRenderAsync("已重新排队");
        DispatchQueue();
    }

    private async void OnQueueAssignmentModeClicked(object? sender, EventArgs e)
    {
        if (FindTaskFromSender(sender) is not { } task || !task.CanEdit)
        {
            return;
        }

        task.AssignmentMode = task.AssignmentMode == Image2ConnectionAssignmentMode.Auto
            ? Image2ConnectionAssignmentMode.Manual
            : Image2ConnectionAssignmentMode.Auto;

        var selected = GetSelectedConnection();
        if (task.AssignmentMode == Image2ConnectionAssignmentMode.Manual && selected is not null)
        {
            task.RequestedConnectionId = selected.Id;
            task.RequestedConnectionTitle = selected.Title;
        }
        else
        {
            task.RequestedConnectionId = null;
            task.RequestedConnectionTitle = null;
        }

        task.StatusDetail = BuildWaitingStatus(task);
        await PersistAndRenderAsync("已更新接口分配");
        DispatchQueue();
    }

    private Image2QueuedTask? FindTaskFromSender(object? sender)
    {
        return sender is Button { CommandParameter: string id }
            ? _state.Queue.FirstOrDefault(task => task.Id == id)
            : null;
    }

    private Image2QueuedTask? FindTaskFromContext(object? sender)
    {
        return sender switch
        {
            BindableObject { BindingContext: Image2QueuedTask task } => task,
            Button { CommandParameter: string id } => _state.Queue.FirstOrDefault(task => task.Id == id),
            _ => null
        };
    }

    private Image2HistoryEntry? FindHistoryFromContext(object? sender)
    {
        return sender switch
        {
            BindableObject { BindingContext: Image2HistoryEntry history } => history,
            Button { CommandParameter: string id } => _state.History.FirstOrDefault(history => history.Id == id),
            _ => null
        };
    }

    private async void OnHistoryRerunClicked(object? sender, EventArgs e)
    {
        var history = FindHistoryFromContext(sender);
        if (history is null)
        {
            return;
        }

        await Navigation.PushAsync(CreateTaskEditorPage(CreateRerunDraftFromHistory(history), addAsNewTask: true));
    }

    private async void OnHistoryDeleteClicked(object? sender, EventArgs e)
    {
        var history = FindHistoryFromContext(sender);
        if (history is null)
        {
            return;
        }

        var deleteGroup = GetVersionCount(history) > 1 && await DisplayAlertAsync(
            "删除范围",
            "这个主任务有多个版本。要删除整个主任务，还是只删除当前版本？",
            "删除全部版本",
            "只删当前版本");
        var targets = deleteGroup
            ? _state.History.Where(item => GetHistoryGroupId(item) == GetHistoryGroupId(history)).ToArray()
            : new[] { history };
        var confirm = await DisplayAlertAsync(
            "确认删除",
            $"将删除 {targets.Length} 个历史版本，并同步删除对应任务文件夹和索引。此操作不可恢复。",
            "删除",
            "取消");
        if (!confirm)
        {
            return;
        }

        var deletedFiles = 0;
        var warnings = new List<string>();
        foreach (var item in targets)
        {
            _state.History.Remove(item);
            try
            {
                await _fileService.DeleteHistoryAsync(item, _settings, deleteFiles: true);
                deletedFiles++;
            }
            catch (Exception ex)
            {
                warnings.Add($"{item.Title} v{item.Version}: {ex.Message}");
            }
        }

        if (_homeDetailHistoryId is not null && targets.Any(item => item.Id == _homeDetailHistoryId))
        {
            _homeDetailHistoryId = null;
        }

        await _taskStore.SaveAsync(_state);
        RenderHistory();
        RefreshOpenDetailPages();
        UpdateQueueSummary();
        SetStatus(warnings.Count == 0
            ? $"已删除 {targets.Length} 个历史版本和 {deletedFiles} 个文件夹"
            : $"已删除历史，部分文件夹清理失败：{warnings[0]}");
    }

    private Image2QueuedTask CreateRerunDraftFromHistory(Image2HistoryEntry history)
    {
        var groupId = GetHistoryGroupId(history);
        var nextVersion = _state.History
            .Where(item => GetHistoryGroupId(item) == groupId)
            .Select(item => item.Version)
            .DefaultIfEmpty(0)
            .Max() + 1;

        return new Image2QueuedTask
        {
            Id = Guid.NewGuid().ToString("N"),
            VersionGroupId = groupId,
            Title = BuildRerunTitle(history.Title, nextVersion),
            Prompt = history.Prompt,
            Mode = history.Mode,
            Size = history.Size,
            Quality = history.Quality,
            ResponseFormat = string.IsNullOrWhiteSpace(history.ResponseFormat) ? _settings.ResponseFormat : history.ResponseFormat,
            OutputFormat = string.IsNullOrWhiteSpace(history.OutputFormat) ? _settings.OutputFormat : history.OutputFormat,
            Background = string.IsNullOrWhiteSpace(history.Background) ? _settings.Background : history.Background,
            Transparent = history.Transparent,
            ReferenceImages = history.ReferenceImages.Select(CloneReference).ToList(),
            ReferenceImageUrls = history.ReferenceImageUrls.ToList(),
            UseAsyncTask = history.UseAsyncTask || history.ReferenceImages.Count + history.ReferenceImageUrls.Count > 1,
            UseChatEndpoint = history.UseChatEndpoint,
            StreamChat = history.StreamChat,
            AssignmentMode = history.AssignmentMode,
            RequestedConnectionId = history.RequestedConnectionId,
            RequestedConnectionTitle = history.RequestedConnectionTitle,
            Status = Image2TaskStatus.Queued,
            StatusDetail = "从历史版本重新排队",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
    }

    private async void OnQueueTaskOpenDetailsClicked(object? sender, EventArgs e)
    {
        if (FindTaskFromContext(sender) is not { } task)
        {
            return;
        }

        _selectedTask = task;
        await LoadTaskIntoEditorAsync(task);
        await Navigation.PushAsync(CreateTaskDetailPage(task));
    }

    private async void OnQueueTaskEditClicked(object? sender, EventArgs e)
    {
        if (FindTaskFromContext(sender) is not { } task)
        {
            return;
        }

        _selectedTask = task;
        await LoadTaskIntoEditorAsync(task);
        await Navigation.PushAsync(CreateTaskEditorPage(task));
    }

    private async void OnHistoryOpenDetailsClicked(object? sender, EventArgs e)
    {
        if (FindHistoryFromContext(sender) is not { } history)
        {
            return;
        }

        await Navigation.PushAsync(CreateHistoryDetailPage(history));
    }

    private async void OnOpenQueueDetailsClicked(object? sender, EventArgs e)
    {
        _queueDetailsView = BuildQueueDetailsView();
        await Navigation.PushAsync(new ContentPage
        {
            Title = "队列详情",
            BackgroundColor = ResourceColor("PageBg"),
            Content = _queueDetailsView
        });
    }

    private async void OnOpenHistoryDetailsClicked(object? sender, EventArgs e)
    {
        _historyDetailsView = BuildHistoryDetailsView();
        await Navigation.PushAsync(new ContentPage
        {
            Title = "历史记录",
            BackgroundColor = ResourceColor("PageBg"),
            Content = _historyDetailsView
        });
    }

    private async void OnOpenOutputFolderClicked(object? sender, EventArgs e)
    {
        var path = _lastRenderedTask?.OutputFolder ?? _settings.GetResolvedOutputRootPath();
        try
        {
            await OutputFolderWriter.OpenFolderAsync(path);
            SetStatus("已打开保存文件夹");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("打开失败", $"{path}\n\n{ex.Message}", "知道了");
        }
    }

    private async void OnCopyUrlClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastResult?.ImageUrl))
        {
            await DisplayAlertAsync("没有 URL", "当前结果没有可复制的图片 URL。", "知道了");
            return;
        }

        await Clipboard.Default.SetTextAsync(_lastResult.ImageUrl);
        SetStatus("图片 URL 已复制");
    }

    private async void OnOpenSpecificFolderClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string folder } || string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        try
        {
            await OutputFolderWriter.OpenFolderAsync(folder);
            SetStatus("已打开任务文件夹");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("打开失败", ex.Message, "知道了");
        }
    }

    private async void OnManualSaveResultImageClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: ResultImageSaveRequest request })
        {
            return;
        }

        try
        {
            var bytes = await LoadResultImageBytesAsync(request.ImagePath, request.ImageUrl);
            if (bytes is null || bytes.Length == 0)
            {
                await DisplayAlertAsync("没有图片", "当前任务没有可保存的图片文件。", "知道了");
                return;
            }

            var extension = NormalizeManualSaveExtension(request.OutputFormat, request.ImagePath, request.ImageUrl);
            var mimeType = extension switch
            {
                "jpg" => "image/jpeg",
                "webp" => "image/webp",
                _ => "image/png"
            };
            var saveToDefaultGallery = DeviceInfo.Platform == DevicePlatform.Android;
            var destinationText = saveToDefaultGallery
                ? "将保存到系统相册的 Image2 Studio 文件夹。"
                : "请选择图片保存位置。";
            SetStatus($"正在保存图片：{destinationText}");
            await DisplayAlertAsync("开始保存", destinationText, "知道了");

            var fileName = $"{SanitizeManualFileName(request.Title)}-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}";
            var saved = await UserFileSaver.SaveAsync(fileName, mimeType, bytes, askForLocation: !saveToDefaultGallery);
            if (saved is null)
            {
                SetStatus("已取消手动保存");
                return;
            }

            SetStatus($"图片已保存：{saved.Location}");
            await DisplayAlertAsync("保存完成", $"图片已保存到：\n{saved.Location}", "知道了");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("保存失败", ex.Message, "知道了");
        }
    }

    private async void OnCreateHistoryFolderClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: HistoryFolderCreateRequest request })
        {
            return;
        }

        var history = _state.History.FirstOrDefault(item => item.Id == request.HistoryId);
        if (history is null)
        {
            await DisplayAlertAsync("历史不存在", "没有找到这条历史记录。", "知道了");
            return;
        }

        try
        {
            SetStatus("正在为当前历史创建任务文件夹");
            var folderSettings = _settings;
#if ANDROID
            var root = await UserFileSaver.EnsureManagedOutputRootAsync("Image2 Studio Library");
            if (string.IsNullOrWhiteSpace(root))
            {
                SetStatus("已打开文件管理权限页，授权后可继续创建任务文件夹");
                return;
            }

            folderSettings = Image2Settings.Load();
            folderSettings.OutputRootPath = root;
            if (string.IsNullOrWhiteSpace(_settings.OutputRootPath))
            {
                _settings.OutputRootPath = root;
                _settings.Save();
            }
#endif
            var task = CreateFolderBackfillTask(history);
            await _fileService.SaveTaskSnapshotAsync(task, folderSettings);
            string? imageWarning = null;
            try
            {
                await SaveHistoryImageIntoFolderAsync(history, task.OutputFolder);
            }
            catch (Exception imageEx)
            {
                imageWarning = imageEx.Message;
            }

            history.OutputFolder = task.OutputFolder;
            task.OutputFolder = history.OutputFolder;
            task.ImagePath = history.ImagePath;
            await _fileService.SaveTaskSnapshotAsync(task, folderSettings);
            await _fileService.SaveHistoryManifestAsync(history, folderSettings);
            await _taskStore.SaveAsync(_state);

            RenderHistory();
            RefreshOpenDetailPages();
            RefreshHistoryDetailViews(history.Id);
            UpdateQueueSummary();
            SetStatus(string.IsNullOrWhiteSpace(imageWarning)
                ? $"已为历史创建任务文件夹：{history.OutputFolder}"
                : $"已创建任务文件夹，图片复制失败：{imageWarning}");
            await DisplayAlertAsync(
                "文件夹已创建",
                string.IsNullOrWhiteSpace(imageWarning)
                    ? $"当前历史的任务文件夹已创建：\n{history.OutputFolder}"
                    : $"当前历史的任务文件夹已创建：\n{history.OutputFolder}\n\n图片未复制：{imageWarning}",
                "知道了");
        }
        catch (Exception ex)
        {
            SetStatus($"创建任务文件夹失败：{ex.Message}");
            await DisplayAlertAsync("创建失败", ex.Message, "知道了");
        }
    }

    private static async Task<byte[]?> LoadResultImageBytesAsync(string? imagePath, string? imageUrl)
    {
        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            return await File.ReadAllBytesAsync(imagePath);
        }

        if (!string.IsNullOrWhiteSpace(imageUrl) && Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
        {
            using var http = new HttpClient();
            return await http.GetByteArrayAsync(uri);
        }

        return null;
    }

    private Image2QueuedTask CreateFolderBackfillTask(Image2HistoryEntry history)
    {
        return new Image2QueuedTask
        {
            Id = string.IsNullOrWhiteSpace(history.SourceTaskId) ? history.Id : history.SourceTaskId,
            VersionGroupId = GetHistoryGroupId(history),
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
            Status = history.Succeeded ? Image2TaskStatus.Completed : Image2TaskStatus.Failed,
            StatusDetail = history.StatusText,
            AssignedConnectionId = history.AssignedConnectionId,
            AssignedConnectionTitle = history.AssignedConnectionTitle,
            OutputFolder = string.IsNullOrWhiteSpace(history.OutputFolder) ? null : history.OutputFolder,
            ImagePath = history.ImagePath,
            ImageUrl = history.ImageUrl,
            ErrorMessage = history.ErrorMessage,
            AssistantText = history.AssistantText,
            RawJson = history.RawJson,
            CreatedAt = history.CreatedAt,
            UpdatedAt = DateTime.Now,
            CompletedAt = history.CreatedAt
        };
    }

    private async Task SaveHistoryImageIntoFolderAsync(Image2HistoryEntry history, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var bytes = await LoadResultImageBytesAsync(history.ImagePath, history.ImageUrl);
        if (bytes is null || bytes.Length == 0)
        {
            return;
        }

        var extension = NormalizeManualSaveExtension(history.OutputFormat, history.ImagePath, history.ImageUrl);
        var relativePath = $"result.{extension}";
        await OutputFolderWriter.WriteBytesAsync(folder, relativePath, bytes);
        history.ImagePath = OutputFolderWriter.IsDocumentTreePath(folder)
            ? $"{folder}/{relativePath}"
            : System.IO.Path.Combine(folder, relativePath);
    }

    private static string NormalizeManualSaveExtension(string? outputFormat, string? imagePath, string? imageUrl)
    {
        var extension = System.IO.Path.GetExtension(imagePath ?? string.Empty).TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension) && Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
        {
            extension = System.IO.Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToLowerInvariant();
        }

        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = outputFormat?.Trim().TrimStart('.').ToLowerInvariant();
        }

        return extension is "jpg" or "jpeg" ? "jpg" : extension is "webp" ? "webp" : "png";
    }

    private static string SanitizeManualFileName(string value)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var sanitized = new string((value ?? string.Empty).Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "image2-result" : sanitized;
    }

    private async Task RenderResultAsync(Image2Result result, Image2QueuedTask task)
    {
        EndpointLabel.Text = $"{result.Endpoint} / {task.AssignedConnectionTitle}";
        ImageUrlLabel.Text = result.ImageUrl ?? task.ImagePath ?? "";
        AssistantTextEditor.Text = result.AssistantText ?? "";
        RawJsonEditor.Text = result.RawJson;
        LastResultStatusLabel.Text = result.HasImage ? "生图成功" : "生图失败";
        LastResultStatusLabel.TextColor = result.HasImage ? ResourceColor("Success") : ResourceColor("Danger");
        LastResultMetaLabel.Text = $"{task.Title} / {task.Size} / {task.AssignedConnectionTitle ?? "未记录接口"}";
        EmptyPreview.IsVisible = true;

        if (!string.IsNullOrWhiteSpace(task.ImagePath) && File.Exists(task.ImagePath))
        {
            var path = task.ImagePath;
            ResultImage.Source = ImageSource.FromStream(() => File.OpenRead(path));
        }
        else if (result.ImageBytes is { Length: > 0 })
        {
            var bytes = result.ImageBytes;
            ResultImage.Source = ImageSource.FromStream(() => new MemoryStream(bytes));
        }
        else if (!string.IsNullOrWhiteSpace(result.ImageUrl))
        {
            ResultImage.Source = ImageSource.FromUri(new Uri(result.ImageUrl));
        }
        else
        {
            ResultImage.Source = null;
        }

        await AnimateResultAsync(result.HasImage);
        RefreshOpenDetailPages();
    }

    private ContentPage CreateTaskEditorPage(Image2QueuedTask? task, bool addAsNewTask = false)
    {
        var isEditing = task is not null && !addAsNewTask;
        var isRerunDraft = task is not null && addAsNewTask;
        var titleEntry = CreatePlainEntry("任务标题", task?.Title ?? $"任务 {DateTime.Now:HHmmss}");
        var promptEditor = CreatePlainEditor("输入提示词", task?.Prompt ?? _settings.Prompt, DeviceInfo.Platform == DevicePlatform.Android ? 140 : 170);
        var sizePicker = CreatePicker("尺寸");
        foreach (var item in SizePicker.Items)
        {
            sizePicker.Items.Add(item);
        }
        sizePicker.Items.Add("自定义");

        var customSizeEntry = CreatePlainEntry("宽x高，例如 1280x720", "");
        var initialSize = task?.Size ?? _settings.Size;
        var usesCustomSize = !SizePicker.Items.Contains(initialSize);
        var qualityPicker = CreatePicker("质量");
        foreach (var item in QualityPicker.Items)
        {
            qualityPicker.Items.Add(item);
        }

        SetPickerValue(sizePicker, usesCustomSize ? "自定义" : initialSize);
        customSizeEntry.Text = usesCustomSize ? initialSize : "";
        var customSizeField = CreateLabeledInput("自定义尺寸", customSizeEntry, "格式：宽x高，例如 1280x720");
        customSizeField.IsVisible = sizePicker.SelectedItem?.ToString() == "自定义";
        sizePicker.SelectedIndexChanged += (_, _) =>
        {
            customSizeField.IsVisible = sizePicker.SelectedItem?.ToString() == "自定义";
        };
        SetPickerValue(qualityPicker, task?.Quality ?? _settings.Quality);

        var assignmentPicker = CreatePicker("接口分配");
        assignmentPicker.Items.Add("自动分配");
        assignmentPicker.Items.Add("手动指定");
        assignmentPicker.SelectedIndex = task?.AssignmentMode == Image2ConnectionAssignmentMode.Manual ? 1 : 0;

        var connectionPicker = CreatePicker("选择接口");
        foreach (var connection in _connections)
        {
            connectionPicker.Items.Add(connection.Title);
        }

        var connectionIndex = _connections.FindIndex(connection => connection.Id == task?.RequestedConnectionId);
        connectionPicker.SelectedIndex = connectionIndex >= 0 ? connectionIndex : (_connections.Count > 0 ? 0 : -1);
        connectionPicker.IsEnabled = assignmentPicker.SelectedIndex == 1;
        assignmentPicker.SelectedIndexChanged += (_, _) =>
        {
            connectionPicker.IsEnabled = assignmentPicker.SelectedIndex == 1;
        };

        var modePicker = CreatePicker("模式");
        modePicker.Items.Add("稳定生成");
        modePicker.Items.Add("参考改图");
        modePicker.Items.Add("直连生成");
        modePicker.SelectedIndex = task is null
            ? 0
            : ParseWorkMode(task.Mode) switch
        {
            WorkMode.Edit => 1,
            WorkMode.Generate => 2,
            _ => 0
        };

        var localReferences = new List<ReferenceImageItem>();
        var referenceUrlEditor = CreatePlainEditor("每行一个参考图 URL", string.Join(Environment.NewLine, task?.ReferenceImageUrls ?? new List<string>()), 92);
        var referenceList = new VerticalStackLayout { Spacing = 8 };
        var referenceSummary = CreateCaptionLabel("");
        var referenceModeWarning = CreateProminentNotice("需要参考图", "参考改图需要至少一张参考图。没有参考图时，加入队列前会询问是否改为稳定生成。");
        var expandStatus = CreateCaptionLabel("DeepSeek 只扩写当前提示词。");
        var reasoningTitle = CreateSectionTitleLabel("思考过程");
        reasoningTitle.IsVisible = false;
        var reasoningEditor = CreatePlainEditor("deepseek-reasoner 的思考内容会显示在这里", string.Empty, 100);
        reasoningEditor.IsReadOnly = true;
        reasoningEditor.IsVisible = false;
        var reasoningField = WrapInput(reasoningEditor);
        reasoningField.IsVisible = false;
        var expandedPromptEditor = CreatePlainEditor("扩写结果会显示在这里", string.Empty, 130);
        expandedPromptEditor.IsReadOnly = true;
        var expandButton = CreateActionButton("扩写提示词", ResourceColor("ControlBg"), ResourceColor("TextStrong"));
        var pasteExpandedButton = CreateActionButton("粘贴到提示词", ResourceColor("ControlBg"), ResourceColor("TextStrong"));
        pasteExpandedButton.IsEnabled = false;
        expandButton.Clicked += async (_, _) =>
        {
            try
            {
                expandButton.IsEnabled = false;
                expandStatus.Text = "正在调用 DeepSeek 扩写...";
                var expanded = await _promptExpander.ExpandAsync(Image2Settings.Load(), promptEditor.Text ?? string.Empty);
                var hasReasoning = !string.IsNullOrWhiteSpace(expanded.Reasoning);
                reasoningEditor.Text = expanded.Reasoning ?? string.Empty;
                reasoningTitle.IsVisible = hasReasoning;
                reasoningEditor.IsVisible = hasReasoning;
                reasoningField.IsVisible = hasReasoning;
                expandedPromptEditor.Text = expanded.Prompt;
                pasteExpandedButton.IsEnabled = true;
                expandStatus.Text = "扩写完成，可粘贴到上方提示词。";
            }
            catch (Exception ex)
            {
                expandStatus.Text = $"扩写失败：{ex.Message}";
                await DisplayAlertAsync("扩写失败", ex.Message, "知道了");
            }
            finally
            {
                expandButton.IsEnabled = true;
            }
        };
        pasteExpandedButton.Clicked += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(expandedPromptEditor.Text))
            {
                expandStatus.Text = "还没有可粘贴的扩写结果。";
                return;
            }

            promptEditor.Text = expandedPromptEditor.Text;
            expandStatus.Text = "扩写提示词已粘贴到上方输入框。";
        };

        async Task LoadExistingReferencesAsync()
        {
            if (task is null)
            {
                return;
            }

            foreach (var asset in task.ReferenceImages)
            {
                if (!File.Exists(asset.Path))
                {
                    continue;
                }

                var file = new FileResult(asset.Path)
                {
                    FileName = asset.FileName
                };
                localReferences.Add(await CreateReferenceImageItemAsync(file, asset));
            }
        }

        void RefreshReferences()
        {
            var urls = ParseReferenceImageUrls(referenceUrlEditor.Text);
            var total = localReferences.Count + urls.Length;
            referenceSummary.Text = total == 0
                ? "未添加参考图"
                : $"{total} 张参考图，本地 {localReferences.Count} / URL {urls.Length}";
            referenceModeWarning.IsVisible = total == 0 && ModeFromPicker(modePicker) == WorkMode.Edit;

            referenceList.Children.Clear();
            if (total == 0)
            {
                referenceList.Children.Add(CreateCaptionLabel("参考改图时可添加本地图片或图片 URL。"));
                return;
            }

            for (var index = 0; index < localReferences.Count; index++)
            {
                var item = localReferences[index];
                referenceList.Children.Add(CreateEditorReferenceRow(
                    CreateImagePreview(item.PreviewBytes, 64),
                    $"本地 {index + 1}",
                    item.File.FileName,
                    () =>
                    {
                        localReferences.Remove(item);
                        RefreshReferences();
                    }));
            }

            for (var index = 0; index < urls.Length; index++)
            {
                var url = urls[index];
                referenceList.Children.Add(CreateEditorReferenceRow(
                    CreateUrlPreviewSized(url, 64),
                    $"URL {index + 1}",
                    url,
                    () =>
                    {
                        var remaining = ParseReferenceImageUrls(referenceUrlEditor.Text).ToList();
                        remaining.Remove(url);
                        referenceUrlEditor.Text = string.Join(Environment.NewLine, remaining);
                        RefreshReferences();
                    }));
            }
        }

        referenceUrlEditor.TextChanged += (_, _) => RefreshReferences();
        modePicker.SelectedIndexChanged += (_, _) => RefreshReferences();

        var pickReferenceButton = CreateActionButton("添加参考图", ResourceColor("ControlBg"), ResourceColor("TextStrong"));
        pickReferenceButton.Clicked += async (_, _) =>
        {
            try
            {
                var results = await FilePicker.Default.PickMultipleAsync(new PickOptions
                {
                    PickerTitle = "选择参考图片",
                    FileTypes = FilePickerFileType.Images
                });

                var selected = (results ?? Enumerable.Empty<FileResult?>())
                    .Where(result => result is not null)
                    .Cast<FileResult>()
                    .ToArray();
                if (selected.Length == 0)
                {
                    return;
                }

                var items = await Task.WhenAll(selected.Select(file => CreateReferenceImageItemAsync(file, null)));
                localReferences.AddRange(items);
                modePicker.SelectedIndex = 1;
                RefreshReferences();
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync("选择失败", ex.Message, "知道了");
            }
        };

        var clearReferenceButton = CreateActionButton("清空参考图", ResourceColor("ControlBg"), ResourceColor("TextMuted"));
        clearReferenceButton.Clicked += (_, _) =>
        {
            localReferences.Clear();
            referenceUrlEditor.Text = string.Empty;
            RefreshReferences();
        };

        var saveButton = CreateOutlinePrimaryButton(isEditing ? "更新任务" : "加入队列");
        saveButton.Clicked += async (_, _) =>
        {
            var taskId = isEditing && task is not null ? task.Id : Guid.NewGuid().ToString("N");
            var mode = ModeFromPicker(modePicker);
            if (string.IsNullOrWhiteSpace(promptEditor.Text))
            {
                await DisplayAlertAsync("缺少提示词", "请先输入提示词，再加入队列。", "知道了");
                return;
            }

            var currentReferenceUrls = ParseReferenceImageUrls(referenceUrlEditor.Text).ToList();
            if (mode == WorkMode.Edit && localReferences.Count + currentReferenceUrls.Count == 0)
            {
                var switchToChat = await DisplayAlertAsync(
                    "缺少参考图",
                    "当前是参考改图模式，但还没有添加参考图。是否改为稳定生成模式并继续？",
                    "改为稳定生成",
                    "返回添加参考图");
                if (!switchToChat)
                {
                    return;
                }

                modePicker.SelectedIndex = 0;
                mode = WorkMode.Chat;
                RefreshReferences();
            }

            var saved = await CreateTaskFromValuesAsync(
                taskId,
                titleEntry.Text,
                promptEditor.Text,
                mode,
                ResolveSizeValue(sizePicker, customSizeEntry),
                SelectedPickerValue(qualityPicker, _settings.Quality),
                assignmentPicker.SelectedIndex == 1,
                SelectedConnectionFromPicker(connectionPicker),
                localReferences,
                currentReferenceUrls);

            if (!isEditing)
            {
                saved.StatusDetail = BuildWaitingStatus(saved);
                if (isRerunDraft && task is not null)
                {
                    saved.VersionGroupId = string.IsNullOrWhiteSpace(task.VersionGroupId) ? task.Id : task.VersionGroupId;
                    saved.CreatedAt = DateTime.Now;
                    saved.UpdatedAt = DateTime.Now;
                }

                _state.Queue.Add(saved);
                _selectedTask = saved;
                _homeDetailTaskId = saved.Id;
                _homeDetailHistoryId = null;
                await PersistAndRenderAsync($"已加入队列：{saved.Title}");
                await DisplayAlertAsync("已加入队列", $"{saved.Title} 已加入队列。", "知道了");
            }
            else
            {
                if (task is null)
                {
                    return;
                }

                var index = _state.Queue.FindIndex(item => item.Id == task.Id);
                if (index < 0)
                {
                    await DisplayAlertAsync("任务不存在", "队列中没有找到这个任务。", "知道了");
                    return;
                }

                saved.CreatedAt = task.CreatedAt;
                saved.VersionGroupId = string.IsNullOrWhiteSpace(task.VersionGroupId) ? task.Id : task.VersionGroupId;
                saved.OutputFolder = task.OutputFolder;
                saved.ImagePath = task.ImagePath;
                saved.ImageUrl = task.ImageUrl;
                saved.AssistantText = task.AssistantText;
                saved.RawJson = task.RawJson;
                saved.Status = task.Status == Image2TaskStatus.Failed ? Image2TaskStatus.Queued : task.Status;
                saved.StatusDetail = saved.Status == Image2TaskStatus.Queued ? BuildWaitingStatus(saved) : task.StatusDetail;
                _state.Queue[index] = saved;
                _selectedTask = saved;
                _homeDetailTaskId = saved.Id;
                _homeDetailHistoryId = null;
                await PersistAndRenderAsync($"已更新任务：{saved.Title}");
            }

            DispatchQueue();
            if (Navigation.NavigationStack.LastOrDefault() is ContentPage page && page != this)
            {
                await Navigation.PopAsync();
            }
        };

        var stack = new VerticalStackLayout
        {
            Padding = PagePadding(),
            Spacing = DeviceInfo.Platform == DevicePlatform.Android ? 10 : 14
        };
        stack.Children.Add(CreatePageHeader(
            isEditing ? "编辑内容" : isRerunDraft ? "历史版本重生" : "任务内容",
            isRerunDraft
                ? "可先修改提示词和参考图，再作为同一主任务的新版本入队。"
                : "填写提示词、尺寸、接口分配和参考图。"));
        stack.Children.Add(CreateSectionCard("基本信息", new View[]
        {
            CreateLabeledInput("任务标题", titleEntry),
            CreateLabeledInput("提示词", promptEditor),
            CreateTwoColumnRow(
                CreateLabeledInput("尺寸", sizePicker),
                CreateLabeledInput("质量", qualityPicker)),
            customSizeField,
            CreateLabeledInput("生成模式", modePicker)
        }));
        stack.Children.Add(CreateSectionCard("提示词扩写", new View[]
        {
            CreateTwoColumnRow(expandButton, pasteExpandedButton),
            expandStatus,
            reasoningTitle,
            reasoningField,
            CreateSectionTitleLabel("扩写结果"),
            WrapInput(expandedPromptEditor)
        }));
        stack.Children.Add(CreateSectionCard("接口分配", new View[]
        {
            CreateLabeledInput("分配模式", assignmentPicker),
            CreateLabeledInput("指定接口", connectionPicker),
            CreateCaptionLabel("自动分配会使用当前空闲接口；手动指定会等待对应接口空闲。")
        }));
        stack.Children.Add(CreateSectionCard("参考图", new View[]
        {
            referenceSummary,
            referenceModeWarning,
            CreateTwoColumnRow(pickReferenceButton, clearReferenceButton),
            referenceList,
            CreateLabeledInput("参考图 URL", referenceUrlEditor, "每行一个图片 URL")
        }));
        stack.Children.Add(saveButton);

        _ = LoadExistingReferencesAsync().ContinueWith(_ =>
        {
            MainThread.BeginInvokeOnMainThread(RefreshReferences);
        });
        RefreshReferences();

        return new ContentPage
        {
            Title = isEditing ? "编辑任务" : isRerunDraft ? "重新生图" : "创建任务",
            BackgroundColor = ResourceColor("PageBg"),
            Content = new ScrollView { Content = stack }
        };
    }

    private async Task<Image2QueuedTask> CreateTaskFromValuesAsync(
        string taskId,
        string? titleText,
        string? promptText,
        WorkMode mode,
        string size,
        string quality,
        bool manual,
        Image2ConnectionProfile? selectedConnection,
        IEnumerable<ReferenceImageItem> localReferences,
        List<string> referenceUrls)
    {
        var title = string.IsNullOrWhiteSpace(titleText)
            ? $"任务 {DateTime.Now:HHmmss}"
            : titleText.Trim();

        var references = new List<Image2ReferenceAsset>();
        foreach (var item in localReferences)
        {
            references.Add(item.Asset ?? await _fileService.CopyReferenceAsync(item.File, taskId));
        }

        var hasMultipleReferenceImages = references.Count + referenceUrls.Count > 1;

        return new Image2QueuedTask
        {
            Id = taskId,
            VersionGroupId = taskId,
            Title = title,
            Prompt = promptText?.Trim() ?? string.Empty,
            Mode = mode.ToString(),
            Size = size,
            Quality = quality,
            ResponseFormat = _settings.ResponseFormat,
            OutputFormat = _settings.OutputFormat,
            Background = _settings.Background,
            Transparent = _settings.Transparent,
            UseAsyncTask = _settings.UseAsyncTask || hasMultipleReferenceImages,
            UseChatEndpoint = mode == WorkMode.Chat || mode == WorkMode.Edit,
            StreamChat = _settings.StreamChat || mode == WorkMode.Edit,
            ReferenceImages = references,
            ReferenceImageUrls = referenceUrls,
            AssignmentMode = manual ? Image2ConnectionAssignmentMode.Manual : Image2ConnectionAssignmentMode.Auto,
            RequestedConnectionId = manual ? selectedConnection?.Id : null,
            RequestedConnectionTitle = manual ? selectedConnection?.Title : null,
            Status = Image2TaskStatus.Queued,
            StatusDetail = manual && selectedConnection is not null
                ? $"等待指定接口：{selectedConnection.Title}"
                : "等待调度",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
    }

    private ScrollView BuildQueueDetailsView()
    {
        var stack = new VerticalStackLayout
        {
            Padding = PagePadding(),
            Spacing = 10
        };
        stack.Children.Add(CreatePageHeader("队列", BuildQueuePageSubtitle()));

        if (_state.Queue.Count == 0)
        {
            stack.Children.Add(CreateSectionCard("暂无任务", new View[]
            {
                CreateCaptionLabel("创建任务后会出现在这里。")
            }));
        }
        else
        {
            foreach (var task in _state.Queue)
            {
                stack.Children.Add(CreateQueueDetailCard(task, includeActions: true));
            }
        }

        return new ScrollView { Content = stack };
    }

    private ScrollView BuildHistoryDetailsView()
    {
        var stack = new VerticalStackLayout
        {
            Padding = PagePadding(),
            Spacing = 10
        };
        var groups = GetHistoryGroups().ToList();
        stack.Children.Add(CreatePageHeader("历史", $"{groups.Count} 个主任务 / {_state.History.Count} 个版本"));

        if (groups.Count == 0)
        {
            stack.Children.Add(CreateSectionCard("暂无历史", new View[]
            {
                CreateCaptionLabel("任务完成或失败后会自动归档。")
            }));
        }
        else
        {
            foreach (var group in groups)
            {
                var latest = group.OrderByDescending(item => item.Version).ThenByDescending(item => item.CreatedAt).First();
                stack.Children.Add(CreateHistoryGroupCard(latest, group.Count()));
            }
        }

        return new ScrollView { Content = stack };
    }

    private ContentPage CreateTaskDetailPage(Image2QueuedTask task)
    {
        return new ContentPage
        {
            Title = task.Title,
            BackgroundColor = ResourceColor("PageBg"),
            Content = new ScrollView
            {
                Content = CreateTaskDetailContent(task, includeQueueActions: true)
            }
        };
    }

    private ContentPage CreateHistoryDetailPage(Image2HistoryEntry history)
    {
        var detailContent = new ScrollView
        {
            Content = CreateHistoryDetailContent(history)
        };
        TrackHistoryDetailView(history.Id, detailContent);

        return new ContentPage
        {
            Title = $"{history.Title} v{history.Version}",
            BackgroundColor = ResourceColor("PageBg"),
            Content = detailContent
        };
    }

    private VerticalStackLayout CreateTaskDetailContent(Image2QueuedTask task, bool includeQueueActions)
    {
        var stack = new VerticalStackLayout
        {
            Padding = PagePadding(),
            Spacing = 10
        };
        stack.Children.Add(CreatePageHeader("队列任务", $"{task.Title} / {GetStatusText(task)}"));
        stack.Children.Add(CreateStatusCard(task));
        stack.Children.Add(CreateSectionCard("提示词", new View[] { CreateBodyText(task.Prompt, 14) }));
        stack.Children.Add(CreateReferenceSection(task.ReferenceImages, task.ReferenceImageUrls));
        stack.Children.Add(CreateResultSection(task.ImagePath, task.ImageUrl, task.OutputFolder, task.Status == Image2TaskStatus.Completed, task.Title, task.OutputFormat));
        stack.Children.Add(CreateSectionCard("模型文本", new View[] { CreateLongTextPreview("模型文本", task.AssistantText) }));
        stack.Children.Add(CreateSectionCard("JSON", new View[] { CreateLongTextPreview("JSON", task.RawJson) }));

        if (includeQueueActions)
        {
            stack.Children.Add(CreateSectionCard("任务操作", new View[]
            {
                CreateTaskActionRow(task)
            }));
        }

        return stack;
    }

    private VerticalStackLayout CreateHistoryDetailContent(Image2HistoryEntry history)
    {
        var versions = _state.History
            .Where(item => GetHistoryGroupId(item) == GetHistoryGroupId(history))
            .OrderByDescending(item => item.Version)
            .ThenByDescending(item => item.CreatedAt)
            .ToList();

        var stack = new VerticalStackLayout
        {
            Padding = PagePadding(),
            Spacing = 10
        };
        stack.Children.Add(CreatePageHeader("历史详情", $"{history.Title} v{history.Version} / {history.StatusText}"));
        stack.Children.Add(CreateHistoryStatusCard(history, versions.Count));
        stack.Children.Add(CreateSectionCard("提示词", new View[] { CreateBodyText(history.Prompt, 14) }));
        stack.Children.Add(CreateReferenceSection(history.ReferenceImages, history.ReferenceImageUrls));
        stack.Children.Add(CreateResultSection(history.ImagePath, history.ImageUrl, history.OutputFolder, history.Succeeded, history.Title, history.OutputFormat, history.Id));
        stack.Children.Add(CreateSectionCard("模型文本", new View[] { CreateLongTextPreview("模型文本", history.AssistantText) }));
        stack.Children.Add(CreateSectionCard("JSON", new View[] { CreateLongTextPreview("JSON", history.RawJson) }));

        var rerun = CreateActionButton("基于此版本重新生图", ResourceColor("Primary"), ResourceColor("White"));
        rerun.BindingContext = history;
        rerun.Clicked += OnHistoryRerunClicked;
        var delete = CreateActionButton("删除历史和文件夹", ResourceColor("ControlBg"), ResourceColor("Danger"));
        delete.BindingContext = history;
        delete.Clicked += OnHistoryDeleteClicked;
        stack.Children.Add(CreateSectionCard("重新生图", new View[]
        {
            CreateCaptionLabel("会创建一个新队列任务，并归入这个主任务的新版本。"),
            rerun,
            delete
        }));

        var oldVersionViews = new List<View>();
        foreach (var version in versions.Where(item => item.Id != history.Id))
        {
            oldVersionViews.Add(CreateHistoryVersionCard(version));
        }

        if (oldVersionViews.Count == 0)
        {
            oldVersionViews.Add(CreateCaptionLabel("暂无旧版本。"));
        }

        stack.Children.Add(CreateSectionCard("旧版本", oldVersionViews));
        return stack;
    }

    private View CreateStatusCard(Image2QueuedTask task)
    {
        return CreateSectionCard("状态", new View[]
        {
            CreateKeyValueGrid(new (string, string)[]
            {
                ("状态", GetStatusText(task)),
                ("接口", task.AssignedConnectionTitle ?? task.RequestedConnectionTitle ?? "自动分配"),
                ("分配", GetAssignmentText(task)),
                ("模式", ModeText(task.Mode)),
                ("尺寸", task.Size),
                ("质量", task.Quality),
                ("参考图", $"{task.ReferenceImages.Count + task.ReferenceImageUrls.Count} 张"),
                ("文件夹", string.IsNullOrWhiteSpace(task.OutputFolder) ? "尚未创建" : task.OutputFolder),
                ("更新时间", task.UpdatedAt.ToString("MM-dd HH:mm"))
            })
        });
    }

    private View CreateHistoryStatusCard(Image2HistoryEntry history, int versionCount)
    {
        return CreateSectionCard("状态", new View[]
        {
            CreateKeyValueGrid(new (string, string)[]
            {
                ("状态", history.StatusText),
                ("版本", $"v{history.Version} / 共 {versionCount} 个版本"),
                ("接口", history.AssignedConnectionTitle ?? history.RequestedConnectionTitle ?? "未记录"),
                ("分配", history.AssignmentMode == Image2ConnectionAssignmentMode.Auto ? "自动分配" : $"指定 {history.RequestedConnectionTitle ?? "未选择"}"),
                ("模式", ModeText(history.Mode)),
                ("尺寸", history.Size),
                ("质量", history.Quality),
                ("参考图", $"{history.ReferenceImages.Count + history.ReferenceImageUrls.Count} 张"),
                ("文件夹", string.IsNullOrWhiteSpace(history.OutputFolder) ? "未记录" : history.OutputFolder),
                ("时间", history.CreatedAt.ToString("MM-dd HH:mm"))
            })
        });
    }

    private View CreateReferenceSection(IReadOnlyList<Image2ReferenceAsset> images, IReadOnlyList<string> urls)
    {
        var views = new List<View>();
        if (images.Count == 0 && urls.Count == 0)
        {
            views.Add(CreateCaptionLabel("没有参考图。"));
        }
        else
        {
            foreach (var image in images)
            {
                views.Add(CreateAssetReferenceDetailRow(image));
            }

            foreach (var url in urls)
            {
                views.Add(CreateReferenceDetailRow(CreateUrlPreviewSized(url, 68), "URL", url));
            }
        }

        return CreateSectionCard("参考图", views);
    }

    private View CreateResultSection(string? imagePath, string? imageUrl, string? outputFolder, bool succeeded, string title, string outputFormat, string? historyId = null)
    {
        var views = new List<View>();
        var image = CreateResultImage(imagePath, imageUrl);
        if (image is not null)
        {
            views.Add(image);
        }
        else
        {
            views.Add(CreateCaptionLabel(succeeded ? "结果已记录，但当前没有可预览的图片文件。" : "任务没有可预览的结果图。"));
        }

        views.Add(CreateKeyValueGrid(new (string, string)[]
        {
            ("图片路径", string.IsNullOrWhiteSpace(imagePath) ? "未保存本地图片" : imagePath),
            ("图片 URL", string.IsNullOrWhiteSpace(imageUrl) ? "无" : imageUrl),
            ("任务文件夹", string.IsNullOrWhiteSpace(outputFolder) ? "尚未创建" : outputFolder)
        }));

        var actions = new FlexLayout
        {
            Direction = FlexDirection.Row,
            Wrap = FlexWrap.Wrap,
            AlignItems = FlexAlignItems.Center
        };
        var save = CreateSmallButton("保存图片", string.Empty, OnManualSaveResultImageClicked);
        save.CommandParameter = new ResultImageSaveRequest(imagePath, imageUrl, title, outputFormat);
        save.WidthRequest = 82;
        save.MinimumWidthRequest = 82;
        save.IsEnabled = !string.IsNullOrWhiteSpace(imagePath) || !string.IsNullOrWhiteSpace(imageUrl);
        var open = CreateSmallButton("打开文件夹", outputFolder ?? string.Empty, OnOpenSpecificFolderClicked);
        open.WidthRequest = 82;
        open.MinimumWidthRequest = 82;
        open.IsEnabled = !string.IsNullOrWhiteSpace(outputFolder);
        save.Margin = new Thickness(0, 0, 8, 8);
        open.Margin = new Thickness(0, 0, 8, 8);
        actions.Children.Add(save);
        actions.Children.Add(open);
        if (!string.IsNullOrWhiteSpace(historyId))
        {
            var createFolder = CreateSmallButton("创建文件夹", string.Empty, OnCreateHistoryFolderClicked);
            createFolder.CommandParameter = new HistoryFolderCreateRequest(historyId);
            createFolder.WidthRequest = 98;
            createFolder.MinimumWidthRequest = 98;
            createFolder.Margin = new Thickness(0, 0, 8, 8);
            actions.Children.Add(createFolder);
        }
        views.Add(actions);

        return CreateSectionCard("生图结果", views);
    }

    private View? CreateResultImage(string? imagePath, string? imageUrl)
    {
        ImageSource? source = null;
        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            var path = imagePath;
            source = ImageSource.FromStream(() => File.OpenRead(path));
        }
        else if (!string.IsNullOrWhiteSpace(imageUrl) && Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
        {
            source = ImageSource.FromUri(uri);
        }

        if (source is null)
        {
            return null;
        }

        return new Border
        {
            Padding = 0,
            StrokeThickness = 0,
            BackgroundColor = ResourceColor("PreviewBg"),
            HeightRequest = DeviceInfo.Platform == DevicePlatform.Android ? 320 : 420,
            Content = new Image
            {
                Source = source,
                Aspect = Aspect.AspectFit,
                BackgroundColor = ResourceColor("PreviewBg")
            }
        };
    }

    private View CreateQueueDetailCard(Image2QueuedTask task, bool includeActions)
    {
        var accent = StatusAccent(task.Status);
        var content = new VerticalStackLayout { Spacing = 8 };
        content.Children.Add(CreateCardTitle(task.Title, accent));
        content.Children.Add(CreateStatusLine(GetStatusText(task), accent));
        content.Children.Add(CreateCaptionLabel($"{ModeText(task.Mode)} / {task.Size} / {task.Quality} / 参考图 {task.ReferenceImages.Count + task.ReferenceImageUrls.Count} 张", LineBreakMode.TailTruncation));
        content.Children.Add(CreateCaptionLabel($"接口：{task.AssignedConnectionTitle ?? task.RequestedConnectionTitle ?? "自动分配"} / {GetAssignmentText(task)}", LineBreakMode.TailTruncation));

        if (task.CanEdit && task.AssignmentMode == Image2ConnectionAssignmentMode.Manual)
        {
            content.Children.Add(CreateQueueConnectionPicker(task));
        }

        if (includeActions)
        {
            content.Children.Add(CreateTaskActionRow(task));
        }

        var card = CreateClickableCard(content, accent);
        card.BindingContext = task;
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            _homeDetailTaskId = task.Id;
            _homeDetailHistoryId = null;
            OnQueueTaskOpenDetailsClicked(card, EventArgs.Empty);
        };
        card.GestureRecognizers.Add(tap);
        return card;
    }

    private View CreateHistoryGroupCard(Image2HistoryEntry latest, int versionCount)
    {
        var accent = latest.Succeeded ? ResourceColor("Success") : ResourceColor("Danger");
        var cardAccent = latest.Succeeded ? accent : ResourceColor("BorderSoft");
        var content = new VerticalStackLayout { Spacing = 8 };
        content.Children.Add(CreateCardTitle(latest.Title, accent));
        content.Children.Add(CreateStatusLine($"{latest.StatusText} / {versionCount} 个版本 / 最新 v{latest.Version}", accent));
        content.Children.Add(CreateCaptionLabel($"{latest.Size} / {latest.Quality} / {latest.AssignedConnectionTitle ?? "未记录接口"} / {latest.CreatedAt:MM-dd HH:mm}", LineBreakMode.TailTruncation));

        var row = new HorizontalStackLayout { Spacing = 8 };
        var open = CreateSmallButton("详情", latest.Id, OnHistoryOpenDetailsClicked);
        open.WidthRequest = 54;
        open.MinimumWidthRequest = 54;
        var rerun = CreateSmallButton("重生", latest.Id, OnHistoryRerunClicked);
        rerun.WidthRequest = 54;
        rerun.MinimumWidthRequest = 54;
        var delete = CreateSmallButton("删除", latest.Id, OnHistoryDeleteClicked);
        delete.WidthRequest = 54;
        delete.MinimumWidthRequest = 54;
        row.Children.Add(open);
        row.Children.Add(rerun);
        row.Children.Add(delete);
        content.Children.Add(row);

        var card = CreateClickableCard(content, cardAccent);
        card.BindingContext = latest;
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            _homeDetailHistoryId = latest.Id;
            _homeDetailTaskId = null;
            OnHistoryOpenDetailsClicked(card, EventArgs.Empty);
        };
        card.GestureRecognizers.Add(tap);
        return card;
    }

    private View CreateHistoryVersionCard(Image2HistoryEntry history)
    {
        var accent = history.Succeeded ? ResourceColor("Success") : ResourceColor("Danger");
        var content = new VerticalStackLayout { Spacing = 6 };
        content.Children.Add(CreateCardTitle($"v{history.Version} · {history.StatusText}", accent));
        content.Children.Add(CreateCaptionLabel($"{history.Size} / {history.Quality} / {history.CreatedAt:MM-dd HH:mm}"));
        if (!history.Succeeded && !string.IsNullOrWhiteSpace(history.ErrorMessage))
        {
            content.Children.Add(CreateCaptionLabel(history.ErrorMessage, LineBreakMode.TailTruncation));
        }

        var card = CreateClickableCard(content, accent);
        card.BindingContext = history;
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => OnHistoryOpenDetailsClicked(card, EventArgs.Empty);
        card.GestureRecognizers.Add(tap);
        return card;
    }

    private View CreateTaskActionRow(Image2QueuedTask task)
    {
        var wrap = new FlexLayout
        {
            Direction = FlexDirection.Row,
            Wrap = FlexWrap.Wrap,
            AlignItems = FlexAlignItems.Center,
            JustifyContent = FlexJustify.Start,
            Margin = new Thickness(0)
        };

        void Add(Button button)
        {
            button.Margin = new Thickness(0, 0, 8, 8);
            wrap.Children.Add(button);
        }

        if (task.Status == Image2TaskStatus.Running)
        {
            var stopButton = CreateSmallButton("停止", task.Id, OnQueueTaskStopClicked);
            stopButton.WidthRequest = 54;
            stopButton.MinimumWidthRequest = 54;
            Add(stopButton);
            return wrap;
        }

        if (task.CanEdit)
        {
            var editButton = CreateSmallButton("编辑", task.Id, OnQueueTaskEditClicked);
            editButton.WidthRequest = 54;
            editButton.MinimumWidthRequest = 54;
            Add(editButton);

            var upButton = CreateSmallButton("上移", task.Id, OnQueueTaskMoveUpClicked);
            upButton.WidthRequest = 54;
            upButton.MinimumWidthRequest = 54;
            Add(upButton);

            var downButton = CreateSmallButton("下移", task.Id, OnQueueTaskMoveDownClicked);
            downButton.WidthRequest = 54;
            downButton.MinimumWidthRequest = 54;
            Add(downButton);

            var assignButton = CreateSmallButton(task.AssignmentMode == Image2ConnectionAssignmentMode.Auto ? "自动" : "手动", task.Id, OnQueueAssignmentModeClicked);
            assignButton.WidthRequest = 54;
            assignButton.MinimumWidthRequest = 54;
            Add(assignButton);
        }

        if (task.Status is Image2TaskStatus.Failed or Image2TaskStatus.Stopped or Image2TaskStatus.Completed)
        {
            var retryButton = CreateSmallButton("重试", task.Id, OnQueueTaskRetryClicked);
            retryButton.WidthRequest = 54;
            retryButton.MinimumWidthRequest = 54;
            Add(retryButton);
        }

        var deleteButton = CreateSmallButton("删除", task.Id, OnQueueTaskDeleteClicked);
        deleteButton.WidthRequest = 54;
        deleteButton.MinimumWidthRequest = 54;
        Add(deleteButton);

        return wrap;
    }

    private void RefreshOpenDetailPages()
    {
        if (_queueDetailsView is not null)
        {
            _queueDetailsView.Content = BuildQueueDetailsView().Content;
        }

        if (_historyDetailsView is not null)
        {
            _historyDetailsView.Content = BuildHistoryDetailsView().Content;
        }

        RenderHomeDetail();
    }

    private void TrackHistoryDetailView(string historyId, ScrollView view)
    {
        if (!_historyDetailViews.TryGetValue(historyId, out var views))
        {
            views = new List<WeakReference<ScrollView>>();
            _historyDetailViews[historyId] = views;
        }

        views.RemoveAll(reference => !reference.TryGetTarget(out _));
        views.Add(new WeakReference<ScrollView>(view));
    }

    private void RefreshHistoryDetailViews(string historyId)
    {
        if (!_historyDetailViews.TryGetValue(historyId, out var views))
        {
            return;
        }

        var history = _state.History.FirstOrDefault(item => item.Id == historyId);
        if (history is null)
        {
            _historyDetailViews.Remove(historyId);
            return;
        }

        views.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in views.ToArray())
        {
            if (reference.TryGetTarget(out var view))
            {
                var scrollY = view.ScrollY;
                view.Content = CreateHistoryDetailContent(history);
                view.Dispatcher.Dispatch(async () => await view.ScrollToAsync(0, scrollY, false));
            }
        }

        if (views.Count == 0)
        {
            _historyDetailViews.Remove(historyId);
        }
    }

    private void RenderHomeDetail()
    {
        HomeDetailStack.Children.Clear();
        HomeDetailStack.Children.Add(CreateSectionTitleLabel("任务详情"));

        if (!string.IsNullOrWhiteSpace(_homeDetailTaskId) &&
            _state.Queue.FirstOrDefault(task => task.Id == _homeDetailTaskId) is { } task)
        {
            HomeDetailStack.Children.Add(CreateCardTitle(task.Title, StatusAccent(task.Status)));
            HomeDetailStack.Children.Add(CreateCaptionLabel($"{GetStatusText(task)} / {task.Size} / {task.Quality}"));
            HomeDetailStack.Children.Add(CreateCaptionLabel($"接口：{task.AssignedConnectionTitle ?? task.RequestedConnectionTitle ?? "自动分配"}"));
            HomeDetailStack.Children.Add(CreateCaptionLabel($"参考图 {task.ReferenceImages.Count + task.ReferenceImageUrls.Count} 张 / {ModeText(task.Mode)}"));
            var details = CreateSmallButton("详情", task.Id, OnQueueTaskOpenDetailsClicked);
            details.WidthRequest = 58;
            details.MinimumWidthRequest = 58;
            HomeDetailStack.Children.Add(details);
            HomeDetailStack.Children.Add(CreateTaskActionRow(task));
            return;
        }

        if (!string.IsNullOrWhiteSpace(_homeDetailHistoryId) &&
            _state.History.FirstOrDefault(history => history.Id == _homeDetailHistoryId) is { } history)
        {
            HomeDetailStack.Children.Add(CreateCardTitle($"{history.Title} v{history.Version}", history.Succeeded ? ResourceColor("Success") : ResourceColor("Danger")));
            HomeDetailStack.Children.Add(CreateCaptionLabel($"{history.StatusText} / {history.Size} / {history.Quality}"));
            HomeDetailStack.Children.Add(CreateCaptionLabel($"接口：{history.AssignedConnectionTitle ?? "未记录"} / 版本组 {GetVersionCount(history)} 个版本"));
            var details = CreateSmallButton("详情", history.Id, OnHistoryOpenDetailsClicked);
            details.WidthRequest = 58;
            details.MinimumWidthRequest = 58;
            var rerun = CreateSmallButton("重生", history.Id, OnHistoryRerunClicked);
            rerun.WidthRequest = 58;
            rerun.MinimumWidthRequest = 58;
            var delete = CreateSmallButton("删除", history.Id, OnHistoryDeleteClicked);
            delete.WidthRequest = 58;
            delete.MinimumWidthRequest = 58;
            HomeDetailStack.Children.Add(CreateThreeButtonRow(details, rerun, delete));
            return;
        }

        HomeDetailStack.Children.Add(CreateCaptionLabel("选择队列任务或历史记录后在这里查看摘要。"));
    }

    private Entry CreatePlainEntry(string placeholder, string? text)
    {
        return new Entry
        {
            Placeholder = placeholder,
            Text = text ?? string.Empty
        };
    }

    private Editor CreatePlainEditor(string placeholder, string? text, double height)
    {
        var editor = new Editor
        {
            Placeholder = placeholder,
            Text = text ?? string.Empty,
            MinimumHeightRequest = height
        };

        if (DeviceInfo.Platform == DevicePlatform.Android)
        {
            editor.AutoSize = EditorAutoSizeOption.TextChanges;
        }
        else
        {
            editor.AutoSize = EditorAutoSizeOption.Disabled;
            editor.HeightRequest = height;
        }

        return editor;
    }

    private Picker CreatePicker(string title)
    {
        return new Picker
        {
            Title = title,
            MinimumHeightRequest = 46
        };
    }

    private Button CreateActionButton(string text, Color background, Color foreground)
    {
        var button = new Button
        {
            Text = text,
            BackgroundColor = background,
            TextColor = foreground,
            FontSize = 14,
            MinimumHeightRequest = DeviceInfo.Platform == DevicePlatform.Android ? 44 : 48
        };
        button.Pressed += OnButtonPressed;
        button.Released += OnButtonReleased;
        return button;
    }

    private Button CreateOutlinePrimaryButton(string text)
    {
        var button = new Button
        {
            Text = text,
            BackgroundColor = ResourceColor("PanelRaised"),
            TextColor = ResourceColor("Primary"),
            BorderColor = ResourceColor("Primary"),
            BorderWidth = 1,
            MinimumHeightRequest = DeviceInfo.Platform == DevicePlatform.Android ? 50 : 56,
            FontSize = DeviceInfo.Platform == DevicePlatform.Android ? 15 : 16
        };
        button.Pressed += OnButtonPressed;
        button.Released += OnButtonReleased;
        return button;
    }

    private View CreatePageHeader(string title, string subtitle)
    {
        var stack = new VerticalStackLayout { Spacing = 5 };
        stack.Children.Add(new Label
        {
            Text = title,
            FontFamily = "OpenSansSemibold",
            FontSize = DeviceInfo.Platform == DevicePlatform.Android ? 20 : 28,
            TextColor = ResourceColor("TextStrong"),
            LineBreakMode = LineBreakMode.WordWrap
        });
        stack.Children.Add(CreateCaptionLabel(subtitle));
        return stack;
    }

    private View CreateSectionCard(string title, IEnumerable<View> children)
    {
        var stack = new VerticalStackLayout { Spacing = DeviceInfo.Platform == DevicePlatform.Android ? 8 : 10 };
        stack.Children.Add(CreateSectionTitleLabel(title));
        foreach (var child in children)
        {
            stack.Children.Add(child);
        }

        return new Border
        {
            Padding = DeviceInfo.Platform == DevicePlatform.Android ? 12 : 14,
            Content = stack
        };
    }

    private Label CreateSectionTitleLabel(string text)
    {
        return new Label
        {
            Text = text,
            Style = Application.Current?.Resources.TryGetValue("SectionTitle", out var style) == true ? style as Style : null,
            FontFamily = "OpenSansSemibold",
            FontSize = DeviceInfo.Platform == DevicePlatform.Android ? 15 : 16,
            TextColor = ResourceColor("TextStrong")
        };
    }

    private View CreateTwoColumnRow(View first, View second)
    {
        if (DeviceInfo.Platform == DevicePlatform.Android && Width > 0 && Width < 360)
        {
            var stack = new VerticalStackLayout { Spacing = 8 };
            stack.Children.Add(first);
            stack.Children.Add(second);
            return stack;
        }

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            },
            ColumnSpacing = 10
        };
        grid.Add(first, 0, 0);
        grid.Add(second, 1, 0);
        return grid;
    }

    private View CreateThreeButtonRow(View first, View second, View third)
    {
        var layout = new FlexLayout
        {
            Direction = FlexDirection.Row,
            Wrap = FlexWrap.Wrap,
            AlignItems = FlexAlignItems.Center,
            JustifyContent = FlexJustify.Start
        };
        foreach (var child in new[] { first, second, third })
        {
            child.Margin = new Thickness(0, 0, 8, 8);
            layout.Children.Add(child);
        }

        return layout;
    }

    private View WrapInput(View input)
    {
        return new Border
        {
            Padding = DeviceInfo.Platform == DevicePlatform.Android
                ? new Thickness(11, 2)
                : new Thickness(12, 3),
            BackgroundColor = ResourceColor("PanelAlt"),
            StrokeThickness = 1,
            Stroke = new SolidColorBrush(ResourceColor("BorderSoft")),
            Content = input
        };
    }

    private View CreateLabeledInput(string label, View input, string? helper = null)
    {
        var stack = new VerticalStackLayout { Spacing = DeviceInfo.Platform == DevicePlatform.Android ? 5 : 6 };
        stack.Children.Add(new Label
        {
            Text = label,
            FontFamily = "OpenSansSemibold",
            FontSize = DeviceInfo.Platform == DevicePlatform.Android ? 12 : 13,
            TextColor = ResourceColor("TextStrong")
        });
        stack.Children.Add(WrapInput(input));
        if (!string.IsNullOrWhiteSpace(helper))
        {
            stack.Children.Add(CreateCaptionLabel(helper));
        }

        return stack;
    }

    private Border CreateProminentNotice(string title, string text)
    {
        var content = new VerticalStackLayout { Spacing = 3 };
        content.Children.Add(new Label
        {
            Text = title,
            FontFamily = "OpenSansSemibold",
            FontSize = 13,
            TextColor = ResourceColor("WarningText"),
            LineBreakMode = LineBreakMode.WordWrap
        });
        content.Children.Add(new Label
        {
            Text = text,
            FontFamily = "OpenSansRegular",
            FontSize = 12,
            TextColor = ResourceColor("WarningText"),
            LineBreakMode = LineBreakMode.WordWrap
        });

        return new Border
        {
            Padding = new Thickness(10, 8),
            BackgroundColor = ResourceColor("WarningSoft"),
            StrokeThickness = 1,
            Stroke = new SolidColorBrush(ResourceColor("Warning")),
            Content = content
        };
    }

    private View CreateImagePreview(byte[] bytes, double size)
    {
        return new Border
        {
            Padding = 0,
            WidthRequest = size,
            HeightRequest = size,
            StrokeThickness = 0,
            BackgroundColor = ResourceColor("PreviewBg"),
            Content = new Image
            {
                Source = ImageSource.FromStream(() => new MemoryStream(bytes)),
                Aspect = Aspect.AspectFill,
                WidthRequest = size,
                HeightRequest = size
            }
        };
    }

    private View CreateUrlPreviewSized(string url, double size)
    {
        View content;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            content = new Image
            {
                Source = ImageSource.FromUri(uri),
                Aspect = Aspect.AspectFill,
                WidthRequest = size,
                HeightRequest = size
            };
        }
        else
        {
            content = new Label
            {
                Text = "URL",
                FontFamily = "OpenSansSemibold",
                FontSize = 12,
                TextColor = ResourceColor("Primary"),
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center
            };
        }

        return new Border
        {
            Padding = 0,
            WidthRequest = size,
            HeightRequest = size,
            StrokeThickness = 0,
            BackgroundColor = ResourceColor("PreviewBg"),
            Content = content
        };
    }

    private View CreateEditorReferenceRow(View preview, string label, string text, Action remove)
    {
        var title = new Label
        {
            Text = label,
            FontFamily = "OpenSansSemibold",
            FontSize = 13,
            TextColor = ResourceColor("TextStrong")
        };
        var caption = CreateCaptionLabel(text, LineBreakMode.TailTruncation);
        var textStack = new VerticalStackLayout
        {
            Spacing = 3,
            VerticalOptions = LayoutOptions.Center
        };
        textStack.Children.Add(title);
        textStack.Children.Add(caption);

        var delete = CreateActionButton("删除", ResourceColor("ControlBg"), ResourceColor("TextMuted"));
        delete.WidthRequest = 56;
        delete.MinimumWidthRequest = 56;
        delete.Clicked += (_, _) => remove();

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(64)),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(62))
            },
            ColumnSpacing = 10
        };
        grid.Add(preview, 0, 0);
        grid.Add(textStack, 1, 0);
        grid.Add(delete, 2, 0);
        return grid;
    }

    private View CreateAssetReferenceDetailRow(Image2ReferenceAsset asset)
    {
        View preview;
        if (!string.IsNullOrWhiteSpace(asset.Path) && File.Exists(asset.Path))
        {
            var path = asset.Path;
            preview = new Border
            {
                Padding = 0,
                WidthRequest = 68,
                HeightRequest = 68,
                StrokeThickness = 0,
                BackgroundColor = ResourceColor("PreviewBg"),
                Content = new Image
                {
                    Source = ImageSource.FromStream(() => File.OpenRead(path)),
                    Aspect = Aspect.AspectFill
                }
            };
        }
        else
        {
            preview = CreateUrlPreviewSized("local", 68);
        }

        return CreateReferenceDetailRow(preview, "本地", asset.FileName);
    }

    private View CreateReferenceDetailRow(View preview, string label, string text)
    {
        var stack = new VerticalStackLayout
        {
            Spacing = 3,
            VerticalOptions = LayoutOptions.Center
        };
        stack.Children.Add(new Label
        {
            Text = label,
            FontFamily = "OpenSansSemibold",
            FontSize = 13,
            TextColor = ResourceColor("TextStrong")
        });
        stack.Children.Add(CreateCaptionLabel(text, LineBreakMode.TailTruncation));

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(68)),
                new ColumnDefinition(GridLength.Star)
            },
            ColumnSpacing = 10
        };
        grid.Add(preview, 0, 0);
        grid.Add(stack, 1, 0);
        return grid;
    }

    private Label CreateBodyText(string? text, double fontSize)
    {
        return new Label
        {
            Text = string.IsNullOrWhiteSpace(text) ? "未填写" : text,
            FontSize = fontSize,
            TextColor = ResourceColor("TextStrong"),
            LineBreakMode = LineBreakMode.WordWrap
        };
    }

    private View CreateLongTextPreview(string title, string? text)
    {
        var value = string.IsNullOrWhiteSpace(text) ? "无" : text.Trim();
        var preview = value.Length > 520 ? $"{value[..520]}..." : value;
        var stack = new VerticalStackLayout { Spacing = 10 };
        stack.Children.Add(new Border
        {
            Padding = 12,
            BackgroundColor = ResourceColor("PanelAlt"),
            Content = new Label
            {
                Text = preview,
                FontSize = 12,
                FontFamily = "OpenSansRegular",
                TextColor = ResourceColor("TextStrong"),
                LineBreakMode = LineBreakMode.WordWrap
            }
        });

        var row = new HorizontalStackLayout { Spacing = 8 };
        var openButton = CreateSmallButton("完整", Guid.NewGuid().ToString("N"), (_, _) => OpenLongTextPage(title, value));
        openButton.WidthRequest = 58;
        openButton.MinimumWidthRequest = 58;
        var copyButton = CreateSmallButton("复制", Guid.NewGuid().ToString("N"), async (_, _) =>
        {
            await Clipboard.Default.SetTextAsync(value);
            SetStatus($"{title} 已复制");
        });
        copyButton.WidthRequest = 58;
        copyButton.MinimumWidthRequest = 58;
        row.Children.Add(openButton);
        row.Children.Add(copyButton);
        stack.Children.Add(row);

        return stack;
    }

    private async void OpenLongTextPage(string title, string text)
    {
        var editor = new Editor
        {
            Text = text,
            IsReadOnly = true,
            AutoSize = EditorAutoSizeOption.TextChanges,
            FontSize = 12,
            FontFamily = "OpenSansRegular",
            MinimumHeightRequest = DeviceInfo.Platform == DevicePlatform.Android ? 420 : 520
        };

        var copyButton = CreateOutlinePrimaryButton("复制全部");
        copyButton.Clicked += async (_, _) =>
        {
            await Clipboard.Default.SetTextAsync(text);
            SetStatus($"{title} 已复制");
        };

        var stack = new VerticalStackLayout
        {
            Padding = PagePadding(),
            Spacing = 12
        };
        stack.Children.Add(CreatePageHeader(title, "完整内容"));
        stack.Children.Add(copyButton);
        stack.Children.Add(WrapInput(editor));

        await Navigation.PushAsync(new ContentPage
        {
            Title = title,
            BackgroundColor = ResourceColor("PageBg"),
            Content = new ScrollView { Content = stack }
        });
    }

    private View CreateKeyValueGrid(IReadOnlyList<(string Key, string Value)> items)
    {
        var stack = new VerticalStackLayout { Spacing = 7 };
        foreach (var (key, value) in items)
        {
            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(88)),
                    new ColumnDefinition(GridLength.Star)
                },
                ColumnSpacing = 8
            };
            var keyLabel = CreateCaptionLabel(key, LineBreakMode.TailTruncation);
            var valueLabel = new Label
            {
                Text = value,
                FontSize = 13,
                TextColor = ResourceColor("TextStrong"),
                LineBreakMode = LineBreakMode.WordWrap
            };
            grid.Add(keyLabel, 0, 0);
            grid.Add(valueLabel, 1, 0);
            stack.Children.Add(grid);
        }

        return stack;
    }

    private View CreateStatusLine(string text, Color accent)
    {
        return new Border
        {
            Padding = new Thickness(8, 4),
            StrokeThickness = 0,
            BackgroundColor = accent.WithAlpha(0.10f),
            HorizontalOptions = LayoutOptions.Start,
            Content = new Label
            {
                Text = text,
                FontFamily = "OpenSansSemibold",
                FontSize = 12,
                TextColor = accent,
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines = 1
            }
        };
    }

    private View CreateEmptyState(string title, string detail)
    {
        var stack = new VerticalStackLayout { Spacing = 4 };
        stack.Children.Add(new Label
        {
            Text = title,
            FontFamily = "OpenSansSemibold",
            FontSize = 13,
            TextColor = ResourceColor("TextStrong")
        });
        stack.Children.Add(CreateCaptionLabel(detail));

        return new Border
        {
            Padding = new Thickness(12, 10),
            StrokeThickness = 0,
            BackgroundColor = ResourceColor("PanelAlt"),
            Content = stack
        };
    }

    private Border CreateClickableCard(View content, Color accent)
    {
        return new Border
        {
            Padding = new Thickness(12, 10),
            BackgroundColor = ResourceColor("PanelRaised"),
            Stroke = new SolidColorBrush(accent.WithAlpha(0.28f)),
            Content = content
        };
    }

    private Label CreateCardTitle(string text, Color accent)
    {
        return new Label
        {
            Text = string.IsNullOrWhiteSpace(text) ? "未命名任务" : text,
            FontFamily = "OpenSansSemibold",
            FontSize = 14,
            TextColor = accent,
            LineBreakMode = LineBreakMode.TailTruncation
        };
    }

    private Thickness PagePadding()
    {
        return DeviceInfo.Platform == DevicePlatform.Android
            ? new Thickness(14, 16, 14, 26)
            : new Thickness(22);
    }

    private bool UseSplitHomeLayout()
    {
        return Width >= 920;
    }

    private WorkMode ParseWorkMode(string? value)
    {
        return Enum.TryParse<WorkMode>(value, out var mode) ? mode : WorkMode.Chat;
    }

    private WorkMode ModeFromPicker(Picker picker)
    {
        return picker.SelectedIndex switch
        {
            1 => WorkMode.Edit,
            2 => WorkMode.Generate,
            _ => WorkMode.Chat
        };
    }

    private Image2ConnectionProfile? SelectedConnectionFromPicker(Picker picker)
    {
        var index = picker.SelectedIndex;
        return index >= 0 && index < _connections.Count ? _connections[index] : _connections.FirstOrDefault();
    }

    private static string ResolveSizeValue(Picker picker, Entry customSizeEntry)
    {
        if (picker.SelectedItem?.ToString() == "自定义")
        {
            var custom = (customSizeEntry.Text ?? string.Empty).Trim().ToLowerInvariant();
            return IsValidSize(custom) ? custom : "1024x1024";
        }

        return SelectedPickerValue(picker, "1024x1024");
    }

    private static bool IsValidSize(string value)
    {
        var parts = value.Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 &&
               int.TryParse(parts[0], out var width) &&
               int.TryParse(parts[1], out var height) &&
               width >= 64 &&
               height >= 64 &&
               width <= 8192 &&
               height <= 8192;
    }

    private static string ModeText(string? mode)
    {
        return Enum.TryParse<WorkMode>(mode, out var parsed) switch
        {
            true when parsed == WorkMode.Edit => "参考改图",
            true when parsed == WorkMode.Generate => "直连生成",
            true when parsed == WorkMode.DirectEdit => "直连改图",
            _ => "稳定生成"
        };
    }

    private Color StatusAccent(Image2TaskStatus status)
    {
        return status switch
        {
            Image2TaskStatus.Running => ResourceColor("Accent"),
            Image2TaskStatus.Completed => ResourceColor("Success"),
            Image2TaskStatus.Failed => ResourceColor("Danger"),
            Image2TaskStatus.Stopped => ResourceColor("Warning"),
            _ => ResourceColor("Primary")
        };
    }

    private IEnumerable<IGrouping<string, Image2HistoryEntry>> GetHistoryGroups()
    {
        return _state.History
            .GroupBy(GetHistoryGroupId)
            .OrderByDescending(group => group.Max(item => item.CreatedAt));
    }

    private static string GetHistoryGroupId(Image2HistoryEntry history)
    {
        return string.IsNullOrWhiteSpace(history.SourceTaskId) ? history.Id : history.SourceTaskId;
    }

    private static string BuildRerunTitle(string title, int nextVersion)
    {
        var baseTitle = string.IsNullOrWhiteSpace(title) ? "历史任务" : title.Trim();
        var marker = baseTitle.LastIndexOf(" v", StringComparison.OrdinalIgnoreCase);
        if (marker > 0 &&
            int.TryParse(baseTitle[(marker + 2)..], out _))
        {
            baseTitle = baseTitle[..marker].Trim();
        }

        return $"{baseTitle} v{nextVersion}";
    }

    private int GetVersionCount(Image2HistoryEntry history)
    {
        var groupId = GetHistoryGroupId(history);
        return _state.History.Count(item => GetHistoryGroupId(item) == groupId);
    }

    private async Task PersistAndRenderAsync(string? status = null)
    {
        await _taskStore.SaveAsync(_state);
        RenderQueue();
        RenderHistory();
        RefreshOpenDetailPages();
        UpdateQueueSummary();
        if (!string.IsNullOrWhiteSpace(status))
        {
            SetStatus(status);
        }
    }

    private async Task PersistHistoryLibraryAsync()
    {
        foreach (var history in _state.History)
        {
            try
            {
                await _fileService.SaveHistoryManifestAsync(history, _settings);
            }
            catch
            {
                // History manifests are retried on later task completion/import; UI state should stay responsive.
            }
        }
    }

    private void RenderQueue()
    {
        QueueListStack.Children.Clear();
        if (_state.Queue.Count == 0)
        {
            QueueListStack.Children.Add(CreateEmptyState("队列为空", "创建任务后会显示等待接口、运行接口和参数摘要。"));
            return;
        }

        foreach (var task in _state.Queue.Take(5))
        {
            QueueListStack.Children.Add(CreateHomeQueueCard(task));
        }

        if (_state.Queue.Count > 5)
        {
            QueueListStack.Children.Add(CreateCaptionLabel($"还有 {_state.Queue.Count - 5} 个任务，进入队列详情查看。"));
        }
    }

    private View CreateHomeQueueCard(Image2QueuedTask task)
    {
        var accent = StatusAccent(task.Status);
        var content = new VerticalStackLayout { Spacing = 6 };
        content.Children.Add(CreateCardTitle(task.Title, accent));
        content.Children.Add(CreateStatusLine(GetStatusText(task), accent));
        content.Children.Add(CreateCaptionLabel($"{ModeText(task.Mode)} / {task.Size} / {task.Quality}", LineBreakMode.TailTruncation));
        content.Children.Add(CreateCaptionLabel($"接口：{task.AssignedConnectionTitle ?? task.RequestedConnectionTitle ?? "自动分配"} / 参考图 {task.ReferenceImages.Count + task.ReferenceImageUrls.Count} 张", LineBreakMode.TailTruncation));
        var detailsButton = CreateSmallButton("详情", task.Id, OnQueueTaskOpenDetailsClicked);
        detailsButton.WidthRequest = 58;
        detailsButton.MinimumWidthRequest = 58;
        content.Children.Add(detailsButton);

        var card = CreateClickableCard(content, accent);
        card.BindingContext = task;
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            _homeDetailTaskId = task.Id;
            _homeDetailHistoryId = null;
            _selectedTask = task;
            if (UseSplitHomeLayout())
            {
                RenderHomeDetail();
            }
            else
            {
                OnQueueTaskOpenDetailsClicked(card, EventArgs.Empty);
            }
        };
        card.GestureRecognizers.Add(tap);
        return card;
    }

    private View CreateQueueRow(Image2QueuedTask task)
    {
        var accent = task.Status switch
        {
            Image2TaskStatus.Running => ResourceColor("Accent"),
            Image2TaskStatus.Completed => ResourceColor("Success"),
            Image2TaskStatus.Failed => ResourceColor("Danger"),
            Image2TaskStatus.Stopped => ResourceColor("Warning"),
            _ => ResourceColor("Primary")
        };

        var title = new Label
        {
            Text = task.Title,
            FontFamily = "OpenSansSemibold",
            FontSize = 14,
            TextColor = ResourceColor("TextStrong"),
            LineBreakMode = LineBreakMode.TailTruncation
        };

        var detail = new Label
        {
            Text = $"{GetStatusText(task)} · {GetAssignmentText(task)} · {task.ReferenceImages.Count + task.ReferenceImageUrls.Count} 参考图",
            LineBreakMode = LineBreakMode.TailTruncation
        };
        ApplyCaptionStyle(detail);

        var connection = new Label
        {
            Text = $"接口：{task.AssignedConnectionTitle ?? task.RequestedConnectionTitle ?? "自动分配"}",
            FontSize = 12,
            TextColor = accent,
            LineBreakMode = LineBreakMode.TailTruncation
        };

        var textStack = new VerticalStackLayout { Spacing = 3 };
        textStack.Children.Add(title);
        textStack.Children.Add(detail);
        textStack.Children.Add(connection);

        var buttons = new HorizontalStackLayout { Spacing = 6 };
        buttons.Children.Add(CreateSmallButton("详情", task.Id, OnQueueTaskOpenDetailsClicked));
        if (task.Status == Image2TaskStatus.Running)
        {
            buttons.Children.Add(CreateSmallButton("停止", task.Id, OnQueueTaskStopClicked));
        }
        else
        {
            if (task.CanEdit)
            {
                buttons.Children.Add(CreateSmallButton("编辑", task.Id, OnQueueTaskEditClicked));
                buttons.Children.Add(CreateSmallButton("↑", task.Id, OnQueueTaskMoveUpClicked));
                buttons.Children.Add(CreateSmallButton("↓", task.Id, OnQueueTaskMoveDownClicked));
                buttons.Children.Add(CreateSmallButton(task.AssignmentMode == Image2ConnectionAssignmentMode.Auto ? "自动" : "指定", task.Id, OnQueueAssignmentModeClicked));
            }

            if (task.Status is Image2TaskStatus.Failed or Image2TaskStatus.Stopped or Image2TaskStatus.Completed)
            {
                buttons.Children.Add(CreateSmallButton("重试", task.Id, OnQueueTaskRetryClicked));
            }

            buttons.Children.Add(CreateSmallButton("删除", task.Id, OnQueueTaskDeleteClicked));
        }

        var stack = new VerticalStackLayout { Spacing = 10 };
        stack.Children.Add(textStack);
        if (task.CanEdit && task.AssignmentMode == Image2ConnectionAssignmentMode.Manual)
        {
            stack.Children.Add(CreateQueueConnectionPicker(task));
        }
        stack.Children.Add(buttons);

        return new Border
        {
            Padding = 12,
            Stroke = new SolidColorBrush(_selectedTask?.Id == task.Id ? accent : ResourceColor("BorderSoft")),
            BackgroundColor = ResourceColor("PanelAlt"),
            Content = stack
        };
    }

    private static string GetStatusText(Image2QueuedTask task)
    {
        return task.Status switch
        {
            Image2TaskStatus.Running => task.StatusDetail,
            Image2TaskStatus.Completed => "生图成功",
            Image2TaskStatus.Failed => "生图失败",
            Image2TaskStatus.Stopped => "已停止",
            _ => task.StatusDetail
        };
    }

    private static string GetAssignmentText(Image2QueuedTask task)
    {
        return task.AssignmentMode == Image2ConnectionAssignmentMode.Auto
            ? "自动分配"
            : $"指定 {task.RequestedConnectionTitle ?? "未选择"}";
    }

    private View CreateQueueConnectionPicker(Image2QueuedTask task)
    {
        var picker = new Picker
        {
            Title = "指定接口",
            FontSize = 12,
            HeightRequest = 38,
            MinimumHeightRequest = 38,
            BindingContext = task.Id
        };

        foreach (var connection in _connections)
        {
            picker.Items.Add(connection.Title);
        }

        var selectedIndex = _connections.FindIndex(connection => connection.Id == task.RequestedConnectionId);
        picker.SelectedIndex = selectedIndex >= 0 ? selectedIndex : (_connections.Count > 0 ? 0 : -1);
        picker.SelectedIndexChanged += OnQueueConnectionChanged;

        return new Border
        {
            Padding = new Thickness(10, 0),
            BackgroundColor = ResourceColor("PanelBg"),
            Content = picker
        };
    }

    private async void OnQueueConnectionChanged(object? sender, EventArgs e)
    {
        if (sender is not Picker { BindingContext: string taskId } picker)
        {
            return;
        }

        var task = _state.Queue.FirstOrDefault(item => item.Id == taskId);
        if (task is null || !task.CanEdit || task.AssignmentMode != Image2ConnectionAssignmentMode.Manual)
        {
            return;
        }

        var index = picker.SelectedIndex;
        if (index < 0 || index >= _connections.Count)
        {
            return;
        }

        var connection = _connections[index];
        task.RequestedConnectionId = connection.Id;
        task.RequestedConnectionTitle = connection.Title;
        task.StatusDetail = BuildWaitingStatus(task);
        task.UpdatedAt = DateTime.Now;

        if (_selectedTask?.Id == task.Id)
        {
            SetConnectionPicker(connection.Id);
        }

        await PersistAndRenderAsync("已更新任务接口");
        DispatchQueue();
    }

    private void RenderHistory()
    {
        HistoryListStack.Children.Clear();
        if (_state.History.Count == 0)
        {
            HistoryListStack.Children.Add(CreateEmptyState("暂无历史", "任务完成或失败后会自动归档，并保留提示词、参考图和结果。"));
            return;
        }

        foreach (var group in GetHistoryGroups().Take(5))
        {
            var latest = group.OrderByDescending(item => item.Version).ThenByDescending(item => item.CreatedAt).First();
            HistoryListStack.Children.Add(CreateHomeHistoryCard(latest, group.Count()));
        }

        var remaining = GetHistoryGroups().Count() - 5;
        if (remaining > 0)
        {
            HistoryListStack.Children.Add(CreateCaptionLabel($"还有 {remaining} 个主任务，进入历史记录查看。"));
        }
    }

    private View CreateHomeHistoryCard(Image2HistoryEntry history, int versionCount)
    {
        var accent = history.Succeeded ? ResourceColor("Success") : ResourceColor("Danger");
        var cardAccent = history.Succeeded ? accent : ResourceColor("BorderSoft");
        var content = new VerticalStackLayout { Spacing = 6 };
        content.Children.Add(CreateCardTitle(history.Title, accent));
        content.Children.Add(CreateStatusLine($"{history.StatusText} / {versionCount} 个版本 / 最新 v{history.Version}", accent));
        content.Children.Add(CreateCaptionLabel($"{history.Size} / {history.Quality} / {history.AssignedConnectionTitle ?? "未记录接口"}", LineBreakMode.TailTruncation));
        var row = new HorizontalStackLayout { Spacing = 8 };
        var detailsButton = CreateSmallButton("详情", history.Id, OnHistoryOpenDetailsClicked);
        detailsButton.WidthRequest = 58;
        detailsButton.MinimumWidthRequest = 58;
        var rerunButton = CreateSmallButton("重生", history.Id, OnHistoryRerunClicked);
        rerunButton.WidthRequest = 58;
        rerunButton.MinimumWidthRequest = 58;
        row.Children.Add(detailsButton);
        row.Children.Add(rerunButton);
        content.Children.Add(row);

        var card = CreateClickableCard(content, cardAccent);
        card.BindingContext = history;
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            _homeDetailHistoryId = history.Id;
            _homeDetailTaskId = null;
            if (UseSplitHomeLayout())
            {
                RenderHomeDetail();
            }
            else
            {
                OnHistoryOpenDetailsClicked(card, EventArgs.Empty);
            }
        };
        card.GestureRecognizers.Add(tap);
        return card;
    }

    private View CreateHistoryRow(Image2HistoryEntry history)
    {
        var title = new Label
        {
            Text = $"{history.Title} · v{history.Version}",
            FontFamily = "OpenSansSemibold",
            FontSize = 13,
            TextColor = ResourceColor("TextStrong"),
            LineBreakMode = LineBreakMode.TailTruncation
        };

        var detail = new Label
        {
            Text = $"{history.StatusText} · {history.AssignedConnectionTitle ?? "未记录接口"} · {history.CreatedAt:MM-dd HH:mm}",
            LineBreakMode = LineBreakMode.TailTruncation
        };
        ApplyCaptionStyle(detail);

        var rerun = CreateSmallButton("重生", history.Id, OnHistoryRerunClicked);
        rerun.WidthRequest = 58;
        rerun.MinimumWidthRequest = 58;

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(64))
            },
            ColumnSpacing = 8
        };
        var textStack = new VerticalStackLayout { Spacing = 3 };
        textStack.Children.Add(title);
        textStack.Children.Add(detail);
        if (!history.Succeeded && !string.IsNullOrWhiteSpace(history.ErrorMessage))
        {
            textStack.Children.Add(CreateCaptionLabel(
                history.ErrorMessage,
                LineBreakMode.TailTruncation));
        }

        grid.Add(textStack, 0, 0);
        grid.Add(rerun, 1, 0);
        return new Border
        {
            Padding = 10,
            BackgroundColor = ResourceColor("PanelAlt"),
            Stroke = new SolidColorBrush(history.Succeeded ? ResourceColor("BorderSoft") : ResourceColor("Danger")),
            Content = grid
        };
    }

    private Button CreateSmallButton(string text, string id, EventHandler handler)
    {
        var button = new Button
        {
            Text = text,
            FontSize = 11,
            Padding = 0,
            WidthRequest = 40,
            HeightRequest = 32,
            MinimumWidthRequest = 40,
            MinimumHeightRequest = 32,
            CornerRadius = 7,
            BackgroundColor = ResourceColor("ControlBg"),
            TextColor = ResourceColor("TextStrong"),
            CommandParameter = id
        };
        button.Pressed += OnButtonPressed;
        button.Released += OnButtonReleased;
        button.Clicked += handler;
        return button;
    }

    private static Label CreateCaptionLabel(string? text, LineBreakMode lineBreakMode = LineBreakMode.WordWrap)
    {
        var label = new Label
        {
            Text = text ?? string.Empty,
            LineBreakMode = lineBreakMode
        };
        ApplyCaptionStyle(label);
        return label;
    }

    private static void ApplyCaptionStyle(Label label)
    {
        if (Application.Current?.Resources.TryGetValue("Caption", out var value) == true && value is Style style)
        {
            label.Style = style;
            return;
        }

        label.FontSize = 12;
        label.TextColor = ResourceColor("TextMuted");
    }

    private void UpdateQueueSummary()
    {
        var queued = _state.Queue.Count(task => task.Status == Image2TaskStatus.Queued);
        var running = _state.Queue.Count(task => task.Status == Image2TaskStatus.Running);
        QueueSummaryLabel.Text = $"{(_queueRunning ? "队列开启" : "队列关闭")} / 等待 {queued} / 运行 {running} / 历史 {_state.History.Count}";
        ConnectionSummaryLabel.Text = BuildConnectionSummaryText();
        BusyIndicator.IsVisible = running > 0;
        BusyIndicator.IsRunning = running > 0;
        UpdateStatusPanel();
    }

    private void UpdateStatusPanel()
    {
        if (EndpointLabel is null || LastResultStatusLabel is null || LastResultMetaLabel is null || ImageUrlLabel is null)
        {
            return;
        }

        var queued = _state.Queue.Count(task => task.Status == Image2TaskStatus.Queued);
        var running = _state.Queue.Count(task => task.Status == Image2TaskStatus.Running);
        var failed = _state.History.Count(history => !history.Succeeded);
        var idle = Math.Max(0, _connections.Count - _busyConnections.Count);
        EndpointLabel.Text = $"{(_queueRunning ? "队列开启" : "队列暂停")} / 接口 {_connections.Count} 个 / 空闲 {idle} 个";

        if (_lastRenderedTask is not null)
        {
            LastResultStatusLabel.Text = GetStatusText(_lastRenderedTask);
            LastResultStatusLabel.TextColor = StatusAccent(_lastRenderedTask.Status);
            LastResultMetaLabel.Text = $"{_lastRenderedTask.Title} / {_lastRenderedTask.Size} / {_lastRenderedTask.AssignedConnectionTitle ?? _lastRenderedTask.RequestedConnectionTitle ?? "自动分配"}";
            ImageUrlLabel.Text = string.IsNullOrWhiteSpace(_lastRenderedTask.OutputFolder)
                ? _settings.GetResolvedOutputRootPath()
                : _lastRenderedTask.OutputFolder;
            return;
        }

        LastResultStatusLabel.Text = running > 0
            ? $"运行中 {running} 个任务"
            : queued > 0
                ? $"等待 {queued} 个任务"
                : "暂无运行任务";
        LastResultStatusLabel.TextColor = running > 0
            ? ResourceColor("Accent")
            : queued > 0
                ? ResourceColor("Primary")
                : ResourceColor("TextMuted");
        LastResultMetaLabel.Text = $"历史 {_state.History.Count} 条 / 失败 {failed} 条 / 自动保存根目录";
        ImageUrlLabel.Text = _settings.GetResolvedOutputRootPath();
    }

    private string BuildQueuePageSubtitle()
    {
        var queued = _state.Queue.Count(task => task.Status == Image2TaskStatus.Queued);
        var running = _state.Queue.Count(task => task.Status == Image2TaskStatus.Running);
        var idle = Math.Max(0, _connections.Count - _busyConnections.Count);
        return $"{(_queueRunning ? "队列开启" : "队列暂停")} / 等待 {queued} / 运行 {running} / 空闲接口 {idle}";
    }

    private string BuildConnectionSummaryText()
    {
        if (_connections.Count == 0)
        {
            return "接口：未配置";
        }

        var idle = _connections.Count(connection => !_busyConnections.Contains(connection.Id));
        var details = _connections
            .Take(DeviceInfo.Platform == DevicePlatform.Android ? 3 : 5)
            .Select(connection =>
            {
                var runningTask = _state.Queue.FirstOrDefault(task =>
                    task.Status == Image2TaskStatus.Running &&
                    task.AssignedConnectionId == connection.Id);
                return runningTask is null
                    ? $"{connection.Title} 空闲"
                    : $"{connection.Title} 运行 {runningTask.Title}";
            });
        var suffix = _connections.Count > (DeviceInfo.Platform == DevicePlatform.Android ? 3 : 5)
            ? "..."
            : string.Empty;
        return $"接口：{_connections.Count} 个 / 空闲 {idle} | {string.Join("；", details)}{suffix}";
    }

    private void UpdateReferenceSummary()
    {
        if (ReferenceListStack is null)
        {
            return;
        }

        var urls = ParseReferenceImageUrls(ReferenceUrlEditor?.Text);
        var total = _referenceImages.Count + urls.Length;
        ReferenceCountLabel.Text = total == 0
            ? "未添加"
            : $"{total} 张参考图（本地 {_referenceImages.Count}，URL {urls.Length}）";

        ReferenceListStack.Children.Clear();
        if (total == 0)
        {
            ReferenceEmptyLabel.Text = "添加本地图片或粘贴图片 URL。";
            ReferenceListStack.Children.Add(ReferenceEmptyLabel);
        }
        else
        {
            for (var index = 0; index < _referenceImages.Count; index++)
            {
                ReferenceListStack.Children.Add(CreateLocalReferenceRow(_referenceImages[index], index));
            }

            for (var index = 0; index < urls.Length; index++)
            {
                ReferenceListStack.Children.Add(CreateUrlReferenceRow(urls[index], index));
            }
        }

        ClearReferenceButton.IsEnabled = total > 0;
    }

    private View CreateLocalReferenceRow(ReferenceImageItem item, int index)
    {
        var preview = new Image
        {
            Source = ImageSource.FromStream(() => new MemoryStream(item.PreviewBytes)),
            Aspect = Aspect.AspectFill,
            WidthRequest = 54,
            HeightRequest = 54,
            BackgroundColor = ResourceColor("PreviewBg")
        };

        var previewFrame = new Border
        {
            Padding = 0,
            WidthRequest = 54,
            HeightRequest = 54,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 7 },
            BackgroundColor = ResourceColor("PreviewBg"),
            Content = preview
        };

        return CreateReferenceRow(
            previewFrame,
            "本地",
            item.File.FileName,
            index + 1,
            OnRemoveReferenceImageClicked,
            index);
    }

    private View CreateUrlReferenceRow(string url, int index)
    {
        return CreateReferenceRow(
            CreateUrlPreview(url),
            "URL",
            url,
            index + 1,
            OnRemoveReferenceUrlClicked,
            index);
    }

    private View CreateUrlPreview(string url)
    {
        View content;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            content = new Image
            {
                Source = ImageSource.FromUri(uri),
                Aspect = Aspect.AspectFill,
                WidthRequest = 54,
                HeightRequest = 54,
                BackgroundColor = ResourceColor("PreviewBg")
            };
        }
        else
        {
            content = new Label
            {
                Text = "URL",
                FontFamily = "OpenSansSemibold",
                FontSize = 11,
                TextColor = ResourceColor("Primary"),
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center
            };
        }

        return new Border
        {
            Padding = 0,
            WidthRequest = 54,
            HeightRequest = 54,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 7 },
            BackgroundColor = ResourceColor("PreviewBg"),
            Content = content
        };
    }

    private View CreateReferenceRow(
        View preview,
        string badge,
        string text,
        int displayIndex,
        EventHandler removeHandler,
        object commandParameter)
    {
        var badgeLabel = new Label
        {
            Text = $"{badge} {displayIndex}",
            FontFamily = "OpenSansSemibold",
            FontSize = 12,
            TextColor = ResourceColor("Primary"),
            VerticalTextAlignment = TextAlignment.Center
        };

        var textLabel = new Label
        {
            Text = text,
            FontSize = 12,
            TextColor = ResourceColor("TextMuted"),
            LineBreakMode = LineBreakMode.TailTruncation,
            VerticalTextAlignment = TextAlignment.Center
        };

        var textStack = new VerticalStackLayout
        {
            Spacing = 3,
            VerticalOptions = LayoutOptions.Center
        };
        textStack.Children.Add(badgeLabel);
        textStack.Children.Add(textLabel);

        var removeButton = new Button
        {
            Text = "×",
            FontSize = 20,
            Padding = 0,
            WidthRequest = 38,
            HeightRequest = 38,
            MinimumWidthRequest = 38,
            MinimumHeightRequest = 38,
            CornerRadius = 19,
            BackgroundColor = ResourceColor("ControlBg"),
            TextColor = ResourceColor("TextMuted"),
            CommandParameter = commandParameter
        };
        removeButton.Pressed += OnButtonPressed;
        removeButton.Released += OnButtonReleased;
        removeButton.Clicked += removeHandler;

        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(54)),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(40))
            },
            ColumnSpacing = 10,
            Padding = new Thickness(0, 3)
        };

        row.Add(preview, 0, 0);
        row.Add(textStack, 1, 0);
        row.Add(removeButton, 2, 0);
        return row;
    }

    private void OnPageSizeChanged(object? sender, EventArgs e)
    {
        var width = Width;
        if (width <= 0)
        {
            return;
        }

        var threeColumn = width >= 1180;
        var twoColumn = width >= 820 && !threeColumn;
        WorkspaceGrid.ColumnDefinitions.Clear();
        WorkspaceGrid.RowDefinitions.Clear();

        if (threeColumn)
        {
            WorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(0.86, GridUnitType.Star)));
            WorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1.08, GridUnitType.Star)));
            WorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1.06, GridUnitType.Star)));
            WorkspaceGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(EditorColumn, 0);
            Grid.SetRow(EditorColumn, 0);
            Grid.SetRowSpan(EditorColumn, 1);
            Grid.SetColumn(QueueColumn, 1);
            Grid.SetRow(QueueColumn, 0);
            Grid.SetRowSpan(QueueColumn, 1);
            Grid.SetColumn(ResultColumn, 2);
            Grid.SetRow(ResultColumn, 0);
            Grid.SetRowSpan(ResultColumn, 1);
            WorkspaceGrid.ColumnSpacing = 14;
            WorkspaceGrid.RowSpacing = 0;
            WorkspaceGrid.Padding = new Thickness(20);
            PreviewSurface.MinimumHeightRequest = 132;
            RawJsonEditor.HeightRequest = 160;
        }
        else if (!twoColumn)
        {
            WorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            WorkspaceGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            WorkspaceGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            WorkspaceGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(EditorColumn, 0);
            Grid.SetRow(EditorColumn, 0);
            Grid.SetColumn(QueueColumn, 0);
            Grid.SetRow(QueueColumn, 1);
            Grid.SetColumn(ResultColumn, 0);
            Grid.SetRow(ResultColumn, 2);
            WorkspaceGrid.ColumnSpacing = 0;
            WorkspaceGrid.RowSpacing = 10;
            WorkspaceGrid.Padding = DeviceInfo.Platform == DevicePlatform.Android
                ? new Thickness(14, 14, 14, 22)
                : new Thickness(16);
            PreviewSurface.MinimumHeightRequest = 112;
            RawJsonEditor.HeightRequest = 150;
        }
        else
        {
            WorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(0.96, GridUnitType.Star)));
            WorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            WorkspaceGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            WorkspaceGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(EditorColumn, 0);
            Grid.SetRow(EditorColumn, 0);
            Grid.SetRowSpan(EditorColumn, 1);
            Grid.SetColumn(QueueColumn, 1);
            Grid.SetRow(QueueColumn, 0);
            Grid.SetRowSpan(QueueColumn, 2);
            Grid.SetColumn(ResultColumn, 0);
            Grid.SetRow(ResultColumn, 1);
            Grid.SetRowSpan(ResultColumn, 1);
            WorkspaceGrid.ColumnSpacing = 14;
            WorkspaceGrid.RowSpacing = 10;
            WorkspaceGrid.Padding = new Thickness(18);
            PreviewSurface.MinimumHeightRequest = 112;
            RawJsonEditor.HeightRequest = 160;
        }

        UpdatePlatformChrome();
    }

    private void UpdatePlatformChrome()
    {
        PlatformBadgeLabel.Text = DeviceInfo.Platform == DevicePlatform.Android
            ? "Android"
            : DeviceInfo.Platform == DevicePlatform.WinUI
                ? "Windows"
                : DeviceInfo.Platform.ToString();

        var padding = DeviceInfo.Platform == DevicePlatform.Android ? 14 : 16;
        PreviewCard.Padding = new Thickness(DeviceInfo.Platform == DevicePlatform.Android ? 12 : padding);
        AssistantCard.Padding = new Thickness(padding);
        RawJsonCard.Padding = new Thickness(padding);
    }

    private void SetStatus(string message)
    {
        StatusLabel.Text = message;
        _ = AnimateStatusAsync();
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

    private async Task AnimateStatusAsync()
    {
        StatusLabel.TranslationY = 2;
        await Task.WhenAll(
            StatusLabel.FadeToAsync(0.72, 80, Easing.SinOut),
            StatusLabel.TranslateToAsync(0, 0, 150, Easing.CubicOut));
        await StatusLabel.FadeToAsync(1, 130, Easing.SinIn);
    }

    private async void OnPageLoaded(object? sender, EventArgs e)
    {
        EditorColumn.Opacity = 0;
        QueueColumn.Opacity = 0;
        ResultColumn.Opacity = 0;
        EditorColumn.TranslationY = 18;
        QueueColumn.TranslationY = 22;
        ResultColumn.TranslationY = 24;

        await Task.WhenAll(
            EditorColumn.FadeToAsync(1, 320, Easing.SinOut),
            EditorColumn.TranslateToAsync(0, 0, 360, Easing.SpringOut),
            QueueColumn.FadeToAsync(1, 380, Easing.SinOut),
            QueueColumn.TranslateToAsync(0, 0, 420, Easing.SpringOut),
            ResultColumn.FadeToAsync(1, 420, Easing.SinOut),
            ResultColumn.TranslateToAsync(0, 0, 440, Easing.SpringOut));
    }

    private async Task AnimateResultAsync(bool hasImage)
    {
        ResultImage.Opacity = hasImage ? 0 : 1;
        EmptyPreview.Opacity = hasImage ? 0 : 1;

        if (hasImage)
        {
            ResultImage.Scale = 0.972;
            ResultImage.TranslationY = 12;
            await Task.WhenAll(
                ResultImage.FadeToAsync(1, 330, Easing.SinOut),
                ResultImage.ScaleToAsync(1, 420, Easing.SpringOut),
                ResultImage.TranslateToAsync(0, 0, 420, Easing.SpringOut));
        }
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

    private static string[] ParseReferenceImageUrls(string? text)
    {
        return (text ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
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
}
