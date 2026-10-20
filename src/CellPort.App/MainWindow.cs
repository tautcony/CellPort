using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CellPort.App.Services;
using CellPort.App.Views;
using CellPort.Audio.Core.Services;
using CellPort.Core.Services;

namespace CellPort.App;

public sealed class MainWindow : Window
{
    private static readonly (string Tag, string Title)[] Navigation =
    [ ("Sms", "短信"), ("Calls", "通话"), ("Device", "设备"), ("Esim", "eSIM"), ("Console", "AT 控制台"), ("Settings", "设置") ];

    private readonly DeviceOperationCoordinator _deviceOperations = new();
    private readonly AudioSessionLifecycle _audioLifecycle = new();
    private readonly Dictionary<string, UserControl> _pages = [];
    private readonly ContentControl _pageHost = new();
    private readonly TextBlock _connLabel = new() { Name = "ConnectionLabel", Text = "未连接", FontSize = 13, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _statusHint = new() { Name = "ConnectionHint", Text = "连接模块后即可查看设备状态。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .78 };
    private readonly Avalonia.Controls.Shapes.Ellipse _connectionDot = new() { Name = "ConnectionDot", Width = 8, Height = 8, Fill = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _callPollTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _smsPollTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly SmsForwarder _smsForwarder = new();
    private readonly CancellationTokenSource _forwardingCts = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private SmsService? _forwardingService;
    private bool _everConnected;
    private bool _closing;
    private bool _finalClose;
    private bool _connectBusy;
    private Task? _startupTask;
    private Task? _connectTask;
    private Task? _refreshTask;
    private Task? _callPollTask;
    private Task? _smsPollTask;
    private readonly HashSet<Task> _forwardTasks = [];
    private readonly Dictionary<string, Button> _navButtons = [];
    private readonly System.Text.StringBuilder _connectLog = new();
    public static MainWindow? Current { get; private set; }
    public ModemManager Modem { get; }

    internal Control? CurrentPageControl => _pageHost.Content as Control;
    internal string CurrentPageTag { get; private set; } = "Sms";

    internal TextBlock PageTitleControl { get; } = new() { Name = "PageTitle", FontSize = 20, FontWeight = FontWeight.SemiBold };

    internal TextBlock PageSubtitleControl { get; } = new() { Name = "PageSubtitle", Text = "未连接", FontSize = 12, Opacity = .72 };

    internal TextBlock SignalStatusControl { get; } = new() { Name = "SignalText", Text = "信号  —", FontSize = 12 };

    internal TextBlock OperatorStatusControl { get; } = new() { Name = "OperatorText", Text = "运营商  —", FontSize = 12 };

    internal Button ConnectControl { get; } = new() { Name = "ConnectButton", Content = "连接模块", HorizontalAlignment = HorizontalAlignment.Stretch };

    internal Grid ShellRoot => (Grid)Content!;

    public MainWindow() : this([.. Environment.GetCommandLineArgs().Skip(1)]) { }

    public MainWindow(string[] args)
    {
        Current = this;
        var args1 = args;
        Modem = new ModemManager(PlatformServices.CreateChannelProvider()) { OperationAdmission = _deviceOperations };
        var settings = AppSettings.Current;
        Modem.PrimePortHints([settings.LastGoodSerialPort]);
        Modem.RememberPort = port => { settings.LastGoodSerialPort = port; settings.LastConnectedAt = DateTime.Now; settings.Save(); };
        Title = "CellPort · DJI 4G 模块管理器";
        Width = 1080; Height = 720; MinWidth = 880; MinHeight = 560;
        Content = BuildLayout();
        Modem.ConnectionChanged += OnConnectionChanged;
        Modem.StatusUpdated += OnStatusUpdated;
        Modem.TraceMessage += OnTraceMessage;
        AppSettings.SettingsChanged += OnSettingsChanged;
        _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(settings.RefreshIntervalSeconds, 1, 3600));
        _refreshTimer.Tick += (_, _) => { if (_refreshTask is not { IsCompleted: false } && Modem.IsConnected && !_closing) _refreshTask = RefreshAsync(); };
        _callPollTimer.Tick += (_, _) =>
        {
            if (_callPollTask is not { IsCompleted: false } && Modem.IsConnected && !_closing)
                _callPollTask = PollCallStateAsync();
        };
        _smsPollTimer.Tick += (_, _) =>
        {
            if (_smsPollTask is not { IsCompleted: false } && Modem.IsConnected && !_closing)
                _smsPollTask = PollSmsInboxAsync();
        };
        ConnectControl.Click += (_, _) => { if (_connectTask is not { IsCompleted: false }) _connectTask = ConnectClickAsync(); };
        Closing += OnClosing;
        Opened += async (_, _) =>
        {
            ShowPage("Sms");
            ApplyStartupPageFromArgs(args1);
            if (settings.AutoConnect) { _startupTask = TryAutoConnectAsync(); await _startupTask; }
            if (!_closing)
            {
                _refreshTimer.Start();
                _callPollTimer.Start();
                _smsPollTimer.Start();
            }
        };
    }

    private Control BuildLayout()
    {
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("196,*"), Background = Brush("AppBackground") };
        var sidebarFrame = new Border { Name = "Sidebar", Background = Brush("SidebarSurface"), BorderBrush = Brush("SurfaceBorder"), BorderThickness = new Thickness(0, 0, 1, 0) };
        var sidebar = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(12, 14) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, Margin = new Thickness(5, 4, 4, 16), VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Background = Brush("Accent"), Child = new TextBlock { Text = "4G", FontSize = 12, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White } });
        brand.Children.Add(new TextBlock { Text = "CellPort", FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        sidebar.Children.Add(brand);
        var navigation = new StackPanel { Spacing = 2 };
        foreach (var item in Navigation)
        {
            var button = new Button
            {
                Name = "Nav" + item.Tag, Content = item.Title, HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch, Tag = item.Tag, Height = 34, MinHeight = 34,
                Padding = new Thickness(10, 5), Classes = { "nav-item" },
            };
            button.Click += (_, _) => ShowPage(item.Tag);
            navigation.Children.Add(button);
            _navButtons[item.Tag] = button;
        }
        Grid.SetRow(navigation, 1); sidebar.Children.Add(navigation);
        var connectionCard = new Border { Name = "ConnectionCard", Classes = { "app-card" }, Padding = new Thickness(11), Child = new StackPanel { Spacing = 9 } };
        var connectionContent = (StackPanel)connectionCard.Child!;
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        statusRow.Children.Add(_connectionDot); statusRow.Children.Add(_connLabel);
        connectionContent.Children.Add(statusRow); connectionContent.Children.Add(_statusHint); connectionContent.Children.Add(ConnectControl);
        Grid.SetRow(connectionCard, 2); sidebar.Children.Add(connectionCard);
        sidebarFrame.Child = sidebar;
        Grid.SetColumn(sidebarFrame, 0); root.Children.Add(sidebarFrame);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(20, 9, 20, 8) };
        var titleStack = new StackPanel { Spacing = 2 };
        titleStack.Children.Add(PageTitleControl); titleStack.Children.Add(PageSubtitleControl);
        header.Children.Add(titleStack);
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        chips.Children.Add(StatusChip(SignalStatusControl)); chips.Children.Add(StatusChip(OperatorStatusControl));
        Grid.SetColumn(chips, 1); header.Children.Add(chips);
        header.Classes.Add("shell-header");
        body.Children.Add(header);
        Grid.SetRow(_pageHost, 1); body.Children.Add(_pageHost);
        Grid.SetColumn(body, 1); root.Children.Add(body);
        return root;
    }

