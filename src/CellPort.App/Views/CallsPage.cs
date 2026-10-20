using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CellPort.App.Services;
using CellPort.Audio.Core.Models;
using CellPort.Audio.Core.Resources;
using CellPort.Audio.Core.Services;
using CellPort.Core.Services;

namespace CellPort.App.Views;

/// <summary>通话页：拨号盘、来电控制、通话记录和诚实的音频能力状态。</summary>
public sealed class CallsPage : UserControl, IModuleAware, IDisposable, IAsyncDisposable
{
    private const string HiddenValue = "••••••";
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AudioSessionLifecycle _audioSessionLifecycle;
    private readonly RuntimeAudioAvailability _audioAvailability;
    private readonly List<Task> _operations = [];
    private readonly SemaphoreSlim _callCommandGate = new(1, 1);
    private bool _callCommandBusy;
    private ModemManager? _modem;
    private CallService? _attachedCallService;
    private string? _incomingNumber;
    private bool _muted;
    private bool _disposed;

    public TextBox DialNumberBox { get; }
    public Button DialButton { get; }
    public Button HangupButton { get; }
    public Button AnswerButton { get; }
    public Button MuteButton { get; }
    public TextBlock CallStateText { get; }
    public TextBlock AudioAvailabilityText { get; }
    public TextBlock AudioSessionText { get; }
    public UniformGrid KeypadGrid { get; }
    public ListBox HistoryList { get; }
    private readonly TextBlock _historyEmptyState;

    public CallsPage() : this(new AudioSessionLifecycle()) { }

    public CallsPage(AudioSessionLifecycle audioSessionLifecycle)
    {
        _audioSessionLifecycle = audioSessionLifecycle ?? throw new ArgumentNullException(nameof(audioSessionLifecycle));
        _audioAvailability = RuntimeAudioAvailability.Evaluate(profile: null, hostBackendAvailable: false);

        DialNumberBox = new TextBox { Name = "DialNumberBox", PlaceholderText = "输入号码", FontSize = 16 };
        DialButton = MakeButton("拨打", "DialButton", primary: true);
        HangupButton = MakeButton("挂断", "HangupButton");
        AnswerButton = MakeButton("接听", "AnswerButton");
        MuteButton = MakeButton("静音", "MuteButton");
        CallStateText = MakeText("空闲");
        CallStateText.Name = "CallStateText";
        AudioAvailabilityText = MakeText(_audioAvailability.StatusText, 12);
        AudioAvailabilityText.Name = "AudioAvailabilityText";
        AudioSessionText = MakeText("通话控制可用；媒体通路不可用（仅控制）", 12);
        AudioSessionText.Name = "AudioSessionText";
        KeypadGrid = new UniformGrid { Name = "KeypadGrid", Rows = 4, Columns = 3 };
        HistoryList = new ListBox
        {
            Name = "HistoryList",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _historyEmptyState = MakeText("暂无通话记录");
        _historyEmptyState.HorizontalAlignment = HorizontalAlignment.Center;
        _historyEmptyState.VerticalAlignment = VerticalAlignment.Center;
        _historyEmptyState.IsHitTestVisible = false;

        DialButton.Click += (_, _) => StartOperation(DialAsync);
        HangupButton.Click += (_, _) => StartOperation(HangupAsync);
        AnswerButton.Click += (_, _) => StartOperation(AnswerAsync);
        MuteButton.Click += (_, _) => StartOperation(MuteAsync);
        BuildKeypad();
        DialNumberBox.MinHeight = 42;
        DialNumberBox.MaxWidth = 620;
        // Match each column to the button plus its margins so horizontal and vertical gaps stay equal.
        KeypadGrid.Width = 144;

        var numberRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        numberRow.Children.Add(DialNumberBox);
        Grid.SetColumn(DialButton, 1);
        numberRow.Children.Add(DialButton);
        Grid.SetColumn(HangupButton, 2);
        numberRow.Children.Add(HangupButton);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(AnswerButton);
        controls.Children.Add(MuteButton);
        controls.Children.Add(CallStateText);

        var left = new StackPanel { Spacing = 10, MaxWidth = 780, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(MakeText("拨号", 15, true));
        left.Children.Add(numberRow);
        left.Children.Add(controls);
        left.Children.Add(AudioAvailabilityText);
        left.Children.Add(AudioSessionText);

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 18 };
        top.Children.Add(left);
        Grid.SetColumn(KeypadGrid, 1);
        top.Children.Add(KeypadGrid);
        var topCard = new Border { Classes = { "app-card" }, Padding = new Thickness(16), Child = top };

        var historyContent = new Grid();
        historyContent.Children.Add(HistoryList);
        historyContent.Children.Add(_historyEmptyState);
        var history = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 12 };
        history.Children.Add(MakeText("通话记录", 15, true));
        Grid.SetRow(historyContent, 1);
        history.Children.Add(historyContent);
        var historyCard = new Border { Classes = { "app-card" }, Padding = new Thickness(16), Child = history };
        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 12, Margin = new Thickness(16) };
        content.Children.Add(topCard);
        Grid.SetRow(historyCard, 1);
        content.Children.Add(historyCard);
        Content = content;

