using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CellPort.Core.Services;
using System.Text;

namespace CellPort.App.Views;

public sealed class ConsolePage : UserControl, IModuleAware, IDisposable, IAsyncDisposable
{
    private static readonly string[] QuickCommands = ["AT", "ATI", "AT+CGMM", "AT+CGMR", "AT+CGSN", "AT+CPIN?", "AT+CSQ", "AT+COPS?", "AT+CREG?", "AT+QNWINFO", "AT+CGATT?", "AT+QCCID", "AT+CIMI", "AT+CNUM", "AT+CMGF?", "AT+CPMS?", "AT+QCFG=\"usbnet\"", "AT+QCFG=\"usbcfg\"", "AT+CFUN?", "AT+QENG=\"servingcell\""];
    private readonly Button _run = new() { Name = "RunButton", Content = "执行", IsEnabled = false };
    private readonly StringBuilder _log = new();
    private readonly List<string> _history = [];
    private readonly CancellationTokenSource _operationsCts = new();
    private ModemManager? _modem;
    private int _historyIndex;
    private bool _disposed;
    private bool _busy;
    private Task? _activeCommand;

    public ConsolePage()
    {
        var quick = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
        foreach (var cmd in QuickCommands)
        {
            var button = new Button { Content = cmd, Margin = new Avalonia.Thickness(2), Padding = new Avalonia.Thickness(8, 4) };
            button.Click += (_, _) => { CommandInput.Text = cmd; CommandInput.Focus(); };
            quick.Children.Add(button);
        }
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), Margin = new Avalonia.Thickness(16), RowSpacing = 8 };
        var outputCard = new Border { Classes = { "app-card" }, Padding = new Avalonia.Thickness(12), Child = CommandOutput };
        Grid.SetRow(outputCard, 0);
        layout.Children.Add(outputCard);
        var quickScroller = new ScrollViewer
        {
            Content = quick,
            MaxHeight = 144,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        var quickCard = new Border { Classes = { "app-card" }, Padding = new Avalonia.Thickness(8), Child = quickScroller };
        Grid.SetRow(quickCard, 1);
        layout.Children.Add(quickCard);
        var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        inputRow.Children.Add(CommandInput); Grid.SetColumn(_run, 1); inputRow.Children.Add(_run);
        Grid.SetRow(inputRow, 2); layout.Children.Add(inputRow); Content = layout;
        _run.Click += (_, _) => { if (_activeCommand is not { IsCompleted: false }) _activeCommand = ExecuteAsync(); };
        CommandInput.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; if (_activeCommand is not { IsCompleted: false }) _activeCommand = ExecuteAsync(); }
            else if (e.Key == Key.Up && _history.Count > 0) { _historyIndex = Math.Max(0, _historyIndex - 1); CommandInput.Text = _history[_historyIndex]; CommandInput.CaretIndex = CommandInput.Text.Length; }
            else if (e.Key == Key.Down && _history.Count > 0) { _historyIndex = Math.Min(_history.Count, _historyIndex + 1); CommandInput.Text = _historyIndex == _history.Count ? "" : _history[_historyIndex]; CommandInput.CaretIndex = CommandInput.Text.Length; }
        };
        Append("info", "AT 控制台就绪。输入指令后回车执行。");
    }

    internal IReadOnlyList<string> History => _history;
    internal TextBox CommandInput { get; } = new() { Name = "CommandBox", FontFamily = new FontFamily("monospace"), PlaceholderText = "输入 AT 指令" };

    internal TextBox CommandOutput { get; } = new() { Name = "OutputText", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("monospace"), MinHeight = 0, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };

    internal Button RunButtonForTests() => _run;
    public void OnActivated(ModemManager modem)
    {
        if (ReferenceEquals(_modem, modem)) return;
        Detach(); _modem = modem; _modem.UnsolicitedLine += OnUnsolicited;
        OnConnectionChanged(modem.IsConnected);
    }
    public void OnConnectionChanged(bool connected) => _run.IsEnabled = connected && !_disposed;
    private void OnUnsolicited(object? sender, string line) => Dispatcher.UIThread.Post(() => Append("event", "⬅ " + line));

    private async Task ExecuteAsync()
    {
        if (_busy || _disposed) return;
        var command = CommandInput.Text?.Trim() ?? "";
        if (command.Length == 0) return;
        if (command.Contains('\r') || command.Contains('\n')) { Append("error", "每次只能执行一条指令。"); return; }
        if (_modem is null || !_modem.IsConnected) { Append("error", "模块未连接。"); return; }
        _history.Add(command); _historyIndex = _history.Count; CommandInput.Clear(); Append("send", "➡ " + command); _busy = true; _run.IsEnabled = false;
        try
        {
            var result = await _modem.ExecuteAtAsync(command, _operationsCts.Token);
            foreach (var line in result.Lines) Append("recv", "⬅ " + line);
            Append(result.Succeeded ? "ok" : "error", result.Succeeded ? $"OK ({result.Duration.TotalMilliseconds:F0} ms)" : $"FAILED ({result.Duration.TotalMilliseconds:F0} ms)\n{result.RawText}");
        }
        catch (OperationCanceledException) when (_operationsCts.IsCancellationRequested) { }
        catch (Exception ex) { Append("error", ex.Message); }
        finally { _busy = false; OnConnectionChanged(_modem?.IsConnected == true); }
    }
    private void Append(string kind, string text)
    {
        _log.AppendLine((kind == "send" ? "$ " : "  ") + text);
        if (_log.Length > 200_000) _log.Remove(0, 100_000);
        CommandOutput.Text = _log.ToString();
        CommandOutput.CaretIndex = CommandOutput.Text.Length;
    }
    private void Detach() { if (_modem is not null) _modem.UnsolicitedLine -= OnUnsolicited; }
    public void Dispose() { if (_disposed) return; _disposed = true; _operationsCts.Cancel(); Detach(); }
    public async ValueTask DisposeAsync() { Dispose(); if (_activeCommand is not null) try { await _activeCommand; } catch (OperationCanceledException) { } _operationsCts.Dispose(); }
}
