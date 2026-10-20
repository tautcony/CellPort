using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Controls.Templates;
using CellPort.App.Services;
using CellPort.Core.At;
using CellPort.Core.Services;

namespace CellPort.App.Views;

/// <summary>短信会话、消息阅读、发送与整箱导出。</summary>
public sealed class SmsPage : UserControl, IModuleAware, IDisposable, IAsyncDisposable
{
    private const string HiddenValue = "••••••";
    private ModemManager? _modem;
    private SmsService? _attachedSmsService;
    private readonly Dictionary<string, List<SmsMessage>> _threads = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _activeTasks = [];
    private bool _sending;
    private string? _activeNumber;
    private string? _activeRecipient;
    private bool _disposed;
    private IReadOnlyList<SmsMessage>? _inboxOverride;
    internal Func<string, string, Task>? ShowOverride { get; set; }
    internal Func<string, Task<(string Name, Stream Stream)?>>? ExportPickerOverride { get; set; }

    public ListBox ThreadList { get; } = new() { Name = "ThreadList", HorizontalAlignment = HorizontalAlignment.Stretch };
    public Button SendButton { get; } = new() { Name = "SendButton", Content = "发送", IsEnabled = false };
    public Button RefreshButton { get; } = new() { Name = "RefreshButton", Content = "刷新" };
    public Button CallButton { get; } = new() { Name = "CallButton", Content = "拨打" };
    public Button ExportCsvButton { get; } = new() { Name = "ExportCsvButton", Content = "导出 CSV" };
    public Button ExportJsonButton { get; } = new() { Name = "ExportJsonButton", Content = "导出 JSON" };
    public TextBox NumberBox { get; } = new() { PlaceholderText = "目标号码", HorizontalAlignment = HorizontalAlignment.Stretch };
    public TextBox BodyBox { get; } = new() { PlaceholderText = "短信内容（Ctrl+Enter 发送）", AcceptsReturn = true };
    private readonly TextBlock _detailNumber = new() { Text = "选择一条会话", FontSize = 14 };
    private readonly TextBlock _detailMeta = new();
    public StackPanel MessagePanel { get; } = new() { Name = "MessagePanel", Spacing = 8 };
    private readonly ScrollViewer _messageScroller;
    private readonly TextBlock _status = new();
    private readonly TextBlock _threadEmptyState = new() { Text = "暂无短信", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };

