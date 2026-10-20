using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;
using System.Runtime.CompilerServices;

namespace CellPort.App.Services;

public static class DialogService
{
    private static readonly ConditionalWeakTable<Window, HashSet<Window>> Owned = new();
    public static async Task ShowAsync(Control owner, string message, string title = "提示") =>
        _ = await ShowCoreAsync(owner, message, title, false, null).ConfigureAwait(true);

    public static async Task<bool> ConfirmAsync(Control owner, string message, string title = "确认") =>
        await ShowCoreAsync(owner, message, title, true, null).ConfigureAwait(true) is true;

    public static async Task<string?> InputAsync(Control owner, string prompt, string initialText, string title = "输入") =>
        await ShowCoreAsync(owner, prompt, title, false, initialText).ConfigureAwait(true) as string;

    private static Task<object?> ShowCoreAsync(Control owner, string message, string title, bool confirm, string? initial)
    {
        var window = new Window { Title = title, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(20) };
        panel.Children.Add(new ScrollViewer { MaxHeight = 520, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 400 } });
        TextBox? input = null;
        if (initial is not null) { input = new TextBox { Text = initial }; panel.Children.Add(input); }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = confirm || input is not null ? "取消" : "关闭", MinWidth = 84 };
        cancel.Click += (_, _) => window.Close(null);
        buttons.Children.Add(cancel);
        if (confirm || input is not null)
        {
            var ok = new Button { Content = "确定", MinWidth = 84, IsDefault = true };
            ok.Click += (_, _) => window.Close(confirm ? true : input!.Text ?? string.Empty);
            buttons.Children.Add(ok);
        }
        panel.Children.Add(buttons); window.Content = panel;
        if (initial is not null) window.Opened += (_, _) => input!.Focus();
        var ownerWindow = owner as Window ?? TopLevel.GetTopLevel(owner) as Window ?? MainWindow.Current;
        if (ownerWindow is null || !ownerWindow.IsVisible) return Task.FromResult<object?>(null);
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { window.Close(null); e.Handled = true; }
            else if (e.Key == Key.Enter && (confirm || input is not null)) { window.Close(confirm ? true : input!.Text ?? string.Empty); e.Handled = true; }
        };
        var set = Owned.GetOrCreateValue(ownerWindow);
        set.Add(window);
        window.Closed += (_, _) => set.Remove(window);
        return window.ShowDialog<object?>(ownerWindow);
    }

    public static void CancelOwned(Control owner)
    {
        var ownerWindow = owner as Window ?? TopLevel.GetTopLevel(owner) as Window ?? MainWindow.Current;
        if (ownerWindow is null || !Owned.TryGetValue(ownerWindow, out var set)) return;
        foreach (var dialog in set.ToArray()) dialog.Close(null);
        set.Clear();
    }
}
