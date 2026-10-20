using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CellPort.App.Services;
using CellPort.App.Views;
using CellPort.Core.Services;
using CellPort.Core.Transport;

namespace CellPort.App.Tests;

public sealed class DevicePageTests
{
    [Fact]
    public void DiagnosticsDistinguishNoSerialNodesAtFailureAndDisappearedSelection()
    {
        var macEmpty = PlatformServices.FormatMacSerialDiagnostics([]);
        Assert.Equal("未发现串口。", macEmpty);

        var macWithPort = PlatformServices.FormatMacSerialDiagnostics(["/dev/cu.usbserial-test"]);
        Assert.Equal("/dev/cu.usbserial-test", macWithPort);

        var noSerial = PlatformServices.DescribeConnectionFailure(null, []);
        Assert.Equal("未发现串口。", noSerial);

        var noAtResponse = PlatformServices.DescribeConnectionFailure(null, ["/dev/cu.usbserial-test"]);
        Assert.Equal("检测到 1 个串口，模块未响应。", noAtResponse);

        var qdcDevice = new[] { "QDC507 · 2CA3:4006" };
        Assert.Equal("QDC507 · 2CA3:4006", PlatformServices.FormatMacDiagnostics(qdcDevice, []));
        var qdcFailure = OperatingSystem.IsMacOS() ? "QDC507 未响应。" : "未发现串口。";
        Assert.Equal(qdcFailure, PlatformServices.DescribeConnectionFailure(null, [], qdcDevice));
        Assert.Equal("未发现设备。", PlatformServices.FormatMacDiagnostics([], []));

        var disappeared = PlatformServices.DescribeConnectionFailure("/dev/cu.old", ["/dev/cu.current"]);
        Assert.Equal("串口已断开：/dev/cu.old", disappeared);
    }

