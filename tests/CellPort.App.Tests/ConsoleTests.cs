using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using CellPort.App.Views;
using CellPort.Core.Services;

namespace CellPort.App.Tests;

public sealed class ConsoleTests
{
    [AvaloniaFact]
    public async Task ConnectedConsoleExecutesCommandAndKeepsHistory()
    {
        var channel = new CallsDeviceTestChannel();
        var modem = new ModemManager(new CallsDeviceTestProvider(channel));
        var page = new ConsolePage();
        try
        {
            Assert.True(await modem.ConnectAsync("fake-console-port"));
            page.OnActivated(modem);
            page.CommandInput.Text = "AT+CSQ";
            page.RunButtonForTests().RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            var stopAt = DateTime.UtcNow.AddSeconds(3);
            while (page.History.Count == 0 && DateTime.UtcNow < stopAt) await Task.Delay(10);
            Assert.Equal(new[] { "AT+CSQ" }, page.History);
            Assert.Contains("AT+CSQ", channel.Commands);
            Assert.Contains("AT+CSQ", page.CommandOutput.Text);
        }
        finally
        {
            await page.DisposeAsync();
            await modem.DisposeAsync();
        }
    }
}
