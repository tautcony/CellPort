using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using CellPort.App.Views;
using CellPort.Core.At;
using CellPort.Core.Services;
using Avalonia.Interactivity;
using System.Text;

namespace CellPort.App.Tests;

public sealed class SmsPageTests
{
    [AvaloniaFact]
    public async Task PrivacyHidesMessageAndNumberWhileKeepingSelectedRecipientUsable()
    {
        var settingsDirectory = Path.Combine(Path.GetTempPath(), "cellport-app-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDirectory);
        var previousSettingsPath = AppSettings.ConfigPathOverride;
        AppSettings.ResetForTests();
        AppSettings.ConfigPathOverride = Path.Combine(settingsDirectory, "settings.json");
        AppSettings.Current.PrivacyMode = true;
        var page = new SmsPage();
        try
        {
            var message = new SmsMessage
            {
                Number = "+15551234567",
                Body = "Your verification code is 483921",
                VerificationCode = "483921",
                Timestamp = DateTime.Now,
                Direction = SmsDirection.Incoming,
            };
            page.SetInboxSnapshotForTest([message]);

            Assert.Equal("••••••", page.NumberBox.Text);
            Assert.Equal("+15551234567", page.CurrentRecipientForTest);
            Assert.Single(page.ThreadList.ItemsSource!.Cast<ThreadItem>());
            var bubble = Assert.IsType<Border>(Assert.Single(page.MessagePanel.Children));
            var content = Assert.IsType<StackPanel>(bubble.Child);
            Assert.DoesNotContain(content.Children.OfType<TextBlock>(), text => text.Text == message.VerificationCode);
            Assert.Contains(content.Children.OfType<TextBlock>(), text => text.Text == "••••••");

            AppSettings.Current.PrivacyMode = false;
            page.SetInboxSnapshotForTest([message]);
            Assert.Equal("+15551234567", page.NumberBox.Text);
            Assert.Equal("+15551234567", page.CurrentRecipientForTest);
            Assert.Contains(page.MessagePanel.Children.OfType<Border>()
                .SelectMany(b => ((StackPanel)b.Child!).Children.OfType<StackPanel>())
                .SelectMany(row => row.Children.OfType<TextBlock>()), text => text.Text == message.VerificationCode);
        }
        finally
        {
            await page.DisposeAsync();
            AppSettings.ResetForTests();
            AppSettings.ConfigPathOverride = previousSettingsPath;
            try { Directory.Delete(settingsDirectory, recursive: true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task DisconnectedPageRetainsEmptyStateAndGuardsSending()
    {
        var page = new SmsPage();
        try
        {
            page.OnConnectionChanged(false);
            Assert.Equal("ThreadList", page.ThreadList.Name);
            Assert.Equal("SendButton", page.SendButton.Name);
            Assert.False(page.SendButton.IsEnabled);
            Assert.Null(page.ThreadList.ItemsSource);
        }
        finally { await page.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task DialButtonUsesSelectedRealRecipientWhenPrivacyMasksNumber()
    {
        var settingsDirectory = Path.Combine(Path.GetTempPath(), "cellport-sms-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDirectory);
        var previousSettingsPath = AppSettings.ConfigPathOverride;
        AppSettings.ResetForTests();
        AppSettings.ConfigPathOverride = Path.Combine(settingsDirectory, "settings.json");
        AppSettings.Current.PrivacyMode = true;
        var channel = new CallsDeviceTestChannel();
        await using var modem = new ModemManager(new CallsDeviceTestProvider(channel));
        var page = new SmsPage();
        try
        {
            Assert.True(await modem.ConnectAsync("fake-port"));
            page.OnActivated(modem);
            page.OnConnectionChanged(true);
            page.SetInboxSnapshotForTest([new SmsMessage
            {
                Number = "+15551234567", Body = "appointment reminder", Direction = SmsDirection.Incoming,
                Timestamp = DateTime.Now,
            }]);
            Assert.Equal("••••••", page.NumberBox.Text);
            Assert.Equal("+15551234567", page.CurrentRecipientForTest);
            page.ShowOverride = (_, _) => Task.CompletedTask;

            page.CallButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await page.WaitForActiveOperationsForTestAsync();

            Assert.Contains("ATD+15551234567;", channel.Commands);
            Assert.DoesNotContain(channel.Commands, command => command.Contains("••••••", StringComparison.Ordinal));
        }
        finally
        {
            await page.DisposeAsync();
            AppSettings.ResetForTests();
            AppSettings.ConfigPathOverride = previousSettingsPath;
            try { Directory.Delete(settingsDirectory, recursive: true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task SendButtonRoutesThroughModemAndClearsComposeAfterSuccessfulCmgs()
    {
        var channel = new CallsDeviceTestChannel();
        await using var modem = new ModemManager(new CallsDeviceTestProvider(channel));
        var page = new SmsPage();
        try
        {
            Assert.True(await modem.ConnectAsync("fake-port"));
            page.OnActivated(modem);
            page.OnConnectionChanged(true);
            page.NumberBox.Text = "+15551234567";
            page.BodyBox.Text = "hello from UI";

            page.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await page.WaitForActiveOperationsForTestAsync();

            var commands = channel.Commands.ToArray();
            Assert.Single(commands, command => command.StartsWith("AT+CMGS=", StringComparison.Ordinal));
            var submit = Assert.Single(commands, command => command.EndsWith('\u001A'));
            Assert.StartsWith("00", submit);
            Assert.Empty(page.BodyBox.Text ?? string.Empty);
            Assert.Contains(page.ThreadList.ItemsSource!.Cast<ThreadItem>(), item => item.Count == 1);
        }
        finally { await page.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task ExportButtonsWriteCoreCsvAndJsonFormatsIntoPickedStreamsAndTruncate()
    {
        var page = new SmsPage();
        var csv = new MemoryStream();
        var json = new MemoryStream();
        try
        {
            var prior = Encoding.UTF8.GetBytes("old file content that is longer than the export");
            csv.Write(prior); json.Write(prior);
            page.SetInboxSnapshotForTest([new SmsMessage
            {
                Number = "+15551234567", Body = "hello, \"world\"", VerificationCode = "123456",
                Direction = SmsDirection.Incoming, Timestamp = new DateTime(2025, 1, 2, 3, 4, 5),
            }]);
            page.ExportPickerOverride = format => Task.FromResult<(string Name, Stream Stream)?>(
                format == "csv" ? ("sms.csv", csv) : ("sms.json", json));
            page.ShowOverride = (_, _) => Task.CompletedTask;

            page.ExportCsvButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await page.WaitForActiveOperationsForTestAsync();
            page.ExportJsonButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await page.WaitForActiveOperationsForTestAsync();

            var csvBytes = csv.ToArray();
            var csvText = Encoding.UTF8.GetString(csvBytes);
            Assert.StartsWith("\uFEFF", csvText);
            Assert.Contains("时间,方向,号码,验证码,正文", csvText);
            Assert.Contains("\"hello, \"\"world\"\"\"", csvText);
            Assert.DoesNotContain("old file content", csvText);
            var jsonText = Encoding.UTF8.GetString(json.ToArray());
            Assert.StartsWith("[", jsonText);
            Assert.Contains("\"direction\": \"in\"", jsonText);
            Assert.Contains("hello, \\\"world\\\"", jsonText);
            Assert.DoesNotContain("old file content", jsonText);

            var untouched = new MemoryStream();
            untouched.Write(Encoding.UTF8.GetBytes("leave this file untouched"));
            var beforeCancel = untouched.ToArray();
            page.ExportPickerOverride = _ => Task.FromResult<(string Name, Stream Stream)?>(null);
            page.ExportCsvButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await page.WaitForActiveOperationsForTestAsync();
            Assert.Equal(beforeCancel, untouched.ToArray());
        }
        finally { await page.DisposeAsync(); }
    }
}
