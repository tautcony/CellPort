using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using CellPort.App.Services;
using CellPort.Core.Models;
using CellPort.Core.Services;

namespace CellPort.App.Views;

/// <summary>设备页：模块状态、平台诊断、串口连接、USSD 与 SIM 电话簿。</summary>
public sealed class DevicePage : UserControl, IModuleAware, IDisposable, IAsyncDisposable
{
    private const string AutoPort = "（自动探测）";
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _operations = [];
    private ModemManager? _modem;
    private string? _connectionMessage;
    private bool _busy;
    private bool _disposed;

    public Grid InfoGrid { get; }
    public Button RescanButton { get; }
    public TextBlock UsbSummary { get; }
    public ComboBox PortCombo { get; }
    public Button RefreshPortsButton { get; }
    public Button ReconnectButton { get; }
    public TextBlock ChannelStatus { get; }
    public Button OpenDeviceManagerButton { get; }
    public Button DriverSetupButton { get; }
    public TextBox UssdBox { get; }
    public Button UssdSendButton { get; }
    public TextBox UssdResult { get; }
    public Button PhonebookButton { get; }
    public TextBlock PhonebookSummary { get; }
    public StackPanel PhonebookPanel { get; }

    public DevicePage()
    {
        InfoGrid = new Grid { Name = "InfoGrid", ColumnDefinitions = new ColumnDefinitions("*,*") };
        RescanButton = Button("重新扫描", "RescanButton");
        UsbSummary = Text("尚未扫描", 12);
        UsbSummary.Name = "UsbSummary";
        PortCombo = new ComboBox { Name = "PortCombo", MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Stretch };
        RefreshPortsButton = Button("刷新", "RefreshPortsButton");
        ReconnectButton = Button("重新连接", "ReconnectButton", true);
        ChannelStatus = Text("尚未连接", 12);
        ChannelStatus.Name = "ChannelStatus";
        OpenDeviceManagerButton = Button("打开设备管理器", "OpenDeviceManagerButton");
        DriverSetupButton = Button("打开驱动绑定向导", "DriverSetupButton", true);
        UssdBox = new TextBox { Name = "UssdBox", Text = "*100#", PlaceholderText = "USSD 代码" };
        UssdSendButton = Button("发送", "UssdSendButton", true);
        UssdResult = new TextBox { Name = "UssdResult", Text = "尚未发送查询。", IsReadOnly = true, MinHeight = 64, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        PhonebookButton = Button("读取", "PhonebookButton");
        PhonebookSummary = Text("尚未读取。", 12);
        PhonebookSummary.Name = "PhonebookSummary";
        PhonebookPanel = new StackPanel { Name = "PhonebookPanel", Spacing = 3 };

        RescanButton.Click += (_, _) => StartOperation(RescanAsync);
        RefreshPortsButton.Click += (_, _) => StartOperation(RefreshPortsAsync);
        ReconnectButton.Click += (_, _) => StartOperation(ReconnectAsync);
        OpenDeviceManagerButton.Click += (_, _) => StartOperation(OpenDeviceManagerAsync);
        DriverSetupButton.Click += (_, _) => StartOperation(OpenDriverSetupAsync);
        UssdSendButton.Click += (_, _) => StartOperation(SendUssdAsync);
        PhonebookButton.Click += (_, _) => StartOperation(ReadPhonebookAsync);

        BuildContent();
        UpdateChannelStatus();
        RefreshDriverControls();
        BuildInfoGrid(null);
    }

    public void OnActivated(ModemManager modem)
    {
        ArgumentNullException.ThrowIfNull(modem);
        _modem = modem;
        RefreshView();
        StartOperation(InitializePageAsync);
    }

    public void OnConnectionChanged(bool connected)
    {
        if (connected) _connectionMessage = null;
        RefreshView();
        UpdateChannelStatus();
    }

    private void BuildContent()
    {
        var info = Card("模块信息");
        CardContent(info).Children.Add(InfoGrid);

        var usb = Card("USB 接口");
        var scanRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetColumn(RescanButton, 1);
        scanRow.Children.Add(RescanButton);
        if (!PlatformServices.SupportsUsbDriverBinding)
        {
            Grid.SetColumn(OpenDeviceManagerButton, 2);
            scanRow.Children.Add(OpenDeviceManagerButton);
        }
        CardContent(usb).Children.Add(scanRow);
        CardContent(usb).Children.Add(UsbSummary);

        var serial = Card("通信通道");
        var serialRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        serialRow.Children.Add(Text("AT 串口", 12));
        Grid.SetColumn(PortCombo, 1);
        serialRow.Children.Add(PortCombo);
        Grid.SetColumn(RefreshPortsButton, 2);
        serialRow.Children.Add(RefreshPortsButton);
        Grid.SetColumn(ReconnectButton, 3);
        serialRow.Children.Add(ReconnectButton);
        CardContent(serial).Children.Add(serialRow);
        CardContent(serial).Children.Add(ChannelStatus);

        Border? driver = null;
        if (PlatformServices.SupportsUsbDriverBinding)
        {
            driver = Card("驱动");
            var driverRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            driverRow.Children.Add(OpenDeviceManagerButton);
            driverRow.Children.Add(DriverSetupButton);
            CardContent(driver).Children.Add(driverRow);
        }

        var ussd = Card("USSD 查询");
        var ussdRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        ussdRow.Children.Add(UssdBox);
        Grid.SetColumn(UssdSendButton, 1);
        ussdRow.Children.Add(UssdSendButton);
        CardContent(ussd).Children.Add(ussdRow);
        CardContent(ussd).Children.Add(UssdResult);

        var phonebook = Card("SIM 电话簿");
        var phonebookHead = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(PhonebookButton, 1);
        phonebookHead.Children.Add(PhonebookButton);
        CardContent(phonebook).Children.Add(phonebookHead);
        CardContent(phonebook).Children.Add(PhonebookSummary);
        CardContent(phonebook).Children.Add(new ScrollViewer { MaxHeight = 260, Content = PhonebookPanel });

        var panel = new StackPanel { Spacing = 9 };
        panel.Children.Add(info);
        panel.Children.Add(usb);
        panel.Children.Add(serial);
        if (driver is not null) panel.Children.Add(driver);
        panel.Children.Add(ussd);
        panel.Children.Add(phonebook);
        Content = new ScrollViewer { Content = panel, Margin = new Thickness(16), VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    private void RefreshView()
    {
        BuildInfoGrid(_modem?.IsConnected == true ? _modem.Status : null);
        UpdateChannelStatus();
    }

    private async Task InitializePageAsync()
    {
        await RefreshDiagnosticsAsync();
        await RefreshPortsAsync();
    }

    private async Task RefreshDiagnosticsAsync()
    {
        try
        {
            var diagnostics = await Task.Run(PlatformServices.GetDiagnostics, _lifetime.Token);
            if (!_lifetime.IsCancellationRequested)
            {
                UsbSummary.Text = string.IsNullOrWhiteSpace(diagnostics)
                    ? "暂无诊断信息。"
                    : diagnostics.Trim();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { UsbSummary.Text = $"平台诊断失败：{ex.Message}"; }
    }

    private void UpdateChannelStatus()
    {
        ChannelStatus.Text = _modem?.IsConnected == true
            ? $"当前通道：{_modem.ChannelDescription}"
            : _connectionMessage ?? "尚未连接";
    }

    private async Task RefreshPortsAsync()
    {
        var previous = PortCombo.SelectedItem as string;
        RefreshPortsButton.IsEnabled = false;
        try
        {
            var ports = await Task.Run(PlatformServices.EnumerateSerialPorts, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            var options = new List<string> { AutoPort };
            options.AddRange(ports.Distinct(StringComparer.OrdinalIgnoreCase));
            PortCombo.ItemsSource = options;
            PortCombo.SelectedItem = previous is not null && options.Contains(previous) ? previous : AutoPort;
            if (_modem?.IsConnected != true)
            {
                _connectionMessage = ports.Count == 0
                    ? PlatformServices.DescribeConnectionFailure(null, ports, await Task.Run(PlatformServices.EnumerateKnownUsbDevices, _lifetime.Token))
                    : $"发现 {ports.Count} 个串口。";
                UpdateChannelStatus();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ChannelStatus.Text = $"串口枚举失败：{ex.Message}"; }
        finally { if (!_disposed) RefreshPortsButton.IsEnabled = true; }
    }

    private async Task ReconnectAsync()
    {
        var modem = _modem;
        if (modem is null) return;
        var selected = PortCombo.SelectedItem as string;
        var port = selected is null or AutoPort ? null : selected;
        ReconnectButton.IsEnabled = false;
        ReconnectButton.Content = "连接中…";
        _connectionMessage = null;
        ChannelStatus.Text = port is null ? "正在自动探测串口…" : $"正在连接 {port}…";
        try
        {
            var connected = await modem.ConnectAsync(port, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            _connectionMessage = connected
                ? null
                : PlatformServices.DescribeConnectionFailure(
                    port,
                    await Task.Run(PlatformServices.EnumerateSerialPorts, _lifetime.Token),
                    await Task.Run(PlatformServices.EnumerateKnownUsbDevices, _lifetime.Token));
            UpdateChannelStatus();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ChannelStatus.Text = $"连接异常：{ex.Message}"; }
        finally
        {
            if (!_disposed)
            {
                ReconnectButton.IsEnabled = true;
                ReconnectButton.Content = "重新连接";
                RefreshView();
            }
        }
    }

    private async Task RescanAsync()
    {
        UsbSummary.Text = "扫描中…";
        await RefreshDiagnosticsAsync();
        if (_modem?.IsConnected == true) await _modem.RefreshStatusAsync(_lifetime.Token);
        if (!_lifetime.IsCancellationRequested) BuildInfoGrid(_modem?.IsConnected == true ? _modem.Status : null);
    }

    private async Task OpenDeviceManagerAsync()
    {
        try { PlatformServices.OpenSystemDeviceManager(); }
        catch (Exception ex) { await DialogService.ShowAsync(this, $"无法打开设备管理器：{ex.Message}", "错误"); }
    }

    private async Task OpenDriverSetupAsync()
    {
        var instructions = PlatformServices.GetDriverSetupInstructions();
        await DialogService.ShowAsync(this,
            instructions ?? "当前平台不支持 USB 驱动绑定设置。",
            instructions is null ? "平台不支持" : "WinUSB 驱动设置");
    }

    private async Task SendUssdAsync()
    {
        var modem = _modem;
        if (modem?.IsConnected != true || modem.Cusd is null) { UssdResult.Text = "模块未连接。"; return; }
        var code = UssdBox.Text?.Trim() ?? string.Empty;
        if (code.Length == 0) return;
        UssdSendButton.IsEnabled = false;
        UssdSendButton.Content = "发送中…";
        UssdResult.Text = $"正在向网络发送 {code} …（响应可能需要数秒）";
        try
        {
            var result = await modem.SendUssdAsync(code, _lifetime.Token);
            UssdResult.Text = result.Ok ? result.Text is { Length: > 0 } ? result.Text : "（网络返回空内容）" : $"失败：{result.Raw ?? "无响应"}";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { UssdResult.Text = $"发送异常：{ex.Message}"; }
        finally { if (!_disposed) { UssdSendButton.IsEnabled = true; UssdSendButton.Content = "发送"; } }
    }

    private async Task ReadPhonebookAsync()
    {
        var modem = _modem;
        if (modem?.IsConnected != true || modem.Phonebook is null) { PhonebookSummary.Text = "模块未连接。"; return; }
        PhonebookButton.IsEnabled = false;
        PhonebookButton.Content = "读取中…";
        PhonebookPanel.Children.Clear();
        PhonebookSummary.Text = "正在读取 SIM 电话簿…";
        try
        {
            var entries = await modem.Phonebook.ListAsync(_lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            PhonebookSummary.Text = entries.Count == 0 ? "SIM 电话簿为空，或当前 SIM 不支持电话簿读取。" : $"共 {entries.Count} 条联系人（点击行复制号码）：";
            foreach (var entry in entries) PhonebookPanel.Children.Add(BuildPhonebookRow(entry));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { PhonebookSummary.Text = $"读取失败：{ex.Message}"; }
        finally { if (!_disposed) { PhonebookButton.IsEnabled = true; PhonebookButton.Content = "读取"; } }
    }

    private Control BuildPhonebookRow(PhonebookEntry entry)
    {
        var row = new Button
        {
            Content = string.IsNullOrWhiteSpace(entry.Name) ? entry.Number : $"{entry.Name}　·　{entry.Number}",
            HorizontalContentAlignment = HorizontalAlignment.Left,
            MinHeight = 34,
        };
        ToolTip.SetTip(row, "点击复制号码");
        row.Click += (_, _) => StartOperation(() => CopyNumberAsync(entry.Number));
        return row;
    }

    private async Task CopyNumberAsync(string number)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) throw new InvalidOperationException("当前窗口没有可用的剪贴板。");
            await clipboard.SetTextAsync(number);
            PhonebookSummary.Text = $"已复制号码 {number}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await DialogService.ShowAsync(this, $"复制号码失败：{ex.Message}", "剪贴板错误");
        }
    }

    private void BuildInfoGrid(DeviceStatus? status)
    {
        InfoGrid.Children.Clear();
        InfoGrid.RowDefinitions.Clear();
        var rows = new (string Label, string Value)[]
        {
            ("连接状态", status is null ? "未连接" : status.Connected ? "已连接" : "未连接"),
            ("型号", status?.Model ?? "--"),
            ("固件版本", status?.FirmwareRevision ?? "--"),
            ("IMEI", status?.Imei ?? "--"),
            ("ICCID", status?.Iccid ?? "--"),
            ("IMSI", status?.Imsi ?? "--"),
            ("本机号码", status?.OwnNumber ?? "--"),
            ("运营商", status?.Operator ?? "--"),
            ("网络制式", status?.NetworkMode ?? "--"),
            ("信号强度", status?.SignalText ?? "--"),
            ("信号 RSSI", status?.SignalRssi?.ToString() ?? "--"),
            ("信号 dBm", status?.SignalDbm is { } dbm ? $"{dbm} dBm" : "--"),
            ("信号百分比", status?.SignalPercent is { } percent ? $"{percent}%" : "--"),
            ("注册状态", status?.RegistrationStatus ?? "--"),
            ("SIM 状态", status is null ? "--" : status.SimReady ? "就绪" : "未就绪"),
            ("数据附着", status?.DataAttached switch { true => "已附着", false => "未附着", null => "--" }),
            ("USB 模式", status?.UsbMode ?? "--"),
            ("更新时间", status?.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss") ?? "--"),
        };
        for (var i = 0; i < (rows.Length + 1) / 2; i++) InfoGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var i = 0; i < rows.Length; i++)
        {
            var item = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 12, 4) };
            item.Children.Add(Text(rows[i].Label, 11));
            item.Children.Add(Text(rows[i].Value, 13));
            Grid.SetRow(item, i / 2);
            Grid.SetColumn(item, i % 2);
            InfoGrid.Children.Add(item);
        }
    }

    private void RefreshDriverControls()
    {
        DriverSetupButton.IsVisible = PlatformServices.SupportsUsbDriverBinding;
        OpenDeviceManagerButton.Content = PlatformServices.DeviceManagerButtonLabel;
    }

    private void StartOperation(Func<Task> operation)
    {
        if (_disposed || _busy || _lifetime.IsCancellationRequested) return;
        _busy = true;
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
            if (!_lifetime.IsCancellationRequested) await DialogService.ShowAsync(this, $"设备操作失败：{ex.Message}", "操作失败");
        }
        finally
        {
            _busy = false;
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
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task[] pending;
        lock (_operations) pending = [.. _operations];
        try { await Task.WhenAll(pending); }
        finally { _lifetime.Dispose(); }
    }

    private static Border Card(string heading)
    {
        var content = new StackPanel { Spacing = 9 };
        content.Children.Add(Text(heading, 14, true));
        return new Border { Classes = { "app-card" }, Padding = new Thickness(14), Child = content };
    }

    private static StackPanel CardContent(Border card) => (StackPanel)card.Child!;

    private static Button Button(string content, string name, bool primary = false) => new()
    {
        Name = name, Content = content, MinHeight = 32, Padding = new Thickness(12, 5),
        HorizontalContentAlignment = HorizontalAlignment.Center,
    };

    private static TextBlock Text(string text, double size = 14, bool bold = false) => new()
    {
        Text = text, FontSize = size, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
    };
}