    private static Border StatusChip(Control child) => new() { Child = child, Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(8), Background = Brush("CardSurface"), BorderBrush = Brush("SurfaceBorder"), BorderThickness = new Thickness(1) };
    private static IBrush Brush(string key) => Application.Current?.Resources.TryGetResource(key, null, out var value) == true && value is IBrush brush ? brush : Brushes.Transparent;

    public void ShowPage(string tag)
    {
        var key = NormalizePage(tag);
        if (key is null) return;
        CurrentPageTag = key;
        PageTitleControl.Text = Navigation.First(x => x.Tag == key).Title;
        UpdateHeaderStatus();
        if (!_pages.TryGetValue(key, out var page)) _pages[key] = page = CreatePage(key);
        _pageHost.Content = page;
        if (page is IModuleAware aware) aware.OnActivated(Modem);
        if (page is IModuleAware modulePage) modulePage.OnConnectionChanged(Modem.IsConnected);
        foreach (var (navTag, button) in _navButtons)
        {
            if (navTag == key) button.Classes.Add("selected");
            else button.Classes.Remove("selected");
        }
    }

    private static string? NormalizePage(string value) => Navigation.FirstOrDefault(x =>
        x.Tag.Equals(value, StringComparison.OrdinalIgnoreCase) || x.Title.Equals(value, StringComparison.OrdinalIgnoreCase)).Tag;

