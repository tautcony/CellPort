using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using CellPort.App.Views;
using CellPort.Core.Services;

namespace CellPort.App.Tests;

public sealed class CallsPageTests
{
    [AvaloniaFact]
    public async Task DisconnectedPageKeepsControlsGuardedAndReportsControlOnlyAudio()
    {
        var page = new CallsPage();
        try
        {
            Assert.Equal("DialButton", page.DialButton.Name);
            Assert.Equal("AnswerButton", page.AnswerButton.Name);
            Assert.Equal("HangupButton", page.HangupButton.Name);
            Assert.Equal("MuteButton", page.MuteButton.Name);
            Assert.False(page.DialButton.IsEnabled);
            Assert.False(page.AnswerButton.IsEnabled);
            Assert.False(page.HangupButton.IsEnabled);
            Assert.False(page.MuteButton.IsEnabled);
            Assert.Equal("通话控制可用；媒体通路不可用（仅控制）", page.AudioSessionText.Text);
            Assert.Contains("不可用", page.AudioAvailabilityText.Text);
            Assert.Equal(12, page.KeypadGrid.Children.Count);
            Assert.Null(page.HistoryList.ItemsSource);
        }
        finally
        {
            await page.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ConnectedPageRoutesDialMuteAndDtmfThroughModemService()
    {
        var channel = new CallsDeviceTestChannel();
        var modem = new ModemManager(new CallsDeviceTestProvider(channel));
        var page = new CallsPage();
        var settings = AppSettings.Current;
        var originalPrivacy = settings.PrivacyMode;
        try
        {
            settings.PrivacyMode = true;
            Assert.True(await modem.ConnectAsync("fake-call-port"));
            page.OnActivated(modem);
            page.DialNumberBox.Text = "+15551234";
            page.DialButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForCommandAsync(channel, "ATD+15551234;");

            modem.Calls!.HandleUnsolicited("+CLIP: \"+44123456\",145");
            await WaitForUiAsync(() => page.DialNumberBox.Text == "••••••");

            channel.ActiveCall = true;
            await modem.Calls.RefreshCallSnapshotAsync();
            Assert.Equal("语音通话中", CallStatePresentation.GetStatusText(modem.Calls));
            await WaitForUiAsync(() => page.MuteButton.IsEnabled);

            page.MuteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForCommandAsync(channel, "AT+CMUT=1");
            page.KeypadGrid.Children.OfType<Button>().Single(button => Equals(button.Content, "5"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForCommandAsync(channel, "AT+VTS=5");

            channel.ActiveCall = false;
            await modem.Calls.RefreshCallSnapshotAsync();
            await WaitForUiAsync(() => page.HistoryList.ItemsSource is System.Collections.IEnumerable rows &&
                rows.Cast<object>().Any(row => row.ToString()?.Contains("••••••", StringComparison.Ordinal) == true));

            Assert.Contains("ATD+15551234;", channel.Commands);
            Assert.Contains("AT+CMUT=1", channel.Commands);
            Assert.Contains("AT+VTS=5", channel.Commands);
        }
        finally
        {
            await page.DisposeAsync();
            await modem.DisposeAsync();
            settings.PrivacyMode = originalPrivacy;
        }
    }

    [AvaloniaFact]
    public async Task IncomingCallControlsRouteAnswerAndHangupCommands()
    {
        var channel = new CallsDeviceTestChannel();
        var modem = new ModemManager(new CallsDeviceTestProvider(channel));
        var page = new CallsPage();
        try
        {
            Assert.True(await modem.ConnectAsync("fake-call-port"));
            page.OnActivated(modem);
            channel.IncomingCall = true;
            modem.Calls!.HandleUnsolicited("+CLIP: \"+15557654321\",145");
            await WaitForUiAsync(() => page.AnswerButton.IsEnabled);
            page.AnswerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForCommandAsync(channel, "ATA");

            channel.IncomingCall = false;
            channel.ActiveCall = true;
            await modem.Calls.RefreshCallSnapshotAsync();
            await WaitForUiAsync(() => page.HangupButton.IsEnabled);
            page.HangupButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForCommandAsync(channel, "AT+CHUP");
        }
        finally
        {
            await page.DisposeAsync();
            await modem.DisposeAsync();
        }
    }

    private static async Task WaitForCommandAsync(CallsDeviceTestChannel channel, string expected)
    {
        var stopAt = DateTime.UtcNow.AddSeconds(3);
        while (!channel.Commands.Contains(expected) && DateTime.UtcNow < stopAt) await Task.Delay(10);
        Assert.Contains(expected, channel.Commands);
    }

    private static async Task WaitForUiAsync(Func<bool> condition)
    {
        var stopAt = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < stopAt) await Task.Delay(10);
        Assert.True(condition());
    }
}