    [AvaloniaFact]
    public async Task DisconnectedPageShowsAllDeviceFieldsAndGuardsUssd()
    {
        var page = new DevicePage();
        try
        {
            Assert.Equal("PortCombo", page.PortCombo.Name);
            Assert.Equal("UssdSendButton", page.UssdSendButton.Name);
            Assert.Equal("PhonebookButton", page.PhonebookButton.Name);
            Assert.Equal("ReconnectButton", page.ReconnectButton.Name);

            var labels = page.InfoGrid.Children.OfType<StackPanel>()
                .Select(x => (x.Children[0] as TextBlock)?.Text)
                .ToArray();
            Assert.Contains("连接状态", labels);
            Assert.Contains("IMEI", labels);
            Assert.Contains("ICCID", labels);
            Assert.Contains("IMSI", labels);
            Assert.Contains("信号 RSSI", labels);
            Assert.Contains("信号 dBm", labels);
            Assert.Contains("信号百分比", labels);
            Assert.Contains("USB 模式", labels);
            Assert.Contains("更新时间", labels);

            page.UssdSendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("模块未连接。", page.UssdResult.Text);
        }
        finally
        {
            await page.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task PlatformOverrideCanSupplyPortableSerialAndDiagnosticData()
    {
        PlatformServices.SerialPortsOverride = () => ["/dev/cu.fake-at"];
        PlatformServices.DiagnosticsOverride = () => "fake diagnostic";
        try
        {
            Assert.Equal(["/dev/cu.fake-at"], PlatformServices.EnumerateSerialPorts());
            Assert.Equal("fake diagnostic", PlatformServices.GetDiagnostics());

            var page = new DevicePage();
            await page.DisposeAsync();
        }
        finally
        {
            PlatformServices.ResetOverrides();
        }
    }

    [AvaloniaFact]
    public async Task PlatformDiagnosticsAreRenderedOnceAndEmptyDiagnosticsStayReadable()
    {
        const string diagnostics = "QDC507 · 2CA3:4006";
        PlatformServices.DiagnosticsOverride = () => diagnostics;
        PlatformServices.SerialPortsOverride = () => [];
        var modem = new ModemManager(new CallsDeviceTestProvider(new CallsDeviceTestChannel()));
        var page = new DevicePage();
        try
        {
            page.OnActivated(modem);
            await WaitForDiagnosticsAsync(page, diagnostics);

            Assert.Equal(diagnostics, page.UsbSummary.Text);
            Assert.Single(DescendantTextBlocks(page), x => x.Text == diagnostics);
        }
        finally
        {
            await page.DisposeAsync();
            await modem.DisposeAsync();
            PlatformServices.ResetOverrides();
        }

        PlatformServices.DiagnosticsOverride = () => "  ";
        var emptyModem = new ModemManager(new CallsDeviceTestProvider(new CallsDeviceTestChannel()));
        var emptyPage = new DevicePage();
        try
        {
            emptyPage.OnActivated(emptyModem);
            await WaitForDiagnosticsAsync(emptyPage, "暂无诊断信息。");
            Assert.Equal("暂无诊断信息。", emptyPage.UsbSummary.Text);
            Assert.Single(DescendantTextBlocks(emptyPage), x => x.Text == "暂无诊断信息。");
        }
        finally
        {
            await emptyPage.DisposeAsync();
            await emptyModem.DisposeAsync();
            PlatformServices.ResetOverrides();
        }
    }

    [AvaloniaFact]
    public async Task ReconnectDistinguishesMissingMacSerialNodesFromUnresponsiveEnumeratedPorts()
    {
        PlatformServices.DiagnosticsOverride = () => PlatformServices.FormatMacSerialDiagnostics([]);
        PlatformServices.SerialPortsOverride = () => [];
        PlatformServices.UsbDevicesOverride = () => [];
        var noNodesFailure = PlatformServices.DescribeConnectionFailure(null, [], []);
        var noNodesModem = new ModemManager(new FailedDetectionProvider());
        var noNodesPage = new DevicePage();
        try
        {
            noNodesPage.OnActivated(noNodesModem);
            await WaitForChannelStatusAsync(noNodesPage, noNodesFailure);
            Assert.Equal("未发现串口。", noNodesPage.UsbSummary.Text);
            noNodesPage.ReconnectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForButtonReadyAsync(noNodesPage.ReconnectButton);
            Assert.Equal(noNodesFailure, noNodesPage.ChannelStatus.Text);
        }
        finally
        {
            await noNodesPage.DisposeAsync();
            await noNodesModem.DisposeAsync();
            PlatformServices.ResetOverrides();
        }

        PlatformServices.SerialPortsOverride = () => ["/dev/cu.synthetic-at"];
        PlatformServices.UsbDevicesOverride = () => [];
        var exposedModem = new ModemManager(new FailedDetectionProvider());
        var exposedPage = new DevicePage();
        try
        {
            exposedPage.OnActivated(exposedModem);
            await WaitForChannelStatusAsync(exposedPage, "发现 1 个串口。");
            exposedPage.ReconnectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForButtonReadyAsync(exposedPage.ReconnectButton);
            Assert.Equal(PlatformServices.DescribeConnectionFailure(null, ["/dev/cu.synthetic-at"]), exposedPage.ChannelStatus.Text);
        }
        finally
        {
            await exposedPage.DisposeAsync();
            await exposedModem.DisposeAsync();
            PlatformServices.ResetOverrides();
        }
    }

    [AvaloniaFact]
    public async Task ConnectedPageReadsAndRendersPhonebookFromModemService()
    {
        var channel = new CallsDeviceTestChannel();
        var modem = new ModemManager(new CallsDeviceTestProvider(channel));
        var page = new DevicePage();
        try
        {
            Assert.True(await modem.ConnectAsync("fake-device-port"));
            page.OnActivated(modem);
            await WaitForInitializationAsync(page);
            page.PhonebookButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForPhonebookAsync(page);
            Assert.Single(page.PhonebookPanel.Children);
            Assert.Contains("John", (page.PhonebookPanel.Children[0] as Button)?.Content?.ToString());
            Assert.Contains("5551234", (page.PhonebookPanel.Children[0] as Button)?.Content?.ToString());
            Assert.Contains("AT+CPBR=1,50", channel.Commands);
        }
        finally
        {
            await page.DisposeAsync();
            await modem.DisposeAsync();
        }
    }

    private static async Task WaitForPhonebookAsync(DevicePage page)
    {
        var stopAt = DateTime.UtcNow.AddSeconds(5);
        while (page.PhonebookPanel.Children.Count == 0 && DateTime.UtcNow < stopAt) await Task.Delay(10);
        Assert.NotEmpty(page.PhonebookPanel.Children);
    }

    private static async Task WaitForDiagnosticsAsync(DevicePage page, string expected)
    {
        var stopAt = DateTime.UtcNow.AddSeconds(3);
        while (page.UsbSummary.Text != expected && DateTime.UtcNow < stopAt) await Task.Delay(10);
        Assert.Equal(expected, page.UsbSummary.Text);
    }

    private static async Task WaitForChannelStatusAsync(DevicePage page, string expected)
    {
        var stopAt = DateTime.UtcNow.AddSeconds(3);
        while (!(page.ChannelStatus.Text?.Contains(expected, StringComparison.Ordinal) ?? false) && DateTime.UtcNow < stopAt)
            await Task.Delay(10);
        Assert.Contains(expected, page.ChannelStatus.Text ?? string.Empty);
    }

    private static async Task WaitForButtonReadyAsync(Button button)
    {
        var stopAt = DateTime.UtcNow.AddSeconds(3);
        while (!button.IsEnabled && DateTime.UtcNow < stopAt) await Task.Delay(10);
        Assert.True(button.IsEnabled);
    }

    private static IEnumerable<TextBlock> DescendantTextBlocks(Control control)
    {
        if (control is TextBlock textBlock) yield return textBlock;
        if (control is ContentControl { Content: Control content })
            foreach (var descendant in DescendantTextBlocks(content)) yield return descendant;
        if (control is Decorator { Child: Control decoratorChild })
            foreach (var descendant in DescendantTextBlocks(decoratorChild)) yield return descendant;
        if (control is Panel panel)
            foreach (var child in panel.Children)
                foreach (var descendant in DescendantTextBlocks(child)) yield return descendant;
    }

    private sealed class FailedDetectionProvider : IAtChannelProvider
    {
        public StringComparer PortNameComparer => StringComparer.Ordinal;
        public Task<IAtChannel?> TryOpenUsbAsync(CancellationToken ct = default) => Task.FromResult<IAtChannel?>(null);
        public async Task<string?> DetectSerialAsync(IReadOnlyList<string> knownPorts, CancellationToken ct = default)
        {
            await Task.Delay(50, ct);
            return null;
        }
        public IAtChannel CreateSerial(string portName) => throw new InvalidOperationException("Test provider has no AT-responsive port.");
    }

    private static async Task WaitForInitializationAsync(DevicePage page)
    {
        var stopAt = DateTime.UtcNow.AddSeconds(3);
        while (page.PortCombo.ItemsSource is null && DateTime.UtcNow < stopAt) await Task.Delay(10);
        Assert.NotNull(page.PortCombo.ItemsSource);
        await Task.Delay(25);
    }
}
