using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using CellPort.App.Services;
using CellPort.App.Views;
using CellPort.Core.Services;

namespace CellPort.App;

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        ThemeManager.Install(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppLog.Initialize();
        AppLog.Info($"App 启动（v{typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?"}）");
        ThemeManager.Apply(AppSettings.Current.ThemeIndex switch
        {
            1 => AppTheme.Dark,
            2 => AppTheme.Light,
            _ => AppTheme.System,
        });
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            AppLog.Error("界面线程未处理异常", e.Exception);
            e.Handled = true;
            if (MainWindow.Current is { } window) _ = DialogService.ShowAsync(window, $"界面发生错误：\n{e.Exception.Message}", "错误");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) AppLog.Error("未处理异常（非界面线程）", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("未观察的任务异常", e.Exception);
            e.SetObserved();
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(desktop.Args ?? []);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