    public SmsPage()
    {
        var fresh = new Button { Content = "新短信" };
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6 };
        toolbar.Children.Add(new TextBlock { Text = "短信会话", FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        toolbar.Children.Add(RefreshButton); toolbar.Children.Add(ExportCsvButton); toolbar.Children.Add(ExportJsonButton); toolbar.Children.Add(fresh);

        ThreadList.ItemTemplate = new FuncDataTemplate<ThreadItem>((item, _) =>
        {
            if (item is null) return new TextBlock();
            var stack = new StackPanel { Spacing = 4, Margin = new Thickness(8) };
            stack.Children.Add(new TextBlock { Text = item.DisplayNumber, FontWeight = FontWeight.SemiBold });
            stack.Children.Add(new TextBlock { Text = item.TimeText, FontSize = 11, Opacity = .7 });
            stack.Children.Add(new TextBlock { Text = $"{item.Count} 条 · {item.Preview}", TextTrimming = TextTrimming.CharacterEllipsis, Opacity = .8 });
            return stack;
        });
        var threadContent = new Grid();
        threadContent.Children.Add(ThreadList);
        threadContent.Children.Add(_threadEmptyState);
        var left = new Border { Classes = { "app-card" }, Padding = new Thickness(4), Child = threadContent };
        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var conversationHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12) };
        var titleStack = new StackPanel(); titleStack.Children.Add(_detailNumber); titleStack.Children.Add(_detailMeta);
        conversationHeader.Children.Add(titleStack);
        right.Children.Add(conversationHeader);
        _messageScroller = new ScrollViewer { Content = MessagePanel, Margin = new Thickness(12), VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(_messageScroller, 1); right.Children.Add(_messageScroller);
        var compose = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), Margin = new Thickness(12), RowSpacing = 6 };
        var recipientRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        recipientRow.Children.Add(NumberBox);
        Grid.SetColumn(CallButton, 1); recipientRow.Children.Add(CallButton);
        Grid.SetRow(recipientRow, 0); compose.Children.Add(recipientRow);
        var sendRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        BodyBox.MinHeight = 34; BodyBox.MaxHeight = 110;
        Grid.SetColumn(BodyBox, 0); sendRow.Children.Add(BodyBox);
        Grid.SetColumn(SendButton, 1); sendRow.Children.Add(SendButton);
        Grid.SetRow(sendRow, 1); compose.Children.Add(sendRow);
        Grid.SetRow(_status, 2); compose.Children.Add(_status);
        Grid.SetRow(compose, 2); right.Children.Add(compose);
        var rightCard = new Border { Classes = { "app-card" }, Child = right };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,12,5*") };
        body.ColumnDefinitions[0].MinWidth = 230;
        body.ColumnDefinitions[0].MaxWidth = 360;
        body.Children.Add(left); Grid.SetColumn(rightCard, 2); body.Children.Add(rightCard);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 12, Margin = new Thickness(16) };
        Grid.SetRow(toolbar, 0); layout.Children.Add(toolbar); Grid.SetRow(body, 1); layout.Children.Add(body);
        Content = layout;

        ThreadList.SelectionChanged += OnThreadSelected;
        SendButton.Click += async (_, _) => await TrackAsync(() => SendAsync());
        RefreshButton.Click += async (_, _) => await TrackAsync(() => RefreshAsync());
        BodyBox.KeyDown += async (_, e) => { if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; await TrackAsync(() => SendAsync()); } };
        ExportCsvButton.Click += async (_, _) => await TrackAsync(() => ExportAsync("csv"));
        ExportJsonButton.Click += async (_, _) => await TrackAsync(() => ExportAsync("json"));
        fresh.Click += (_, _) => NewSms();
        CallButton.Click += async (_, _) => await TrackAsync(() => DialAsync());
        AppSettings.SettingsChanged += OnSettingsChanged;
    }

    public void OnActivated(ModemManager modem)
    {
        if (!ReferenceEquals(_modem, modem)) { Detach(); _modem = modem; }
        Attach(); Reload();
    }

    public void OnConnectionChanged(bool connected)
    {
        SendButton.IsEnabled = connected;
        if (connected) Attach(); else Detach();
        RefreshButton.IsEnabled = connected;
        Reload();
    }

    private void Attach()
    {
        var service = _modem?.Sms;
        if (ReferenceEquals(service, _attachedSmsService)) return;
        Detach(); _attachedSmsService = service;
        if (service is not null) service.InboxChanged += OnInboxChanged;
    }

    private void Detach()
    {
        if (_attachedSmsService is null) return;
        _attachedSmsService.InboxChanged -= OnInboxChanged;
        _attachedSmsService = null;
    }

    private void OnInboxChanged(object? sender, EventArgs e) => PostReload();
    private void OnSettingsChanged(object? sender, EventArgs e) => PostReload();
    private void PostReload()
    {
        if (_disposed) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(Reload);
    }

    private void Reload()
    {
        if (_disposed) return;
        _threads.Clear();
        var inbox = _inboxOverride ?? _modem?.Sms?.Inbox;
        if (inbox is null) { ThreadList.ItemsSource = null; _threadEmptyState.Text = "未连接模块"; _threadEmptyState.IsVisible = true; ClearConversation("未连接模块", ""); return; }
        foreach (var msg in inbox)
        {
            var key = NormalizeForThread(msg.Number);
            if (!_threads.TryGetValue(key, out var group)) _threads[key] = group = [];
            group.Add(msg);
        }
        var items = _threads.Select(pair =>
        {
            var last = pair.Value.MaxBy(m => m.Timestamp ?? DateTime.MinValue)!;
            return new ThreadItem { Key = pair.Key, Number = last.Number, DisplayNumber = DisplayNumber(last.Number),
                Preview = AppSettings.Current.PrivacyMode ? HiddenValue : last.Body, Time = last.Timestamp,
                Count = pair.Value.Count, HasCode = pair.Value.Any(m => m.VerificationCode is not null) };
        }).OrderByDescending(i => i.Time ?? DateTime.MinValue).ToList();
        ThreadList.ItemsSource = items;
        _threadEmptyState.Text = "暂无短信";
        _threadEmptyState.IsVisible = items.Count == 0;
        var selected = items.FirstOrDefault(i => i.Key == _activeNumber) ?? items.FirstOrDefault();
        if (selected is null) { _activeNumber = _activeRecipient = null; ClearConversation("暂无短信", ""); }
        else ThreadList.SelectedItem = selected;
    }

    private void ClearConversation(string title, string details)
    { _detailNumber.Text = title; _detailMeta.Text = details; NumberBox.Text = string.Empty; MessagePanel.Children.Clear(); }

    private void OnThreadSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ThreadList.SelectedItem is not ThreadItem item) return;
        _activeNumber = item.Key; _activeRecipient = item.Number;
        _detailNumber.Text = DisplayNumber(item.Number); _detailMeta.Text = $"{item.Count} 条消息";
        NumberBox.Text = AppSettings.Current.PrivacyMode ? HiddenValue : item.Number;
        MessagePanel.Children.Clear();
        if (_threads.TryGetValue(item.Key, out var list)) foreach (var msg in list.OrderBy(m => m.Timestamp ?? DateTime.MinValue)) AppendBubble(msg);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _messageScroller.Offset = new Vector(0, _messageScroller.Extent.Height));
    }

    private void AppendBubble(SmsMessage msg)
    {
        var outgoing = msg.Direction == SmsDirection.Outgoing;
        var stack = new StackPanel { Spacing = 4 };
        var privacy = AppSettings.Current.PrivacyMode;
        if (!privacy && !outgoing && msg.VerificationCode is { } code)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = code, FontSize = 20, FontWeight = FontWeight.Bold });
            var copy = new Button { Content = "复制" };
            copy.Click += async (_, _) => { try { await TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(code)!; } catch { } };
            row.Children.Add(copy); stack.Children.Add(row);
        }
        stack.Children.Add(new TextBlock { Text = privacy ? HiddenValue : msg.Body, TextWrapping = TextWrapping.Wrap });
        if (msg.Timestamp.HasValue) stack.Children.Add(new TextBlock { Text = msg.Timestamp.Value.ToString("MM-dd HH:mm"), FontSize = 11, Opacity = .7 });
        var bubble = new Border { Child = stack, Padding = new Thickness(12, 8), MaxWidth = 460, CornerRadius = new CornerRadius(10),
            Background = outgoing ? Brushes.SteelBlue : new SolidColorBrush(Color.FromArgb(35, 128, 128, 128)), HorizontalAlignment = outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        MessagePanel.Children.Add(bubble);
    }

    private async Task SendAsync()
    {
        var number = GetCurrentRecipient(); var body = BodyBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(number)) { await DialogService.ShowAsync(this, "请填写目标号码。"); return; }
        if (body.Length == 0) return;
        var modem = _modem; var service = modem?.Sms;
        if (modem is null || service is null || !modem.IsConnected) { await DialogService.ShowAsync(this, "模块未连接。"); return; }
        if (_sending) return;
        _sending = true; SendButton.IsEnabled = false; SendButton.Content = "发送中…";
        try
        {
            if (_lifetime.IsCancellationRequested || !ReferenceEquals(modem, _modem) || !modem.IsConnected || !ReferenceEquals(service, modem.Sms)) return;
            if (await modem.SendSmsAsync(number, body, _lifetime.Token)) { BodyBox.Text = string.Empty; Reload(); }
            else await DialogService.ShowAsync(this, "发送失败。请检查信号与余额，或查看 AT 控制台日志。", "发送失败");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await DialogService.ShowAsync(this, $"发送异常：{ex.Message}", "错误"); }
        finally { _sending = false; SendButton.Content = "发送"; SendButton.IsEnabled = _modem?.IsConnected == true; }
    }

    private async Task ExportAsync(string format)
    {
        var inbox = (_inboxOverride ?? _modem?.Sms?.Inbox)?.ToArray();
        if (inbox is null || inbox.Length == 0) { await DialogService.ShowAsync(this, "暂无短信可导出。", "导出"); return; }
        var top = TopLevel.GetTopLevel(this);
        var extension = format == "csv" ? ".csv" : ".json";
        try
        {
            string fileName;
            if (ExportPickerOverride is { } pickerOverride)
            {
                var target = await pickerOverride(format);
                if (target is null) return;
                fileName = target.Value.Name;
                await using (target.Value.Stream)
                    await WriteExportToStreamAsync(format, inbox, target.Value.Stream, _lifetime.Token);
            }
            else
            {
                if (top?.StorageProvider is null) return;
                var pickerTask = top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                { Title = $"导出短信 {format.ToUpperInvariant()}", SuggestedFileName = $"CellPort-SMS-{DateTime.Now:yyyyMMdd-HHmmss}{extension}",
                    FileTypeChoices = [new FilePickerFileType(format.ToUpperInvariant()) { Patterns = [$"*{extension}"] }] });
                var file = await pickerTask.WaitAsync(_lifetime.Token);
                if (file is null) return;
                fileName = file.Name;
                await using var destination = await file.OpenWriteAsync();
                await WriteExportToStreamAsync(format, inbox, destination, _lifetime.Token);
            }
            await ShowAsync($"已导出 {inbox.Length} 条短信到：\n{fileName}", "导出完成");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await DialogService.ShowAsync(this, $"导出失败：{ex.Message}", "错误"); }
    }

    private async Task DialAsync()
    {
        var number = GetCurrentRecipient(); var modem = _modem;
        if (string.IsNullOrEmpty(number) || modem?.Calls is null || !modem.IsConnected) return;
        try
        {
            var started = await modem.DialAsync(number, _lifetime.Token);
            if (_lifetime.IsCancellationRequested || !ReferenceEquals(modem, _modem)) return;
            if (started) await ShowAsync($"正在拨打 {DisplayNumber(number)}…\n\n提示：语音通话需模块固件与运营商支持 VoLTE，部分环境下可能无法建立音频通路。", "拨号");
            else await ShowAsync("拨号失败。请确认模块仍连接且通话控制操作未被其他任务占用。", "拨号失败");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await ShowAsync($"拨号失败：{ex.Message}", "拨号"); }
    }

    private async Task RefreshAsync()
    {
        var modem = _modem;
        if (modem?.Sms is null || !modem.IsConnected) { _status.Text = "模块未连接。"; return; }
        try { await modem.Sms.SyncStoredAsync(_lifetime.Token); Reload(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = $"刷新失败：{ex.Message}"; }
    }

    private void NewSms() { _activeNumber = _activeRecipient = null; ThreadList.SelectedItem = null; ClearConversation("新短信", "填写号码后发送"); BodyBox.Text = string.Empty; NumberBox.Focus(); }
    private string GetCurrentRecipient() { var text = NumberBox.Text?.Trim() ?? ""; return AppSettings.Current.PrivacyMode && text == HiddenValue && _activeRecipient is not null ? _activeRecipient : text; }
    private static string DisplayNumber(string number) => AppSettings.Current.PrivacyMode ? HiddenValue : number;
    private static string NormalizeForThread(string number) => new([.. number.Where(char.IsDigit)]);
    internal void SetInboxSnapshotForTest(IReadOnlyList<SmsMessage> messages) { _inboxOverride = messages; Reload(); }
    internal string CurrentRecipientForTest => GetCurrentRecipient();
    internal static async Task WriteExportToStreamAsync(string format, IReadOnlyList<SmsMessage> messages, Stream destination, CancellationToken ct = default)
    {
        var extension = format switch { "csv" => ".csv", "json" => ".json", _ => throw new ArgumentOutOfRangeException(nameof(format)) };
        var temp = Path.Combine(Path.GetTempPath(), $"cellport-{Guid.NewGuid():N}{extension}");
        try
        {
            if (format == "csv") SmsExportService.ExportCsv(messages, temp);
            else SmsExportService.ExportJson(messages, temp);
            if (destination.CanSeek) { destination.SetLength(0); destination.Position = 0; }
            await using var source = File.OpenRead(temp);
            await source.CopyToAsync(destination, ct);
        }
        finally { try { File.Delete(temp); } catch { } }
    }
    private async Task TrackAsync(Func<Task> action)
    {
        if (_disposed) return;
        var task = action(); _activeTasks.Add(task);
        try { await task; } finally { _activeTasks.Remove(task); }
    }
    private Task ShowAsync(string message, string title) => ShowOverride?.Invoke(message, title) ?? DialogService.ShowAsync(this, message, title);
    internal async Task WaitForActiveOperationsForTestAsync() { try { await Task.WhenAll(_activeTasks.ToArray()); } catch (OperationCanceledException) { } }
    public void CancelOperations() { if (!_lifetime.IsCancellationRequested) _lifetime.Cancel(); }
    public void Dispose() { if (_disposed) return; _disposed = true; CancelOperations(); Detach(); AppSettings.SettingsChanged -= OnSettingsChanged; }
    public async ValueTask DisposeAsync() { Dispose(); try { await Task.WhenAll(_activeTasks.ToArray()); } catch (OperationCanceledException) { } _lifetime.Dispose(); }
}

public sealed class ThreadItem
{
    public required string Key { get; init; }
    public required string Number { get; init; }
    public required string DisplayNumber { get; init; }
    public required string Preview { get; init; }
    public DateTime? Time { get; init; }
    public int Count { get; init; }
    public bool HasCode { get; init; }
    public string TimeText => Time?.ToString("MM-dd HH:mm") ?? "";
}
