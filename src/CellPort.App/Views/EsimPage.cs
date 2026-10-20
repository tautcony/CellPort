using System.Text;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CellPort.App.Services;
using CellPort.Core.Models;
using CellPort.Core.Services;
using CellPort.Core.At;

namespace CellPort.App.Views;

/// <summary>eUICC profile management.</summary>
public sealed class EsimPage : UserControl, IModuleAware, IDisposable, IAsyncDisposable
{
    private ModemManager? _modem;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _activeTasks = [];
    private bool _busy;
    private bool _disposed;
    internal Func<string, string, Task<bool>>? ConfirmOverride { get; set; }
    internal Button? FindProfileActionForTest(string label) => _profiles.Children.OfType<Border>()
        .Select(border => border.Child).OfType<Grid>()
        .SelectMany(grid => grid.Children.OfType<Panel>())
        .SelectMany(panel => panel.Children.OfType<Button>())
        .FirstOrDefault(button => string.Equals(button.Content?.ToString(), label, StringComparison.Ordinal));
    public Button RefreshButton { get; } = new() { Name = "RefreshButton", Content = "读取", IsEnabled = false };
    private readonly TextBlock _eid = new() { Text = "--", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _iccid = new() { Text = "--", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _hint = new() { Text = "模块未连接。", TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _profiles = new() { Spacing = 6 };

    public EsimPage()
    {
        var readHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Avalonia.Thickness(0, 0, 0, 10) };
        readHeader.Children.Add(new TextBlock { Text = "eSIM 管理", FontSize = 15, FontWeight = Avalonia.Media.FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        readHeader.Children.Add(RefreshButton);
        var info = new StackPanel { Spacing = 5, Margin = new Avalonia.Thickness(0, 0, 0, 10) };
        info.Children.Add(new TextBlock { Text = "EID" }); info.Children.Add(_eid);
        info.Children.Add(new TextBlock { Text = "ICCID" }); info.Children.Add(_iccid);
        var content = new StackPanel { Spacing = 9, Margin = new Avalonia.Thickness(16) };
        content.Children.Add(readHeader); content.Children.Add(info); content.Children.Add(_hint); content.Children.Add(_profiles);
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        RefreshButton.Click += async (_, _) => await TrackAsync(() => RefreshListAsync());
    }

    public void OnActivated(ModemManager modem) => _modem = modem;
    public void OnConnectionChanged(bool connected)
    {
        RefreshButton.IsEnabled = connected && !_busy;
        if (!connected) { _profiles.Children.Clear(); _eid.Text = "--"; _iccid.Text = "--"; _hint.Text = "模块未连接。"; }
    }

    private async Task RefreshListAsync()
        => await RefreshListAsync(false);

    private async Task RefreshListAsync(bool duringMutation)
    {
        if (_busy && !duringMutation) return;
        var modem = _modem;
        if (modem is null || !modem.IsConnected) { _hint.Text = "模块未连接。"; return; }
        var info = modem.Info;
        if (info is null) { _hint.Text = "读取失败"; return; }
        var ownsBusy = !_busy;
        if (ownsBusy) _busy = true;
        RefreshButton.IsEnabled = false; RefreshButton.Content = "读取中…";
        _profiles.Children.Clear(); _eid.Text = "--"; _hint.Text = "读取中…";
        try
        {
            var admission = await modem.TryAcquireOperationAsync(ModuleOperationKind.EsimModification, cancellationToken: _lifetime.Token);
            if (!admission.Allowed) { _hint.Text = "读取失败"; return; }
            await using var lease = admission.Lease;
            _iccid.Text = modem.Status.Iccid ?? "--";
            var result = await info.ProbeEuiccAsync(_lifetime.Token);
            if (_disposed || !ReferenceEquals(modem, _modem) || !modem.IsConnected || !ReferenceEquals(info, modem.Info)) return;
            switch (result.Capability)
            {
                case EuiccCapability.Ready: RenderReady(result); break;
                case EuiccCapability.ApduLengthLimited: ShowUnavailable("eUICC 不可用"); break;
                case EuiccCapability.IsdrNotFound: ShowUnavailable("未检测到 eUICC"); break;
                default: ShowUnavailable("读取失败"); break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _hint.Text = "读取失败"; }
        finally { RefreshButton.Content = "读取"; if (ownsBusy) _busy = false; RefreshButton.IsEnabled = _modem?.IsConnected == true && !_busy && !_disposed; }
    }

    private void RenderReady(DeviceInfoService.EuiccProbeResult result)
    {
        if (string.IsNullOrEmpty(result.Eid))
        {
            _eid.Text = "不可读取";
        }
        else _eid.Text = result.Eid;
        _hint.Text = result.Profiles.Count == 0
            ? "暂无 Profile"
            : $"{result.Profiles.Count} 个 Profile";
        var activeIccid = _modem?.Status.Iccid;
        if (!string.IsNullOrWhiteSpace(activeIccid))
            foreach (var profile in result.Profiles.Where(profile => string.Equals(profile.Iccid, activeIccid, StringComparison.Ordinal)))
                profile.State = 1;
        foreach (var profile in result.Profiles) AddProfile(profile);
    }

    private void AddProfile(EsimProfileInfo profile)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrWhiteSpace(profile.Iccid) && profile.State is 0 or 1)
        {
            AddAction(actions, profile.Enabled ? "停用" : "启用", () => TrackAsync(() => ToggleAsync(profile)));
            if (profile.State == 0 && !profile.IsBootstrap)
                AddAction(actions, "删除", () => TrackAsync(() => DeleteAsync(profile)));
        }
        if (!string.IsNullOrWhiteSpace(profile.Iccid)) AddAction(actions, "改名", () => TrackAsync(() => RenameAsync(profile)));
        AddCard(profile.DisplayName, BuildProfileDetail(profile), profile.Enabled ? "已启用" : null, actions);
    }

    private async Task ToggleAsync(EsimProfileInfo profile)
    {
        var modem = _modem;
        if (_busy || !IsCurrentReady(modem)) { _hint.Text = "操作失败"; return; }
        var engine = modem!.Engine;
        var info = modem.Info;
        var channel = info?.Es10;
        if (engine is null || info is null || channel is null || string.IsNullOrWhiteSpace(profile.Iccid) || profile.State is not (0 or 1)) { _hint.Text = "操作失败"; return; }
        _busy = true; RefreshButton.IsEnabled = false;
        try
        {
            var enable = !profile.Enabled;
            var action = enable ? "启用" : "停用";
            if (!IsSameSession(modem, engine, info, channel)) { _hint.Text = "操作失败"; return; }
            _hint.Text = $"{action}中…";
            var result = await modem!.SetEsimProfileStateAsync(profile.Iccid!, enable, _lifetime.Token);
            if (!IsCurrent(modem)) return;
            if (!result.Ok) { await DialogService.ShowAsync(this, $"{action}失败", "eSIM"); _hint.Text = $"{action}失败"; return; }
            await Task.Delay(TimeSpan.FromSeconds(1), _lifetime.Token);
            if (!IsCurrent(modem)) return;
            await modem.RefreshStatusAsync(_lifetime.Token);
            if (IsCurrentReady(modem)) await RefreshListAsync(true);
            _hint.Text = $"{action}成功";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _hint.Text = "操作失败"; }
        finally { _busy = false; RefreshButton.IsEnabled = _modem?.IsConnected == true && !_disposed; }
    }

    private async Task RenameAsync(EsimProfileInfo profile)
    {
        var modem = _modem;
        if (_busy || string.IsNullOrWhiteSpace(profile.Iccid) || !IsCurrentReady(modem)) return;
        _busy = true; RefreshButton.IsEnabled = false;
        try
        {
            var nickname = await DialogService.InputAsync(this, "昵称", profile.Nickname ?? string.Empty);
            if (nickname is null) return;
            if (!IsCurrentReady(modem)) { _hint.Text = "改名失败"; return; }
            _hint.Text = "改名中…";
            var result = await modem!.RenameEsimProfileAsync(profile.Iccid, nickname.Trim(), _lifetime.Token);
            if (!IsCurrent(modem)) return;
            if (!result.Ok) { await DialogService.ShowAsync(this, "改名失败", "eSIM"); _hint.Text = "改名失败"; return; }
            await RefreshListAsync(true);
            _hint.Text = "改名成功";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _hint.Text = "改名失败"; }
        finally { _busy = false; RefreshButton.IsEnabled = _modem?.IsConnected == true && !_disposed; }
    }

    private async Task DeleteAsync(EsimProfileInfo profile)
    {
        var modem = _modem;
        if (_busy || string.IsNullOrWhiteSpace(profile.Iccid) || profile.State != 0 || profile.IsBootstrap || !IsCurrentReady(modem)) return;
        _busy = true; RefreshButton.IsEnabled = false;
        try
        {
            var info = modem!.Info!; var channel = info.Es10;
            if (channel is null) { await DialogService.ShowAsync(this, "删除失败", "eSIM"); return; }
            if (!await ConfirmAsync($"删除「{profile.DisplayName}」？", "删除 Profile")) return;
            if (!IsCurrentReady(modem) || !ReferenceEquals(info, modem.Info) || !ReferenceEquals(channel, info.Es10)) { _hint.Text = "删除失败"; return; }
            _hint.Text = "删除中…";
            var result = await modem.DeleteEsimProfileAsync(profile.Iccid, _lifetime.Token);
            if (!IsCurrent(modem)) return;
            if (!result.Ok)
            {
                await DialogService.ShowAsync(this, "删除失败", "eSIM"); _hint.Text = "删除失败"; return;
            }
            await RefreshListAsync(true);
            _hint.Text = result.Warning is null ? "删除成功" : "删除成功，通知未处理";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _hint.Text = "删除失败"; }
        finally { _busy = false; RefreshButton.IsEnabled = _modem?.IsConnected == true && !_disposed; }
    }

    private bool IsCurrent(ModemManager? modem) => !_disposed && modem is not null && ReferenceEquals(modem, _modem) && modem.IsConnected;
    private bool IsCurrentReady(ModemManager? modem) => IsCurrent(modem) && modem!.Info is not null;
    private bool IsSameSession(ModemManager modem, AtEngine engine, DeviceInfoService info, LogicalChannelEs10 channel) =>
        IsCurrentReady(modem) && ReferenceEquals(engine, modem.Engine) && ReferenceEquals(info, modem.Info) && ReferenceEquals(channel, info.Es10);
    private Task<bool> ConfirmAsync(string message, string title) => ConfirmOverride?.Invoke(message, title) ?? DialogService.ConfirmAsync(this, message, title);

    private static string BuildProfileDetail(EsimProfileInfo p)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(p.Iccid)) sb.Append("ICCID  ").AppendLine(p.Iccid);
        if (!string.IsNullOrWhiteSpace(p.ServiceProvider)) sb.Append("运营商  ").AppendLine(p.ServiceProvider);
        if (!string.IsNullOrWhiteSpace(p.IsdpAid)) sb.Append("ISD-P  ").AppendLine(p.IsdpAid);
        sb.Append("状态    ").Append(p.StateText);
        if (!string.IsNullOrEmpty(p.ClassText)) sb.Append(" · ").Append(p.ClassText).Append("型");
        return sb.ToString();
    }

    private void ShowUnavailable(string message)
    {
        _eid.Text = "--";
        _hint.Text = message;
    }

    private void AddAction(Panel panel, string label, Func<Task> action)
    { var button = new Button { Content = label, MinWidth = 56 }; button.Click += async (_, _) => await action(); panel.Children.Add(button); }
    private void AddCard(string title, string detail, string? badge, Panel? actions = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Avalonia.Thickness(10) };
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Opacity = .8, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        grid.Children.Add(text);
        if (!string.IsNullOrEmpty(badge)) { var b = new TextBlock { Text = badge, Margin = new Avalonia.Thickness(10), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(b, 1); grid.Children.Add(b); }
        if (actions is not null) { Grid.SetColumn(actions, 2); grid.Children.Add(actions); }
        _profiles.Children.Add(new Border { Child = grid, Classes = { "app-card" }, Padding = new Avalonia.Thickness(10) });
    }

    private async Task TrackAsync(Func<Task> operation)
    {
        if (_disposed) return;
        var task = operation(); _activeTasks.Add(task);
        try { await task; } finally { _activeTasks.Remove(task); }
    }
    internal async Task WaitForActiveOperationsForTestAsync() { try { await Task.WhenAll(_activeTasks.ToArray()); } catch (OperationCanceledException) { } }
    public void CancelOperations() { if (!_lifetime.IsCancellationRequested) _lifetime.Cancel(); }
    public void Dispose() { if (_disposed) return; _disposed = true; CancelOperations(); }
    public async ValueTask DisposeAsync() { Dispose(); try { await Task.WhenAll(_activeTasks.ToArray()); } catch (OperationCanceledException) { } _lifetime.Dispose(); }
}