        foreach (var button in KeypadGrid.Children.OfType<Button>())
        {
            button.Width = 44;
            button.Height = 40;
            button.MinWidth = 0;
            button.Margin = new Thickness(2);
            button.Padding = new Thickness(0);
            button.FontSize = 15;
            button.HorizontalAlignment = HorizontalAlignment.Center;
            button.VerticalAlignment = VerticalAlignment.Center;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.VerticalContentAlignment = VerticalAlignment.Center;
        }
        _audioSessionLifecycle.StateChanged += OnAudioSessionChanged;
        AppSettings.SettingsChanged += OnSettingsChanged;
        UpdateState();
    }

    public void OnActivated(ModemManager modem)
    {
        ArgumentNullException.ThrowIfNull(modem);
        if (!ReferenceEquals(_modem, modem))
        {
            Detach();
            _modem = modem;
        }
        Attach();
        ReloadHistory();
        UpdateState();
    }

    public void OnConnectionChanged(bool connected)
    {
        if (connected) Attach(); else Detach();
        ReloadHistory();
        UpdateState();
    }

    private void Attach()
    {
        var service = _modem?.Calls;
        if (ReferenceEquals(service, _attachedCallService)) return;
        Detach();
        _attachedCallService = service;
        if (_attachedCallService is not null)
        {
            _attachedCallService.StateChanged += OnCallStateChanged;
            _attachedCallService.IncomingCall += OnIncomingCall;
        }
    }

    private void Detach()
    {
        if (_attachedCallService is null) return;
        _attachedCallService.StateChanged -= OnCallStateChanged;
        _attachedCallService.IncomingCall -= OnIncomingCall;
        _attachedCallService = null;
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Ui(() =>
    {
        if (_incomingNumber is not null && (DialNumberBox.Text == HiddenValue || DialNumberBox.Text == _incomingNumber))
            DialNumberBox.Text = DisplayNumber(_incomingNumber);
        ReloadHistory();
        UpdateState();
    });

    private void OnCallStateChanged(object? sender, CallState state) => Ui(() => { UpdateState(); ReloadHistory(); });
    private void OnIncomingCall(object? sender, string number) => Ui(() =>
    {
        _incomingNumber = number;
        DialNumberBox.Text = DisplayNumber(number);
        CallStateText.Text = $"来电：{DisplayNumber(number)}";
    });
    private void OnAudioSessionChanged(object? sender, AudioSessionLifecycleDiagnostic diagnostic) => Ui(UpdateState);

    private void BuildKeypad()
    {
        foreach (var digit in new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "*", "0", "#" })
        {
            var button = MakeButton(digit, $"Key{(digit == "*" ? "Star" : digit == "#" ? "Hash" : digit)}");
            button.Click += (_, _) => StartOperation(() => KeypadAsync(digit));
            KeypadGrid.Children.Add(button);
        }
    }

    private async Task KeypadAsync(string digit)
    {
        var modem = _modem;
        if (modem?.Calls?.State == CallState.Active)
        {
            if (!modem.IsConnected) return;
            var ok = await modem.SendDtmfAsync(digit[0], _lifetime.Token);
            if (!ok && !_lifetime.IsCancellationRequested) await DialogService.ShowAsync(this, "DTMF 按键发送失败。", "发送失败");
        }
        else DialNumberBox.Text += digit;
    }

    private async Task DialAsync()
    {
        var modem = _modem;
        if (modem?.IsConnected != true || modem.Calls is null) return;
        var number = DialNumberBox.Text?.Trim() ?? string.Empty;
        if (AppSettings.Current.PrivacyMode && number == HiddenValue && _incomingNumber is not null) number = _incomingNumber;
        if (number.Length == 0) return;
        if (!await _callCommandGate.WaitAsync(0, _lifetime.Token)) return;
        _callCommandBusy = true;
        UpdateState();
        bool ok;
        try { ok = await modem.DialAsync(number, _lifetime.Token); }
        finally { _callCommandBusy = false; _callCommandGate.Release(); UpdateState(); }
        if (!ok && !_lifetime.IsCancellationRequested)
            await DialogService.ShowAsync(this, "拨号失败。模块可能不支持语音通话，或未注册到网络。", "拨号失败");
        UpdateState();
    }

    private async Task HangupAsync()
    {
        var modem = _modem;
        if (modem?.IsConnected != true || modem.Calls is null) return;
        await modem.HangUpAsync(_lifetime.Token);
        UpdateState();
        ReloadHistory();
    }

    private async Task AnswerAsync()
    {
        var modem = _modem;
        if (modem?.IsConnected != true || modem.Calls?.State != CallState.Ringing) return;
        if (!await _callCommandGate.WaitAsync(0, _lifetime.Token)) return;
        _callCommandBusy = true;
        UpdateState();
        try { await modem.AnswerAsync(_lifetime.Token); }
        finally { _callCommandBusy = false; _callCommandGate.Release(); UpdateState(); }
    }

    private async Task MuteAsync()
    {
        var modem = _modem;
        if (modem?.IsConnected != true || modem.Calls is null) return;
        if (!await _callCommandGate.WaitAsync(0, _lifetime.Token)) return;
        var next = !_muted;
        _callCommandBusy = true;
        UpdateState();
        bool ok;
        try { ok = await modem.SetCallMuteAsync(next, _lifetime.Token); }
        finally { _callCommandBusy = false; _callCommandGate.Release(); UpdateState(); }
        if (ok) _muted = next;
        else if (!_lifetime.IsCancellationRequested) await DialogService.ShowAsync(this, "静音状态更新失败。", "操作失败");
        MuteButton.Content = _muted ? "取消静音" : "静音";
    }

    private void UpdateState()
    {
        var calls = _modem?.Calls;
        var state = calls?.State ?? CallState.Idle;
        var status = CallStatePresentation.GetStatusText(calls);
        var peer = calls?.PeerNumber;
        CallStateText.Text = calls?.CurrentSnapshot.SelectedVoiceCall is not null && !string.IsNullOrEmpty(peer)
            ? $"{status} · {DisplayNumber(peer)}" : status;
        var connected = _modem?.IsConnected == true && calls is not null;
        DialButton.IsEnabled = connected && !_callCommandBusy;
        HangupButton.IsEnabled = connected && state is CallState.Dialing or CallState.Ringing or CallState.Active or CallState.Held or CallState.HangupPending or CallState.HangupUnconfirmed;
        AnswerButton.IsEnabled = connected && state == CallState.Ringing && !_callCommandBusy;
        MuteButton.IsEnabled = connected && state == CallState.Active && !_callCommandBusy;
        MuteButton.Content = _muted ? "取消静音" : "静音";
        var snapshot = calls?.CurrentSnapshot ?? new CallSnapshot(ClccQueryOutcome.TransportFailure, false, 0, 0,
            ImmutableArray<TrackedCall>.Empty, null, 0);
        var ui = CallAudioUiSnapshot.Create(state, snapshot, _audioAvailability, _audioSessionLifecycle.Diagnostic);
        AudioAvailabilityText.Text = ui.AudioStateText;
        AudioSessionText.Text = ui.MediaPathText;
    }

    private void ReloadHistory()
    {
        var rows = _modem?.Calls?.History.Select(c =>
        {
            var direction = c.Direction == CallDirection.Outgoing ? "↗" : c.Missed ? "✕" : "↙";
            var duration = c.Duration is { TotalSeconds: > 0 } d ? FormatDuration(d) : string.Empty;
            return $"{direction}  {DisplayNumber(c.Number)}     {duration}     {c.Timestamp:MM-dd HH:mm}";
        }).ToList() ?? [];
        HistoryList.ItemsSource = rows;
        _historyEmptyState.IsVisible = rows.Count == 0;
    }

    private void StartOperation(Func<Task> operation)
    {
        if (_disposed || _lifetime.IsCancellationRequested) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_operations) _operations.Add(completion.Task);
        _ = RunOperationAsync(operation, completion);
    }

    private async Task RunOperationAsync(Func<Task> operation, TaskCompletionSource completion)
    {
        try { await operation(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_lifetime.IsCancellationRequested)
                await DialogService.ShowAsync(this, $"通话操作失败：{ex.Message}", "操作失败");
        }
        finally
        {
            lock (_operations) _operations.Remove(completion.Task);
            completion.TrySetResult();
        }
    }

    public void CancelOperations()
    {
        if (!_lifetime.IsCancellationRequested) _lifetime.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelOperations();
        Detach();
        AppSettings.SettingsChanged -= OnSettingsChanged;
        _audioSessionLifecycle.StateChanged -= OnAudioSessionChanged;
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task[] pending;
        lock (_operations) pending = _operations.ToArray();
        try { await Task.WhenAll(pending); }
        finally { _callCommandGate.Dispose(); _lifetime.Dispose(); }
    }

    private void Ui(Action action) => Dispatcher.UIThread.Post(() => { if (!_disposed) action(); });
    private static string DisplayNumber(string? number) => AppSettings.Current.PrivacyMode && !string.IsNullOrEmpty(number) ? HiddenValue : number ?? string.Empty;
    private static string FormatDuration(TimeSpan value) => value.TotalHours >= 1 ? $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}" : $"{value.Minutes}:{value.Seconds:D2}";

    private static Button MakeButton(string content, string name, bool primary = false) => new()
    {
        Name = name, Content = content, MinHeight = 34, Padding = new Thickness(12, 5),
        HorizontalContentAlignment = HorizontalAlignment.Center,
    };
    private static TextBlock MakeText(string text, double size = 14, bool bold = false) => new()
    {
        Text = text, FontSize = size, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
    };
}
