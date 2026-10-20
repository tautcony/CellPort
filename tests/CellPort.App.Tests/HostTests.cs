using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using CellPort.Core.Transport;
using CellPort.App.Services;
using CellPort.App.Views;

namespace CellPort.App.Tests;

public sealed class HostTests
{
    [AvaloniaFact]
    public async Task SixPageArgumentsAndNavigationCreateUsableViewsAndCloseCleanly()
    {
        var settings = AppSettings.Current;
        settings.AutoConnect = false;
        var window = new MainWindow(["--page", "eSiM"]);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        await Task.Delay(30);
        Assert.Equal("Esim", window.CurrentPageTag);
        Assert.IsType<EsimPage>(window.CurrentPageControl);

        var types = new Dictionary<string, Type>
        {
            ["sms"] = typeof(SmsPage), ["calls"] = typeof(CallsPage), ["device"] = typeof(DevicePage),
            ["esim"] = typeof(EsimPage), ["console"] = typeof(ConsolePage), ["settings"] = typeof(SettingsPage),
        };
        foreach (var (tag, type) in types)
        {
            window.ShowPage(tag);
            Assert.Equal(tag, window.CurrentPageTag, ignoreCase: true);
            Assert.IsType(type, window.CurrentPageControl);
        }

        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(MainWindow.Current);
    }

    [AvaloniaFact]
    public async Task DarkShellUsesCompactSidebarAndStatusHeader()
    {
        ThemeManager.Apply(AppTheme.Dark);
        var window = new MainWindow([]);
        window.Show();
        await Task.Delay(20);
        try
        {
            Assert.Equal(196, window.ShellRoot.ColumnDefinitions[0].Width.Value);
            Assert.Equal("短信", window.PageTitleControl.Text);
            Assert.False(string.IsNullOrWhiteSpace(window.PageSubtitleControl.Text));
            Assert.StartsWith("信号", window.SignalStatusControl.Text);
            Assert.StartsWith("运营商", window.OperatorStatusControl.Text);
            Assert.Equal("ConnectButton", window.ConnectControl.Name);
            Assert.IsType<Border>(window.ShellRoot.Children[0]);
            Assert.Equal(Color.Parse("#1C1C1E"), Assert.IsType<SolidColorBrush>(Application.Current!.Resources["AppBackground"]).Color);
        }
        finally
        {
            window.Close();
            await Task.Delay(20);
        }
    }

    [AvaloniaFact]
    public async Task ClosingOwnerCancelsAwaitingConfirmationAndInput()
    {
        var owner = new Window { Content = new TextBlock { Text = "owner" } };
        owner.Show();
        var confirm = DialogService.ConfirmAsync(owner, "Confirm?");
        await Task.Delay(30);
        DialogService.CancelOwned(owner);
        Assert.False(await confirm.WaitAsync(TimeSpan.FromSeconds(3)));

        var input = DialogService.InputAsync(owner, "Name", "initial");
        await Task.Delay(30);
        DialogService.CancelOwned(owner);
        Assert.Null(await input.WaitAsync(TimeSpan.FromSeconds(3)));
        owner.Close();
    }

    [AvaloniaFact]
    public async Task ClosingDuringSerialDetectionCancelsAndAwaitsConnection()
    {
        AppSettings.ResetForTests();
        AppSettings.Current.AutoConnect = true;
        var provider = new BlockingDetectionProvider();
        PlatformServices.ChannelProviderOverride = () => provider;
        var window = new MainWindow([]);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            window.Show();
            await provider.DetectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await provider.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Null(MainWindow.Current);
        }
        finally
        {
            if (MainWindow.Current is not null) window.Close();
            PlatformServices.ResetOverrides();
            AppSettings.Current.AutoConnect = false;
        }
    }

    private sealed class BlockingDetectionProvider : IAtChannelProvider
    {
        public TaskCompletionSource DetectionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public StringComparer PortNameComparer => StringComparer.Ordinal;
        public Task<IAtChannel?> TryOpenUsbAsync(CancellationToken ct = default) => Task.FromResult<IAtChannel?>(null);
        public async Task<string?> DetectSerialAsync(IReadOnlyList<string> knownPorts, CancellationToken ct = default)
        {
            DetectionStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { CancellationObserved.TrySetResult(); throw; }
            return null;
        }
        public IAtChannel CreateSerial(string portName) => throw new NotSupportedException();
    }
}
