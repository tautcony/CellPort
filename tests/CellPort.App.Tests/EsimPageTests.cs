using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using CellPort.App.Views;
using CellPort.Core.At;
using CellPort.Core.Models;
using CellPort.Core.Services;
using CellPort.Core.Transport;

namespace CellPort.App.Tests;

public sealed class EsimPageTests
{
    [AvaloniaFact]
    public async Task DeletingProfileStopsWhenEitherConfirmationIsCancelled()
    {
        foreach (var responses in new[] { new[] { false }, new[] { true, false } })
        {
            await using var rig = await TestRig.CreateAsync();
            var confirmations = 0;
            rig.Page.ConfirmOverride = (_, _) => Task.FromResult(responses[confirmations++]);
            await rig.LoadProfilesAsync();
            rig.Channel.Commands.Clear();

            var delete = Assert.IsType<Button>(rig.Page.FindProfileActionForTest("删除"));
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await rig.Page.WaitForActiveOperationsForTestAsync();

            Assert.Equal(responses.Length, confirmations);
            Assert.DoesNotContain(rig.Channel.Commands, command => IsProfileWrite(command));
        }
    }

    [AvaloniaFact]
    public async Task ReconnectingSameModemDuringSecondConfirmationDoesNotDeleteOldSessionProfile()
    {
        await using var rig = await TestRig.CreateAsync();
        var secondConfirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmations = 0;
        rig.Page.ConfirmOverride = (_, _) =>
        {
            if (Interlocked.Increment(ref confirmations) == 1) return Task.FromResult(true);
            secondConfirmation.TrySetResult();
            return allowSecond.Task;
        };
        await rig.LoadProfilesAsync();
        rig.Channel.Commands.Clear();

        Assert.IsType<Button>(rig.Page.FindProfileActionForTest("删除"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await secondConfirmation.Task.WaitAsync(TimeSpan.FromSeconds(5));

        rig.Page.OnConnectionChanged(false);
        await rig.Modem.DisconnectAsync();
        Assert.True(await rig.Modem.ConnectAsync("synthetic-at"));
        rig.Page.OnConnectionChanged(true);
        allowSecond.SetResult(true);
        await rig.Page.WaitForActiveOperationsForTestAsync();

        Assert.Equal(2, confirmations);
        Assert.DoesNotContain(rig.Channel.Commands, command => IsProfileWrite(command));
    }

    [AvaloniaFact]
    public async Task DisconnectedPageShowsReadableEmptyStateAndDisablesProfileRead()
    {
        var page = new EsimPage();
        try
        {
            page.OnConnectionChanged(false);
            Assert.Equal("RefreshButton", page.RefreshButton.Name);
            Assert.False(page.RefreshButton.IsEnabled);
        }
        finally { await page.DisposeAsync(); }
    }

    private static bool IsProfileWrite(string command) =>
        command.StartsWith("AT+CGLA=", StringComparison.Ordinal) &&
        (command.Contains("BF31", StringComparison.OrdinalIgnoreCase) ||
         command.Contains("BF32", StringComparison.OrdinalIgnoreCase) ||
         command.Contains("BF33", StringComparison.OrdinalIgnoreCase) ||
         command.Contains("BF29", StringComparison.OrdinalIgnoreCase));

    private sealed class TestRig : IAsyncDisposable
    {
        private TestRig(TestChannel channel, ModemManager modem, EsimPage page)
        { Channel = channel; Modem = modem; Page = page; }
        public TestChannel Channel { get; }
        public ModemManager Modem { get; }
        public EsimPage Page { get; }

        public static async Task<TestRig> CreateAsync()
        {
            var channel = new TestChannel();
            var modem = new ModemManager(new TestProvider(channel));
            if (!await modem.ConnectAsync("synthetic-at")) throw new InvalidOperationException("Synthetic modem failed to connect.");
            var page = new EsimPage();
            page.OnActivated(modem);
            page.OnConnectionChanged(true);
            return new TestRig(channel, modem, page);
        }

        public async Task LoadProfilesAsync()
        {
            RefreshButtonClick();
            await Page.WaitForActiveOperationsForTestAsync();
            Assert.True(Page.FindProfileActionForTest("删除")?.IsEnabled == true);
        }

        private void RefreshButtonClick() => Page.RefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        public async ValueTask DisposeAsync()
        {
            await Page.DisposeAsync();
            await Modem.DisposeAsync();
        }
    }

    private sealed class TestProvider(TestChannel channel) : IAtChannelProvider
    {
        public StringComparer PortNameComparer => StringComparer.OrdinalIgnoreCase;
        public Task<IAtChannel?> TryOpenUsbAsync(CancellationToken ct = default) => Task.FromResult<IAtChannel?>(null);
        public Task<string?> DetectSerialAsync(IReadOnlyList<string> knownPorts, CancellationToken ct = default) => Task.FromResult<string?>("synthetic-at");
        public IAtChannel CreateSerial(string portName) => channel;
    }

    private sealed class TestChannel : IAtChannel
    {
        private readonly object _gate = new();
        private byte[] _reply = [];
        public string Description => "synthetic eSIM test channel";
        public AtChannelKind Kind => AtChannelKind.SerialPort;
        public bool IsOpen { get; private set; }
        public List<string> Commands { get; } = [];
        public Task OpenAsync(CancellationToken ct = default) { IsOpen = true; return Task.CompletedTask; }
        public Task CloseAsync() { IsOpen = false; return Task.CompletedTask; }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            var command = Encoding.ASCII.GetString(data.Span).TrimEnd('\r', '\n');
            var payload = Response(command);
            lock (_gate) { Commands.Add(command); _reply = Encoding.ASCII.GetBytes($"\r\n{payload}OK\r\n"); }
            return Task.CompletedTask;
        }

        public Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var size = Math.Min(buffer.Length, _reply.Length);
                _reply.AsMemory(0, size).CopyTo(buffer); _reply = _reply[size..];
                return Task.FromResult(size);
            }
        }

        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }

        private static string Response(string command)
        {
            if (command.StartsWith("AT+CCHO=\"", StringComparison.Ordinal)) return "+CCHO: 1\r\n";
            if (!command.StartsWith("AT+CGLA=", StringComparison.Ordinal)) return string.Empty;
            var match = System.Text.RegularExpressions.Regex.Match(command, "\\\"([0-9A-F]+)\\\"$");
            if (!match.Success) return string.Empty;
            var apdu = match.Groups[1].Value;
            var response = apdu.Contains("BF3E", StringComparison.OrdinalIgnoreCase) ? "6A80"
                : apdu.Contains("CA9F7F", StringComparison.OrdinalIgnoreCase) ? "6A82"
                : apdu.Contains("BF2D", StringComparison.OrdinalIgnoreCase)
                    ? "BF2D22A020E31E5A0A989600214365870912344F10A0000005591010FFFFFFFF89000001009000"
                    : "9000";
            return $"+CGLA: {response.Length / 2},\"{response}\"\r\n";
        }
    }
}
