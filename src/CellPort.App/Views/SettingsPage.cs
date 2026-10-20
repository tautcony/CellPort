using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using CellPort.App.Services;
using CellPort.Core.Services;

namespace CellPort.App.Views;

public sealed class SettingsPage : UserControl, IModuleAware, IDisposable, IAsyncDisposable
{
    private readonly ComboBox _theme = new() { Name = "ThemeBox", ItemsSource = new[] { "跟随系统", "深色", "浅色" }, Width = 220 };
    private readonly CheckBox _privacy = new() { Name = "PrivacyBox", Content = "隐私模式（隐藏敏感信息）" };
    private readonly CheckBox _autoConnect = new() { Name = "AutoConnectBox", Content = "启动时自动连接" };
    private readonly TextBox _refresh = new() { Name = "RefreshIntervalBox", Width = 220 };
    private readonly CheckBox _forwarding = new() { Name = "SmsForwardingBox", Content = "启用短信转发（默认关闭）" };
    private readonly TextBox _bark = new() { Name = "BarkBox", PlaceholderText = "Bark Webhook URL" };
    private readonly TextBox _feishu = new() { Name = "FeishuBox", PlaceholderText = "飞书 Webhook URL" };
    private readonly TextBox _dingtalk = new() { Name = "DingTalkBox", PlaceholderText = "钉钉 Webhook URL" };
    private readonly Button _checkUpdates = new() { Name = "CheckUpdateButton", Content = "检查更新" };
    private readonly CancellationTokenSource _operationsCts = new();
    private Task? _updateTask;
    private bool _disposed;
    private bool _loading;
    public SettingsPage()
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Avalonia.Thickness(16), MaxWidth = 620 };
        panel.Children.Add(new TextBlock { Text = "设置", FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = $"当前版本 v{typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?"}" });
        panel.Children.Add(new TextBlock { Text = $"设置文件：{AppSettings.ConfigPath}\n日志目录：{AppLog.LogDirectory}", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(Label("主题", _theme)); panel.Children.Add(_privacy); panel.Children.Add(_autoConnect);
        panel.Children.Add(Label("状态刷新间隔（秒，1–3600）", _refresh));
        panel.Children.Add(new TextBlock { Text = "短信转发", FontSize = 15, FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Avalonia.Thickness(0, 8, 0, 0) });
        panel.Children.Add(new TextBlock
        {
            Text = "开启后收到的每条短信（含验证码等敏感内容）都会发送到下方 webhook，请确认信任目标服务后再启用。",
            FontSize = 12,
            Opacity = .75,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        panel.Children.Add(_forwarding);
        panel.Children.Add(_bark); panel.Children.Add(_feishu); panel.Children.Add(_dingtalk);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var save = new Button { Content = "保存设置" }; save.Click += async (_, _) => await SaveAsync();
        _checkUpdates.Click += (_, _) => { if (_updateTask is not { IsCompleted: false }) _updateTask = CheckUpdatesAsync(); };
        actions.Children.Add(save); actions.Children.Add(_checkUpdates); panel.Children.Add(actions);
        Content = new ScrollViewer { Content = panel };
        _theme.SelectionChanged += (_, _) => { if (!_loading && _theme.SelectedIndex >= 0) { AppSettings.Current.ThemeIndex = _theme.SelectedIndex; ThemeManager.Apply((AppTheme)_theme.SelectedIndex); AppSettings.Current.Save(); } };
        _privacy.IsCheckedChanged += (_, _) => { if (!_loading) { AppSettings.Current.PrivacyMode = _privacy.IsChecked == true; AppSettings.Current.Save(); } };
        LoadSettings();
    }

    public void OnActivated(ModemManager modem) { }
    public void OnConnectionChanged(bool connected) { }
    private static Control Label(string label, Control control) => new StackPanel { Spacing = 5, Children = { new TextBlock { Text = label }, control } };
    private void LoadSettings()
    {
        _loading = true; var s = AppSettings.Current;
        _theme.SelectedIndex = Math.Clamp(s.ThemeIndex, 0, 2); _privacy.IsChecked = s.PrivacyMode; _autoConnect.IsChecked = s.AutoConnect;
        _forwarding.IsChecked = s.SmsForwardingEnabled;
        _refresh.Text = s.RefreshIntervalSeconds.ToString(); _bark.Text = s.BarkUrl ?? ""; _feishu.Text = s.FeishuWebhook ?? ""; _dingtalk.Text = s.DingTalkWebhook ?? "";
        _loading = false;
    }
    private async Task SaveAsync()
    {
        var s = AppSettings.Current; s.AutoConnect = _autoConnect.IsChecked == true;
        s.SmsForwardingEnabled = _forwarding.IsChecked == true;
        s.BarkUrl = EmptyToNull(_bark.Text); s.FeishuWebhook = EmptyToNull(_feishu.Text); s.DingTalkWebhook = EmptyToNull(_dingtalk.Text);
        if (int.TryParse(_refresh.Text, out var seconds) && seconds is >= 1 and <= 3600) s.RefreshIntervalSeconds = seconds;
        else _refresh.Text = s.RefreshIntervalSeconds.ToString();
        if (!s.SaveToPath(AppSettings.ConfigPath)) { await DialogService.ShowAsync(this, "设置保存失败，请检查目录权限。", "保存失败"); return; }
        await DialogService.ShowAsync(this, "设置已保存。");
    }
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private async Task CheckUpdatesAsync()
    {
        _checkUpdates.IsEnabled = false;
        try
        {
            var current = typeof(App).Assembly.GetName().Version ?? new Version(1, 0);
            var result = await UpdateCheckService.CheckAsync(current, _operationsCts.Token);
            if (_disposed || _operationsCts.IsCancellationRequested) return;
            if (result is null)
            {
                if (await DialogService.ConfirmAsync(this, "无法连接 GitHub。是否打开 Release 页面手动检查？", "检查更新")) OpenUrl(UpdateCheckService.ReleasesPage);
            }
            else if (result.HasUpdate)
            {
                if (await DialogService.ConfirmAsync(this, $"发现新版本 {result.LatestTag}（当前 v{current.ToString(3)}）。前往下载页面吗？", "发现更新")) OpenUrl(result.ReleaseUrl!);
            }
            else await DialogService.ShowAsync(this, $"已是最新版本（v{current.ToString(3)}，远端 {result.LatestTag}）。", "检查更新");
        }
        catch (OperationCanceledException) when (_operationsCts.IsCancellationRequested) { }
        catch (Exception ex) { AppLog.Warn($"检查更新失败：{ex.Message}"); if (!_disposed) await DialogService.ShowAsync(this, ex.Message, "检查更新失败"); }
        finally { _checkUpdates.IsEnabled = true; _checkUpdates.Content = "检查更新"; }
    }
    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    public void Dispose() { if (_disposed) return; _disposed = true; _operationsCts.Cancel(); }
    public async ValueTask DisposeAsync() { Dispose(); if (_updateTask is not null) try { await _updateTask; } catch (OperationCanceledException) { } _operationsCts.Dispose(); }
}

/// <summary>Persisted settings retain the original settings.json fields and defaults.</summary>
public sealed class AppSettings
{
    private static readonly Lock SaveGate = new();
    private static AppSettings? _current;
    internal static string? ConfigPathOverride { get; set; }
    public static event EventHandler? SettingsChanged;
    public static AppSettings Current => _current ??= Load();
    public int ThemeIndex { get; set; } = 1;
    public bool PrivacyMode { get; set; }
    public bool AutoConnect { get; set; } = true;
    public int RefreshIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// 是否启用短信转发。默认关闭：短信常含验证码等敏感信息，
    /// 外发必须是用户的显式选择。
    /// </summary>
    public bool SmsForwardingEnabled { get; set; }

    public string? BarkUrl { get; set; }
    public string? FeishuWebhook { get; set; }
    public string? DingTalkWebhook { get; set; }

    /// <summary>是否至少配置了一个转发目标。</summary>
    public bool HasForwardingTarget =>
        !string.IsNullOrWhiteSpace(BarkUrl) ||
        !string.IsNullOrWhiteSpace(FeishuWebhook) ||
        !string.IsNullOrWhiteSpace(DingTalkWebhook);

    public string? LastGoodSerialPort { get; set; }
    public DateTime? LastConnectedAt { get; set; }
    internal static string ConfigPath => ConfigPathOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CellPort", "settings.json");
    internal static void ResetForTests() { _current = null; SettingsChanged = null; }

    public static AppSettings Load() => LoadFromPath(ConfigPath);
    internal static AppSettings LoadFromPath(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
            var fallback = Path.Combine(Path.GetDirectoryName(path) ?? "", "desktop-settings.json");
            if (File.Exists(fallback))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(fallback)); var root = doc.RootElement;
                var migrated = new AppSettings();
                if (root.TryGetProperty("LastGoodSerialPort", out var port)) migrated.LastGoodSerialPort = port.GetString();
                if (root.TryGetProperty("AutoConnect", out var auto)) migrated.AutoConnect = auto.GetBoolean();
                if (root.TryGetProperty("Theme", out var theme)) migrated.ThemeIndex = theme.GetString()?.ToLowerInvariant() switch
                {
                    "dark" => 1,
                    "light" => 2,
                    "system" when OperatingSystem.IsMacOS() => 1,
                    _ => 0,
                };
                return migrated;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { AppLog.Warn($"读取设置失败，使用默认值：{ex.Message}"); }
        return new AppSettings();
    }
    public void Save() => SaveToPath(ConfigPath);
    internal bool SaveToPath(string path)
    {
        lock (SaveGate)
        {
            return SaveToPathLocked(path);
        }
    }

    private bool SaveToPathLocked(string path)
    {
        string? temp = null;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

            // 先写临时文件再原子替换：直接覆盖时若掉电/崩溃会留下截断的 JSON，
            // 下次启动会静默回落到默认值，用户配置全部丢失。
            temp = path + ".tmp";
            File.WriteAllText(temp, json);
            RestrictPermissions(temp);

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temp, path, destinationBackupFileName: null);
                }
                catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
                {
                    // 某些文件系统（容器卷/网络盘）不支持 File.Replace，退化为覆盖移动。
                    File.Move(temp, path, overwrite: true);
                }
            }
            else
            {
                File.Move(temp, path);
            }

            try { SettingsChanged?.Invoke(this, EventArgs.Empty); } catch (Exception ex) { AppLog.Warn($"通知设置变更失败：{ex.Message}"); }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or PlatformNotSupportedException)
        {
            if (temp is not null)
            {
                try { File.Delete(temp); } catch { }
            }
            AppLog.Warn($"保存设置失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 收紧配置文件权限：webhook URL 通常内嵌 token，不应让同机其它用户读取。
    /// Windows 上由 %APPDATA% 的 ACL 提供同等保护。
    /// </summary>
    private static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
