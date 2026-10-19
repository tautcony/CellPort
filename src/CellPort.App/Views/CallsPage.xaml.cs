using System.Windows;
using System.Windows.Controls;
using CellPort.Audio.Core.Resources;
using CellPort.Audio.Core.Services;
using CellPort.Core.Services;

namespace CellPort.App.Views;

/// <summary>
/// 通话页：拨号盘、接听/挂断、通话记录。
/// </summary>
public partial class CallsPage : UserControl, IModuleAware
{
    private ModemManager? _modem;
    private CallService? _attachedCallService;
    private readonly AudioSessionLifecycle _audioSessionLifecycle;
    private readonly RuntimeAudioAvailability _audioAvailability;
    private bool _muted;
    private string? _incomingNumber;
    private const string HiddenValue = "••••••";

    public CallsPage()
        : this(new AudioSessionLifecycle())
    {
    }

    public CallsPage(AudioSessionLifecycle audioSessionLifecycle)
    {
        _audioSessionLifecycle = audioSessionLifecycle ?? throw new ArgumentNullException(nameof(audioSessionLifecycle));
        _audioAvailability = RuntimeAudioAvailability.Evaluate(profile: null, hostBackendAvailable: false);
        InitializeComponent();
        BuildKeypad();
        _audioSessionLifecycle.StateChanged += OnAudioSessionChanged;
        AppSettings.SettingsChanged += OnSettingsChanged;
        AudioAvailabilityText.Text = _audioAvailability.StatusText;
        AudioSessionText.Text = "通话控制可用；媒体通路不可用（仅控制）";
    }

    public void OnActivated(ModemManager modem)
    {
        if (!ReferenceEquals(_modem, modem))
        {
            Detach();
            _modem = modem;
        }

        Attach();
        ReloadHistory();
        UpdateState();
    }

    public void OnConnectionChanged(bool connected) => Dispatcher.Invoke(() =>
    {
        DialButton.IsEnabled = connected;
        if (connected)
        {
            Attach();
        }
        else
        {
            Detach();
        }
        ReloadHistory();
        UpdateState();
    });

