using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using CellPort.App.Services;

[assembly: AvaloniaTestApplication(typeof(CellPort.App.Tests.TestAppBuilder))]

namespace CellPort.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        CellPort.App.Views.AppSettings.ConfigPathOverride = Path.Combine(Path.GetTempPath(), "CellPort.App.Tests", Guid.NewGuid().ToString("N"), "settings.json");
        return AppBuilder.Configure<TestApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}

public sealed class TestApplication : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        ThemeManager.Install(this);
    }
}
