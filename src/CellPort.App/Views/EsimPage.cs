using System.Text;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CellPort.App.Services;
using CellPort.Core.Models;
using CellPort.Core.Services;
using CellPort.Core.At;

namespace CellPort.App.Views;

/// <summary>eUICC capability, EID, profile details and guarded profile management.</summary>
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
    private readonly TextBlock _hint = new() { Text = "模块未连接。请先连接模块。", TextWrapping = TextWrapping.Wrap };
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
        if (!connected) { _profiles.Children.Clear(); _eid.Text = "--"; _iccid.Text = "--"; _hint.Text = "模块未连接。请先在左下角连接模块。"; }
    }

    private async Task RefreshListAsync()
        => await RefreshListAsync(false);

    private async Task RefreshListAsync(bool duringMutation)
    {
        if (_busy && !duringMutation) return;
        var modem = _modem;
        if (modem is null || !modem.IsConnected) { _hint.Text = "模块未连接。"; return; }
        var info = modem.Info;
        if (info is null) { _hint.Text = "模块信息服务未就绪。"; return; }
        var ownsBusy = !_busy;
        if (ownsBusy) _busy = true;
        RefreshButton.IsEnabled = false; RefreshButton.Content = "读取中…";
        _profiles.Children.Clear(); _hint.Text = "正在打开 eUICC 逻辑通道…";
        try
        {
            _iccid.Text = modem.Status.Iccid ?? "--";
            var result = await info.ProbeEuiccAsync(_lifetime.Token);
            if (_disposed || !ReferenceEquals(modem, _modem) || !modem.IsConnected || !ReferenceEquals(info, modem.Info)) return;
            switch (result.Capability)
            {
                case EuiccCapability.Ready: RenderReady(result); break;
                case EuiccCapability.ApduLengthLimited: ShowModuleLimit(); break;
                case EuiccCapability.IsdrNotFound: ShowCardNotEuicc(result.Detail); break;
                default: ShowChannelIssue(); break;
            }
            AddCardInfo();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _hint.Text = $"读取失败：{ex.Message}"; }
        finally { RefreshButton.Content = "读取"; if (ownsBusy) _busy = false; RefreshButton.IsEnabled = _modem?.IsConnected == true && !_busy && !_disposed; }
    }

    private void RenderReady(DeviceInfoService.EuiccProbeResult result)
    {
        if (string.IsNullOrEmpty(result.Eid))
        {
            _eid.Text = "此卡未提供（ISD-R 不支持读取）";
            AddCard("EID 无法读取（仅影响显示，不影响 Profile 管理）", "已成功打开 ISD-R 逻辑通道并读取 Profile，但该卡的 ISD-R 对 ES10c GetEuiccData BF3E / SGP.02 GET DATA 9F7F 两种 EID 读取方式均返回错误。这常见于 9eSIM 等 SGP.02 早期规范的可写卡。", null);
        }
        else _eid.Text = result.Eid;
        _hint.Text = result.Profiles.Count == 0
            ? $"已连接 eUICC（{result.Detail}），卡内暂无 Profile。"
            : $"已连接 eUICC（{result.Detail}），共 {result.Profiles.Count} 个 Profile。启用或停用后可选择重启模块使新卡生效。";
        foreach (var profile in result.Profiles) AddProfile(profile);
        if (result.Profiles.Count == 0) AddCard("卡内暂无 Profile", "eUICC 工作正常，但尚未下载任何 Profile。可通过 LPA（下载二维码）向该卡添加 eSIM Profile。", null);
    }

    private void AddProfile(EsimProfileInfo profile)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrWhiteSpace(profile.IsdpAid))
        {
            AddAction(actions, profile.Enabled ? "停用" : "启用", () => TrackAsync(() => ToggleAsync(profile)));
            AddAction(actions, "删除", () => TrackAsync(() => DeleteAsync(profile)));
        }
        if (!string.IsNullOrWhiteSpace(profile.Iccid)) AddAction(actions, "改名", () => TrackAsync(() => RenameAsync(profile)));
        AddCard(profile.DisplayName, BuildProfileDetail(profile), profile.Enabled ? "已启用" : null, actions);
    }

    private async Task ToggleAsync(EsimProfileInfo profile)
    {
        var modem = _modem;
        if (_busy || !IsCurrentReady(modem)) { _hint.Text = "模块未连接或 eUICC 通道未就绪。"; return; }
        var engine = modem!.Engine;
        var info = modem.Info;
        var channel = info?.Es10;
        if (engine is null || info is null || channel is null) { _hint.Text = "eUICC 通道未就绪，请先点「读取」完成探测。"; return; }
        _busy = true; RefreshButton.IsEnabled = false;
        try
        {
            var enable = !profile.Enabled;
            var prompt = enable
                ? $"启用「{profile.DisplayName}」？\n\n单激活卡会自动停用当前已启用的 Profile；写入后需重启模块（约 40 秒）才能用新卡上网。"
                : $"停用「{profile.DisplayName}」？\n\n停用后该 Profile 无法上网；写入后需重启模块（约 40 秒）生效。";
            if (!await ConfirmAsync(prompt, enable ? "启用 Profile" : "停用 Profile")) return;
            if (!IsSameSession(modem, engine, info, channel)) { _hint.Text = "设备连接已变化，请重新读取后再试。"; return; }
            _hint.Text = $"正在{(enable ? "启用" : "停用")}「{profile.DisplayName}」…";
            var result = await modem!.SetEsimProfileStateAsync(profile.IsdpAid!, enable, _lifetime.Token);
            if (!IsCurrent(modem)) return;
            if (!result.Ok) { await DialogService.ShowAsync(this, $"写入失败：{result.Message}（code={result.Code}）", "切换失败"); return; }
            var restart = await ConfirmAsync(
                "已写入 eUICC。需要重启模块（AT+CFUN=1,1，约 40 秒）新卡才会生效，重启后会自动重连并刷新列表。\n\n立即重启吗？", "写入成功");
            if (restart && IsSameSession(modem, engine, info, channel)) await RestartAndReconnectAsync(modem);
            if (IsCurrentReady(modem)) await RefreshListAsync(true);
            else if (!_lifetime.IsCancellationRequested) _hint.Text = restart
                ? "模块未能恢复连接。请检查模块供电后，点左下角「连接模块」重试。"
                : "已写入，模块重启后生效。可手动重启模块或重新连接。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _hint.Text = $"切换失败：{ex.Message}"; }
        finally { _busy = false; RefreshButton.IsEnabled = _modem?.IsConnected == true && !_disposed; }
    }

    private async Task RenameAsync(EsimProfileInfo profile)
    {
        var modem = _modem;
        if (_busy || string.IsNullOrWhiteSpace(profile.Iccid) || !IsCurrentReady(modem)) return;
        _busy = true; RefreshButton.IsEnabled = false;
        try
        {
            var info = modem!.Info!; var channel = info.Es10;
            if (channel is null) { await DialogService.ShowAsync(this, "eUICC 通道未就绪，请先点「读取」完成探测。", "无法操作"); return; }
            var nickname = await DialogService.InputAsync(this, $"为「{profile.DisplayName}」设置昵称（不超过 64 个英文字符，留空清除）：", profile.Nickname ?? string.Empty);
            if (nickname is null) return;
            if (!IsCurrentReady(modem) || !ReferenceEquals(info, modem.Info) || !ReferenceEquals(channel, info.Es10)) { _hint.Text = "设备连接已变化，请重新读取后再试。"; return; }
            var admission = await modem.TryAcquireOperationAsync(ModuleOperationKind.EsimModification, cancellationToken: _lifetime.Token);
            if (!admission.Allowed) { await DialogService.ShowAsync(this, $"设备正忙，无法修改 Profile：{admission.ErrorCode}", "改名失败"); return; }
            await using var lease = admission.Lease;
            if (!IsCurrentReady(modem) || !ReferenceEquals(info, modem.Info) || !ReferenceEquals(channel, info.Es10)) { _hint.Text = "设备连接已变化，请重新读取后再试。"; return; }
            _hint.Text = "正在写入昵称…";
            var result = await channel.SetNicknameAsync(profile.Iccid, nickname.Trim(), _lifetime.Token);
            if (!IsCurrent(modem)) return;
            if (!result.Ok) { await DialogService.ShowAsync(this, $"写入失败：{result.Message}（code={result.Code}）", "改名失败"); return; }
            await RefreshListAsync(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _hint.Text = $"改名失败：{ex.Message}"; }
        finally { _busy = false; RefreshButton.IsEnabled = _modem?.IsConnected == true && !_disposed; }
    }

    private async Task DeleteAsync(EsimProfileInfo profile)
    {
        var modem = _modem;
        if (_busy || string.IsNullOrWhiteSpace(profile.IsdpAid) || !IsCurrentReady(modem)) return;
        _busy = true; RefreshButton.IsEnabled = false;
        try
        {
            var info = modem!.Info!; var channel = info.Es10;
            if (channel is null) { await DialogService.ShowAsync(this, "eUICC 通道未就绪，请先点「读取」完成探测。", "无法操作"); return; }
            var first = await ConfirmAsync(
                $"确定要删除「{profile.DisplayName}」吗？\n\nICCID：{profile.Iccid ?? "未知"}\n\n删除会把该 Profile 从 eUICC 中移除（不可恢复）。若以后还需要，需要重新用 LPA 下载（二维码 + 确认码）。", "删除 Profile");
            if (!first) return;
            if (!IsCurrentReady(modem) || !ReferenceEquals(info, modem.Info) || !ReferenceEquals(channel, info.Es10)) { _hint.Text = "设备连接已变化，请重新读取后再试。"; return; }
            var second = await ConfirmAsync(
                "再次确认：此操作不可撤销。\n已启用的 Profile 通常会被卡拒绝删除，建议先停用。\n\n真的要删除吗？", "最终确认");
            if (!second) return;
            if (!IsCurrentReady(modem) || !ReferenceEquals(info, modem.Info) || !ReferenceEquals(channel, info.Es10)) { _hint.Text = "设备连接已变化，请重新读取后再试。"; return; }
            var admission = await modem.TryAcquireOperationAsync(ModuleOperationKind.EsimModification, cancellationToken: _lifetime.Token);
            if (!admission.Allowed) { await DialogService.ShowAsync(this, $"设备正忙，无法删除 Profile：{admission.ErrorCode}", "删除失败"); return; }
            await using var lease = admission.Lease;
            if (!IsCurrentReady(modem) || !ReferenceEquals(info, modem.Info) || !ReferenceEquals(channel, info.Es10)) { _hint.Text = "设备连接已变化，请重新读取后再试。"; return; }
            _hint.Text = $"正在删除「{profile.DisplayName}」…";
            var result = await channel.DeleteProfileAsync(profile.IsdpAid, _lifetime.Token);
            if (!IsCurrent(modem)) return;
            if (!result.Ok)
            {
                var hint = profile.Enabled ? "\n\n该 Profile 处于启用状态，多数卡会拒绝删除，请先停用。" : string.Empty;
                await DialogService.ShowAsync(this, $"删除失败：{result.Message}（code={result.Code}）{hint}", "删除失败"); return;
            }
            await DialogService.ShowAsync(this, "该 Profile 已从卡内删除。", "删除成功");
            await RefreshListAsync(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _hint.Text = $"删除失败：{ex.Message}"; }
        finally { _busy = false; RefreshButton.IsEnabled = _modem?.IsConnected == true && !_disposed; }
    }

    private async Task RestartAndReconnectAsync(ModemManager modem)
    {
        _hint.Text = "模块重启中…";
        AtResult restartResult;
        try { restartResult = await modem.ExecuteAtAsync("AT+CFUN=1,1", _lifetime.Token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _hint.Text = $"模块重启命令失败：{ex.Message}"; return; }
        if (restartResult.Completion is AtCompletionStatus.OperationRejected or AtCompletionStatus.Error)
        { _hint.Text = $"模块拒绝重启命令：{restartResult.RawText}"; return; }
        if (_disposed || _lifetime.IsCancellationRequested || !ReferenceEquals(modem, _modem)) return;
        try { await modem.DisconnectAsync(); } catch { }
        for (var attempt = 1; attempt <= 15; attempt++)
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(modem, _modem)) return;
            _hint.Text = $"等待模块恢复（{attempt}/15）…";
            await Task.Delay(TimeSpan.FromSeconds(4), _lifetime.Token);
            try { if (await modem.ConnectAsync(null, _lifetime.Token)) return; }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
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

    private void ShowModuleLimit()
    {
        _eid.Text = "不可读取（通道限制）"; _hint.Text = "固件不支持逻辑通道指令，且 AT+CSIM 透传存在长度上限。";
        AddCard("结论：模块通道限制，与卡片无关", "本固件的 AT+CSIM 透传对单条 APDU 有约 8 字节的长度上限，且不支持 3GPP 逻辑通道指令（AT+CCHO / AT+CGLA）。建议升级模块固件。", null);
        AddCard("可行的替代方案", "升级到支持 CCHO/CGLA 的固件，或把卡插到手机或 5ber / eSIM.me 读卡器上，用厂商 App 管理 Profile。", null);
    }
    private void ShowCardNotEuicc(string? detail)
    {
        _eid.Text = "未检测到 eUICC 应用"; _hint.Text = "通道正常，但卡片中未找到 eUICC 应用（ISD-R）。";
        AddCard("卡内未找到 ISD-R 应用", (string.IsNullOrWhiteSpace(detail) ? "" : detail + "\n\n") + "AT+CCHO 未能为 ISD-R AID 打开逻辑通道，或打开后读不到 EID。这通常意味着该卡是普通物理 SIM 卡，不含 eUICC 应用。", null);
        AddCard("若你确认这是 eUICC 卡（如 9eSIM）", "可到 AT 控制台验证：AT+CCHO=\"A0000005591010FFFFFFFF8900000100\"。预期返回 +CCHO: <通道号>。", null);
    }
    private void ShowChannelIssue()
    {
        _eid.Text = "无法探测（通道无响应）"; _hint.Text = "AT 通道无响应，无法读取卡片。";
        AddCard("模块未响应卡片指令", "可能原因：SIM 卡未就绪；当前串口不是 AT 通道；模块固件未开放卡片访问指令。", null);
    }
    private void AddCardInfo()
    {
        if (_modem is null) return;
        var iccid = _modem.Status.Iccid; var imsi = _modem.Status.Imsi;
        if (string.IsNullOrWhiteSpace(iccid) && string.IsNullOrWhiteSpace(imsi)) return;
        var detail = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(iccid)) { detail.AppendLine($"ICCID  {iccid}"); var issuer = GuessIssuer(iccid); if (issuer is not null) detail.AppendLine($"发卡方  {issuer}"); }
        if (!string.IsNullOrWhiteSpace(imsi)) detail.AppendLine($"IMSI   {imsi}");
        AddCard("卡片基础信息（不依赖 eUICC）", detail.ToString().TrimEnd(), null);
    }
    private static string? GuessIssuer(string iccid) => iccid.Length < 6 ? null : iccid[..6] switch
    { "898520" => "中国电信物联网卡段", "898523" => "中国电信", "898530" => "中国联通物联网卡段", "898601" or "898600" => "中国移动", _ => null };

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