    private void ApplyStartupPageFromArgs(string[] args)
    {
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i].Equals("--page", StringComparison.OrdinalIgnoreCase)) { ShowPage(args[i + 1]); return; }
    }

    private UserControl CreatePage(string tag) => tag switch
    {
        "Sms" => new SmsPage(), "Calls" => new CallsPage(_audioLifecycle), "Device" => new DevicePage(),
        "Esim" => new EsimPage(), "Console" => new ConsolePage(), "Settings" => new SettingsPage(), _ => new SmsPage()
    };

    private async Task TryAutoConnectAsync()
    {
        _connectBusy = true; UpdateConnectButton();
        try
        {
            if (!await Modem.ConnectAsync(ct: _lifetimeCts.Token))
                _statusHint.Text = await DescribeConnectionFailureAsync(null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Error("自动连接失败", ex); _statusHint.Text = $"连接异常：{ex.Message}"; }
        finally { _connectBusy = false; UpdateConnectButton(); FlushConnectLog(); }
    }

    private async Task RefreshAsync()
    {
        try { await Modem.RefreshStatusAsync(_lifetimeCts.Token); }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex) { if (!_closing) AppLog.Warn($"状态刷新失败：{ex.Message}"); }
    }

    private async Task PollCallStateAsync()
    {
        try
        {
            if (Modem.Calls is { } calls)
                await calls.RefreshCallSnapshotAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex) { if (!_closing) AppLog.Warn($"通话状态检查失败：{ex.Message}"); }
    }

    private async Task PollSmsInboxAsync()
    {
        try
        {
            if (Modem.Sms is { } sms)
                await sms.SyncStoredAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex) { if (!_closing) AppLog.Warn($"短信收件箱检查失败：{ex.Message}"); }
    }

    private async Task ConnectClickAsync()
    {
        _connectBusy = true; UpdateConnectButton();
        try
        {
            if (Modem.IsConnected) { await Modem.DisconnectAsync(); return; }
            if (!await Modem.ConnectAsync(ct: _lifetimeCts.Token))
            {
                FlushConnectLog();
                var failure = await DescribeConnectionFailureAsync(null);
                _statusHint.Text = failure;
                var diagnostics = PlatformServices.GetDiagnostics();
                var details = string.Equals(failure, diagnostics, StringComparison.Ordinal)
                    ? failure
                    : $"{failure}\n\n{diagnostics}";
                await DialogService.ShowAsync(this, details, "连接失败");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Error("连接模块失败", ex); await DialogService.ShowAsync(this, ex.Message, "连接错误"); }
        finally { _connectBusy = false; UpdateConnectButton(); FlushConnectLog(); }
    }

    private void OnConnectionChanged(object? sender, bool connected) => Dispatcher.UIThread.Post(() =>
    {
        if (_closing) return;
        UpdateSmsForwardingSubscription(connected);
        _everConnected |= connected;
        _connLabel.Text = connected ? Modem.ChannelDescription : "未连接";
        UpdateHeaderStatus();
        if (!connected) { SignalStatusControl.Text = "信号  —"; OperatorStatusControl.Text = "运营商  —"; }
        else { SignalStatusControl.Text = $"信号  {Modem.Status.SignalText}"; OperatorStatusControl.Text = $"运营商  {Modem.Status.Operator ?? "未知"}"; }
        _statusHint.Text = connected ? "已连接。成功端口已记住。" : (_everConnected ? "连接已断开。点击连接模块重试。" : "点击连接模块以开始使用。");
        if (!_connectBusy) UpdateConnectButton();
        foreach (var page in _pages.Values) if (page is IModuleAware aware) aware.OnConnectionChanged(connected);
    });

    private void OnStatusUpdated(object? sender, Core.Models.DeviceStatus status) => Dispatcher.UIThread.Post(() =>
    { if (!_closing) { SignalStatusControl.Text = $"信号  {status.SignalText}"; OperatorStatusControl.Text = $"运营商  {status.Operator ?? "未知"}"; _statusHint.Text = Modem.IsConnected ? $"{status.Model} · {(status.SimReady ? "SIM 就绪" : "SIM 未就绪")}" : "未连接模块"; UpdateHeaderStatus(status); } });

    private void UpdateHeaderStatus(Core.Models.DeviceStatus? status = null)
    {
        status ??= Modem.IsConnected ? Modem.Status : null;
        PageSubtitleControl.Text = status is null ? "未连接" : $"{status.Model} · {(status.SimReady ? "SIM 就绪" : "SIM 未就绪")}";
    }

    private void UpdateConnectButton() { ConnectControl.IsEnabled = !_closing && !_connectBusy; ConnectControl.Content = _connectBusy ? "连接中…" : Modem.IsConnected ? "断开连接" : "连接模块"; _connectionDot.Fill = Brush(Modem.IsConnected ? "Success" : "TextSecondary"); }
    private void OnTraceMessage(object? sender, string message) { lock (_connectLog) _connectLog.AppendLine($"{DateTime.Now:HH:mm:ss.fff}  {message}"); }
    private async Task<string> DescribeConnectionFailureAsync(string? selectedPort)
    {
        try
        {
            var ports = await Task.Run(PlatformServices.EnumerateSerialPorts, _lifetimeCts.Token);
            var usbDevices = await Task.Run(PlatformServices.EnumerateKnownUsbDevices, _lifetimeCts.Token);
            return PlatformServices.DescribeConnectionFailure(selectedPort, ports, usbDevices);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { throw; }
        catch (Exception ex) { return $"串口枚举失败：{ex.Message}"; }
    }

    private static string ConnectLogPath
    {
        get
        {
            var directory = AppLog.LogDirectory;
            if (string.IsNullOrWhiteSpace(directory))
                directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CellPort", "logs");
            return Path.Combine(directory, "last-connect.log");
        }
    }

    private void FlushConnectLog() { try { Directory.CreateDirectory(Path.GetDirectoryName(ConnectLogPath)!); lock (_connectLog) File.WriteAllText(ConnectLogPath, _connectLog.ToString()); } catch { } }
    private void OnSettingsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    { if (!_closing) _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(AppSettings.Current.RefreshIntervalSeconds, 1, 3600)); });

    private void UpdateSmsForwardingSubscription(bool connected)
    {
        var service = connected ? Modem.Sms : null;
        if (ReferenceEquals(service, _forwardingService)) return;
        if (_forwardingService is not null) _forwardingService.MessageReceived -= OnSmsReceived;
        _forwardingService = service;
        if (service is not null) service.MessageReceived += OnSmsReceived;
    }
    private async void OnSmsReceived(object? sender, Core.At.SmsMessage message)
    {
        Task task;
        lock (_forwardTasks)
        {
            if (_closing || _forwardingCts.IsCancellationRequested) return;
            task = _smsForwarder.ForwardAsync(message, _forwardingCts.Token);
            _forwardTasks.Add(task);
        }
        try { await task; }
        catch (OperationCanceledException) when (_forwardingCts.IsCancellationRequested) { }
        catch (Exception ex) { AppLog.Warn($"短信转发失败：{ex.Message}"); }
        finally { lock (_forwardTasks) _forwardTasks.Remove(task); }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_finalClose) return;
        e.Cancel = true;
        if (_closing) return;
        lock (_forwardTasks) _closing = true;
        _refreshTimer.Stop(); _callPollTimer.Stop(); _smsPollTimer.Stop(); AppSettings.SettingsChanged -= OnSettingsChanged;
        await _lifetimeCts.CancelAsync(); await _forwardingCts.CancelAsync(); UpdateSmsForwardingSubscription(false);
        DialogService.CancelOwned(this);
        try
        {
            foreach (var page in _pages.Values) if (page is IDisposable disposable) disposable.Dispose();
            var active = new List<Task>();
            if (_startupTask is not null) active.Add(_startupTask);
            if (_connectTask is not null) active.Add(_connectTask);
            if (_refreshTask is not null) active.Add(_refreshTask);
            if (_callPollTask is not null) active.Add(_callPollTask);
            if (_smsPollTask is not null) active.Add(_smsPollTask);
            lock (_forwardTasks) active.AddRange(_forwardTasks);
            try { await Task.WhenAll(active); } catch (OperationCanceledException) { } catch (Exception ex) { AppLog.Error("等待后台任务结束失败", ex); }
            foreach (var page in _pages.Values)
            {
                try { if (page is IAsyncDisposable asyncPage) await asyncPage.DisposeAsync(); }
                catch (Exception ex) { AppLog.Error($"页面 {page.GetType().Name} 清理失败", ex); }
            }
            try { await Modem.DisposeAsync(); } catch (Exception ex) { AppLog.Error("释放模块连接失败", ex); }
            try { await _deviceOperations.DisposeAsync(); } catch (Exception ex) { AppLog.Error("释放设备操作协调器失败", ex); }
        }
        catch (Exception ex) { AppLog.Error("关闭窗口时清理资源失败", ex); }
        finally
        {
            Modem.ConnectionChanged -= OnConnectionChanged; Modem.StatusUpdated -= OnStatusUpdated; Modem.TraceMessage -= OnTraceMessage;
            _lifetimeCts.Dispose(); _forwardingCts.Dispose(); Current = null;
            _finalClose = true; Close();
        }
    }
}

public interface IModuleAware
{
    void OnActivated(ModemManager modem);
    void OnConnectionChanged(bool connected);
}