    private void Attach()
    {
        var service = _modem?.Calls;
        if (ReferenceEquals(service, _attachedCallService))
        {
            return;
        }

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
        if (_attachedCallService is not null)
        {
            _attachedCallService.StateChanged -= OnCallStateChanged;
            _attachedCallService.IncomingCall -= OnIncomingCall;
            _attachedCallService = null;
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_incomingNumber is not null &&
                (DialNumberBox.Text == HiddenValue || DialNumberBox.Text == _incomingNumber))
            {
                DialNumberBox.Text = DisplayNumber(_incomingNumber);
            }

            ReloadHistory();
            UpdateState();
        });
    }

    private void OnCallStateChanged(object? sender, CallState state)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateState();
            ReloadHistory();
        });
    }

    private void OnIncomingCall(object? sender, string number)
    {
        Dispatcher.Invoke(() =>
        {
            _incomingNumber = number;
            DialNumberBox.Text = DisplayNumber(number);
            CallStateText.Text = $"来电：{DisplayNumber(number)}";
        });
    }

    private void UpdateState()
    {
        var state = _modem?.Calls?.State ?? CallState.Idle;
        var status = CallStatePresentation.GetStatusText(_modem?.Calls);
        var peer = _modem?.Calls?.PeerNumber;
        CallStateText.Text = _modem?.Calls?.CurrentSnapshot.SelectedVoiceCall is not null
            && !string.IsNullOrEmpty(peer)
            ? $"{status} · {DisplayNumber(peer)}"
            : status;

        HangupButton.IsEnabled = state is CallState.Dialing or CallState.Ringing or CallState.Active
            or CallState.Held or CallState.HangupPending or CallState.HangupUnconfirmed;
        AnswerButton.IsEnabled = state == CallState.Ringing;
        var snapshot = _modem?.Calls?.CurrentSnapshot ?? new CallSnapshot(
            ClccQueryOutcome.TransportFailure, false, 0, 0,
            System.Collections.Immutable.ImmutableArray<TrackedCall>.Empty, null, 0);
        var ui = CallAudioUiSnapshot.Create(state, snapshot, _audioAvailability, _audioSessionLifecycle.Diagnostic);
        AudioAvailabilityText.Text = ui.AudioStateText;
        AudioSessionText.Text = ui.MediaPathText;
    }

    private void OnAudioSessionChanged(object? sender, AudioSessionLifecycleDiagnostic diagnostic)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.Invoke(UpdateState);
    }

    /// <summary>
    /// 生成 0-9、*、# 的拨号按键。
    /// </summary>
    private void BuildKeypad()
    {
        var keys = new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "*", "0", "#" };
        foreach (var key in keys)
        {
            var btn = new Button
            {
                Content = key,
                Margin = new Thickness(2),
                FontSize = 15,
                Height = 36,
                MinWidth = 44,
                Style = (Style)FindResource("GhostButton"),
            };
            btn.Click += OnKeypadClick;
            KeypadGrid.Children.Add(btn);
        }
    }

    private async void OnKeypadClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: string digit })
        {
            return;
        }

        // 通话中按键走 DTMF；否则追加到号码框
        if (_modem?.Calls?.State == CallState.Active)
        {
            await _modem.SendDtmfAsync(digit[0]);
        }
        else
        {
            DialNumberBox.Text += digit;
        }
    }

    private async void Dial_Click(object sender, RoutedEventArgs e)
    {
        var number = DialNumberBox.Text.Trim();
        if (AppSettings.Current.PrivacyMode && number == HiddenValue && _incomingNumber is not null)
        {
            number = _incomingNumber;
        }
        if (string.IsNullOrEmpty(number) || _modem?.Calls is null)
        {
            return;
        }

        var ok = await _modem.DialAsync(number);
        if (!ok)
        {
            MessageBox.Show("拨号失败。模块可能不支持语音通话，或未注册到网络。",
                "拨号失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateState();
    }

    private async void Hangup_Click(object sender, RoutedEventArgs e)
    {
        if (_modem?.Calls is null)
        {
            return;
        }
        await _modem.HangUpAsync();
        UpdateState();
        ReloadHistory();
    }

    private async void Answer_Click(object sender, RoutedEventArgs e)
    {
        if (_modem?.Calls is null)
        {
            return;
        }
        await _modem.AnswerAsync();
        UpdateState();
    }

    private async void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (_modem?.Calls is null)
        {
            return;
        }
        _muted = !_muted;
        await _modem.SetCallMuteAsync(_muted);
        MuteButton.Content = _muted ? "取消静音" : "静音";
    }

    private void ReloadHistory()
    {
        if (_modem?.Calls is null)
        {
            HistoryList.ItemsSource = null;
            return;
        }

        HistoryList.ItemsSource = _modem.Calls.History
            .Select(c => new CallRow
            {
                Number = DisplayNumber(c.Number),
                DirIcon = c.Direction == CallDirection.Outgoing ? "↗" : (c.Missed ? "✕" : "↙"),
                TimeText = c.Timestamp.ToString("MM-dd HH:mm"),
                DurationText = c.Duration.HasValue && c.Duration.Value.TotalSeconds > 0
                    ? FormatDuration(c.Duration.Value)
                    : "",
            })
            .ToList();
    }

    private static string DisplayNumber(string? number) =>
        AppSettings.Current.PrivacyMode && !string.IsNullOrEmpty(number) ? HiddenValue : number ?? string.Empty;

    private static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1
            ? $"{(int)d.TotalHours}:{d.Minutes:D2}:{d.Seconds:D2}"
            : $"{d.Minutes}:{d.Seconds:D2}";
}

/// <summary>通话记录展示行。</summary>
public sealed class CallRow
{
    /// <summary>号码。</summary>
    public required string Number { get; init; }

    /// <summary>方向图标。</summary>
    public required string DirIcon { get; init; }

    /// <summary>时间文本。</summary>
    public required string TimeText { get; init; }

    /// <summary>时长文本。</summary>
    public required string DurationText { get; init; }
}
