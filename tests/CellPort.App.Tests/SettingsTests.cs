using CellPort.App.Views;
using Avalonia.Headless.XUnit;

namespace CellPort.App.Tests;

public sealed class SettingsTests
{
    [AvaloniaFact]
    public void NewSettingsDefaultDarkButExplicitSystemChoiceIsPreserved()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CellPort.App.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        try
        {
            Assert.Equal(1, AppSettings.LoadFromPath(path).ThemeIndex);
            File.WriteAllText(path, "{\"ThemeIndex\":0}");
            Assert.Equal(0, AppSettings.LoadFromPath(path).ThemeIndex);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [AvaloniaFact]
    public void SettingsJsonRoundTripsOriginalFieldsAndDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CellPort.App.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var settings = new AppSettings
            {
                ThemeIndex = 2, PrivacyMode = true, AutoConnect = false, RefreshIntervalSeconds = 17,
                BarkUrl = "https://example.test/bark", FeishuWebhook = "https://example.test/feishu",
                DingTalkWebhook = "https://example.test/dingtalk", LastGoodSerialPort = "/dev/cu.at",
                LastConnectedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local),
            };
            Assert.True(settings.SaveToPath(path));
            var loaded = AppSettings.LoadFromPath(path);
            Assert.Equal(settings.ThemeIndex, loaded.ThemeIndex);
            Assert.Equal(settings.PrivacyMode, loaded.PrivacyMode);
            Assert.Equal(settings.AutoConnect, loaded.AutoConnect);
            Assert.Equal(settings.RefreshIntervalSeconds, loaded.RefreshIntervalSeconds);
            Assert.Equal(settings.BarkUrl, loaded.BarkUrl);
            Assert.Equal(settings.FeishuWebhook, loaded.FeishuWebhook);
            Assert.Equal(settings.DingTalkWebhook, loaded.DingTalkWebhook);
            Assert.Equal(settings.LastGoodSerialPort, loaded.LastGoodSerialPort);
            Assert.Equal(settings.LastConnectedAt, loaded.LastConnectedAt);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [AvaloniaFact]
    public void TemporaryDesktopSettingsMigrateOnlyWhenCanonicalSettingsAreAbsent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CellPort.App.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        var fallback = Path.Combine(dir, "desktop-settings.json");
        try
        {
            File.WriteAllText(fallback, "{\"LastGoodSerialPort\":\"/dev/cu.old\",\"AutoConnect\":false,\"Theme\":\"Light\"}");
            var migrated = AppSettings.LoadFromPath(path);
            Assert.Equal("/dev/cu.old", migrated.LastGoodSerialPort);
            Assert.False(migrated.AutoConnect);
            Assert.Equal(2, migrated.ThemeIndex);
            Assert.False(File.Exists(path));

            File.WriteAllText(fallback, "{\"Theme\":\"System\"}");
            var migratedSystemTheme = AppSettings.LoadFromPath(path);
            Assert.Equal(OperatingSystem.IsMacOS() ? 1 : 0, migratedSystemTheme.ThemeIndex);

            File.WriteAllText(path, "{\"ThemeIndex\":1,\"LastGoodSerialPort\":\"COM9\"}");
            var canonical = AppSettings.LoadFromPath(path);
            Assert.Equal(1, canonical.ThemeIndex);
            Assert.Equal("COM9", canonical.LastGoodSerialPort);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
