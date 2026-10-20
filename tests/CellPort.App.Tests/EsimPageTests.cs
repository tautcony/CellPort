using System.Text;
using System.Net;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using CellPort.App.Views;
using CellPort.Core.Models;
using CellPort.Core.Services;
using CellPort.Core.Transport;

namespace CellPort.App.Tests;

public sealed class EsimPageTests
{
    [AvaloniaFact]
    public async Task DeletingProfileStopsWhenConfirmationIsCancelled()
    {
        await using var rig = await TestRig.CreateAsync();
        var confirmations = 0;
        rig.Page.ConfirmOverride = (_, _) => { confirmations++; return Task.FromResult(false); };
        await rig.LoadProfilesAsync();
        rig.Channel.Commands.Clear();

        var delete = Assert.IsType<Button>(rig.Page.FindProfileActionForTest("删除"));
        delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await rig.Page.WaitForActiveOperationsForTestAsync();

        Assert.Equal(1, confirmations);
        Assert.DoesNotContain(rig.Channel.Commands, IsProfileWrite);
    }

    [AvaloniaFact]
    public async Task ReconnectingDuringDeleteConfirmationDoesNotDeleteOldSessionProfile()
    {
        await using var rig = await TestRig.CreateAsync();
        var confirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDelete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmations = 0;
        rig.Page.ConfirmOverride = (_, _) =>
        {
            Interlocked.Increment(ref confirmations);
            confirmation.TrySetResult();
            return allowDelete.Task;
        };
        await rig.LoadProfilesAsync();
        rig.Channel.Commands.Clear();

        Assert.IsType<Button>(rig.Page.FindProfileActionForTest("删除"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await confirmation.Task.WaitAsync(TimeSpan.FromSeconds(5));

        rig.Page.OnConnectionChanged(false);
        await rig.Modem.DisconnectAsync();
        Assert.True(await rig.Modem.ConnectAsync("synthetic-at"));
        rig.Page.OnConnectionChanged(true);
        allowDelete.SetResult(true);
        await rig.Page.WaitForActiveOperationsForTestAsync();

        Assert.Equal(1, confirmations);
        Assert.DoesNotContain(rig.Channel.Commands, IsProfileWrite);
    }

    [AvaloniaFact]
    public async Task OnlyDisabledNonProvisioningProfilesCanBeDeleted()
    {
        foreach (var (state, profileClass, nickname, canDelete) in new[]
                 {
                     (0, 2, (string?)null, true),
                     (1, 2, (string?)null, false),
                     (0, 1, (string?)null, false),
                     (0, 2, "bootstrap", false),
                     (2, 2, (string?)null, false),
                 })
        {
            await using var rig = await TestRig.CreateAsync(state, profileClass, nickname);
            await rig.ReadProfilesAsync();
            Assert.Equal(canDelete, rig.Page.FindProfileActionForTest("删除") is not null);
        }
    }

    [AvaloniaFact]
    public async Task DeletingDisabledProfileWritesOnceAfterSingleConfirmation()
    {
        await using var rig = await TestRig.CreateAsync();
        var confirmations = 0;
        rig.Page.ConfirmOverride = (_, _) => { confirmations++; return Task.FromResult(true); };
        await rig.LoadProfilesAsync();
        rig.Channel.Commands.Clear();

        Assert.IsType<Button>(rig.Page.FindProfileActionForTest("删除"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await rig.Page.WaitForActiveOperationsForTestAsync();

        Assert.Equal(1, confirmations);
        Assert.Contains(rig.Channel.Commands, command => command.Contains("BF330C5A0A98960021436587091234", StringComparison.OrdinalIgnoreCase));
        Assert.Null(rig.Page.FindProfileActionForTest("删除"));
    }

    [AvaloniaFact]
    public async Task EnablingProfileWritesWithoutRestartPrompt()
    {
        await using var rig = await TestRig.CreateAsync();
        var prompts = 0;
        rig.Page.ConfirmOverride = (_, _) => { prompts++; return Task.FromResult(false); };
        await rig.LoadProfilesAsync();
        rig.Channel.Commands.Clear();

        Assert.IsType<Button>(rig.Page.FindProfileActionForTest("启用"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await rig.Page.WaitForActiveOperationsForTestAsync();

        Assert.Equal(0, prompts);
        Assert.Contains(rig.Channel.Commands, command => command.Contains("BF3111A00F5A0A989600214365870912348101FF", StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public async Task AlreadyActiveIccidSkipsDuplicateEnableCommand()
    {
        await using var rig = await TestRig.CreateAsync();
        await rig.LoadProfilesAsync();
        rig.Channel.ActiveIccid = "89690012345678902143";
        rig.Channel.Commands.Clear();

        Assert.IsType<Button>(rig.Page.FindProfileActionForTest("启用"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await rig.Page.WaitForActiveOperationsForTestAsync();

        Assert.DoesNotContain(rig.Channel.Commands, command => command.Contains("BF31", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(rig.Page.FindProfileActionForTest("停用"));
    }

    [AvaloniaFact]
    public async Task EnablingProfileRetriesCatBusyUpToThreeTimes()
    {
        await using var rig = await TestRig.CreateAsync();
        rig.Channel.CatBusyEnableResponses = 2;
        await rig.LoadProfilesAsync();
        rig.Channel.Commands.Clear();

        var result = await rig.Modem.SetEsimProfileStateAsync(TestChannel.ProfileIccid, enable: true);

        Assert.True(result.Ok);
        Assert.Equal(3, rig.Channel.Commands.Count(command => command.StartsWith("AT+CGLA=", StringComparison.Ordinal) &&
            command.Contains("BF31", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, rig.Channel.ProfileState);
    }

    [AvaloniaFact]
    public async Task RefreshResetAfterEnableIsConfirmedFromFreshProfileState()
    {
        await using var rig = await TestRig.CreateAsync();
        rig.Channel.DropNextEnableResponse = true;
        await rig.LoadProfilesAsync();
        rig.Channel.Commands.Clear();

        var result = await rig.Modem.SetEsimProfileStateAsync(TestChannel.ProfileIccid, enable: true);

        Assert.True(result.Ok);
        Assert.Equal(1, rig.Channel.ProfileState);
        Assert.Contains(rig.Channel.Commands, command => command.StartsWith("AT+CGLA=", StringComparison.Ordinal) &&
            command.Contains("BF31", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rig.Channel.Commands, command => command.StartsWith("AT+CGLA=", StringComparison.Ordinal) &&
            command.Contains("BF2D", StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public async Task RefreshResetAfterDisableIsConfirmedFromFreshProfileState()
    {
        await using var rig = await TestRig.CreateAsync(profileState: 1, activeIccid: TestChannel.ProfileIccid);
        rig.Channel.DropNextDisableResponse = true;
        await rig.ReadProfilesAsync();
        rig.Channel.Commands.Clear();

        var result = await rig.Modem.SetEsimProfileStateAsync(TestChannel.ProfileIccid, enable: false);

        Assert.True(result.Ok);
        Assert.Equal(0, rig.Channel.ProfileState);
        Assert.Contains(rig.Channel.Commands, command => command.StartsWith("AT+CGLA=", StringComparison.Ordinal) &&
            command.Contains("BF32", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rig.Channel.Commands, command => command.StartsWith("AT+CGLA=", StringComparison.Ordinal) &&
            command.Contains("BF2D", StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public async Task RenameOpensIsdrChannelAfterProfileReadClosedIt()
    {
        await using var rig = await TestRig.CreateAsync();
        await rig.LoadProfilesAsync();
        Assert.Equal(0, rig.Modem.Info?.Es10?.Channel);
        rig.Channel.Commands.Clear();

        var result = await rig.Modem.RenameEsimProfileAsync(TestChannel.ProfileIccid, "Travel SIM");

        Assert.True(result.Ok);
        Assert.Contains(rig.Channel.Commands, command => command.StartsWith("AT+CCHO=", StringComparison.Ordinal));
        Assert.Contains(rig.Channel.Commands, command => command.StartsWith("AT+CGLA=", StringComparison.Ordinal) &&
            command.Contains("BF29", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("900A", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, rig.Modem.Info?.Es10?.Channel);
    }

    [AvaloniaFact]
    public async Task Es10GetResponseUsesSingleFiveByteApdu()
    {
        await using var rig = await TestRig.CreateAsync();
        rig.Channel.ReturnProfileListThroughGetResponse = true;

        await rig.ReadProfilesAsync();

        Assert.Contains("AT+CGLA=1,10,\"81C0000004\"", rig.Channel.Commands);
    }

    [AvaloniaFact]
    public async Task EuiccProbeFallsBackThroughDjOneHubIsdrAidOrder()
    {
        await using var rig = await TestRig.CreateAsync();
        rig.Channel.Commands.Clear();

        var result = await rig.Modem.Info!.ProbeEuiccAsync();

        Assert.Equal(EuiccCapability.Ready, result.Capability);
        Assert.Equal(LogicalChannelEs10.StandardIsdrAidHex, rig.Modem.Info.Es10?.IsdrAidHex);
        Assert.Equal(
            [
                "AT+CCHO=\"A06573746B6D65FFFF4953442D522030\"",
                "AT+CCHO=\"A06573746B6D65FFFF4953442D522031\"",
                $"AT+CCHO=\"{LogicalChannelEs10.StandardIsdrAidHex}\""
            ],
            rig.Channel.Commands.Where(command => command.StartsWith("AT+CCHO=\"", StringComparison.Ordinal)));
    }

    [AvaloniaFact]
    public async Task EsimOperationReusesDetectedNonStandardIsdrAid()
    {
        await using var rig = await TestRig.CreateAsync();
        const string aid = "A06573746B6D65FFFF4953442D522030";
        rig.Channel.AcceptedIsdrAidHex = aid;
        var probe = await rig.Modem.Info!.ProbeEuiccAsync();
        Assert.Equal(EuiccCapability.Ready, probe.Capability);
        rig.Channel.Commands.Clear();

        var renamed = await rig.Modem.RenameEsimProfileAsync(TestChannel.ProfileIccid, "Trip");

        Assert.True(renamed.Ok);
        Assert.Contains($"AT+CCHO=\"{aid}\"", rig.Channel.Commands);
    }

    [AvaloniaFact]
    public async Task DeleteSendsNewDeleteNotificationToSmdp()
    {
        await using var rig = await TestRig.CreateAsync();
        var handler = new NotificationHandler();
        using var http = new HttpClient(handler);
        rig.Modem.Info!.EsimNotificationHttpClient = http;
        rig.Channel.EmitDeleteNotification = true;
        await rig.LoadProfilesAsync();

        var result = await rig.Modem.DeleteEsimProfileAsync(TestChannel.ProfileIccid);

        Assert.True(result.Ok);
        Assert.Null(result.Warning);
        Assert.Equal(4096, result.SpaceDeltaBytes);
        Assert.Equal("https://example.com/gsma/rsp2/es9plus/handleNotification", handler.RequestUri?.AbsoluteUri);
        Assert.Equal("gsma-rsp-lpad", handler.UserAgent);
        Assert.Equal("gsma/rsp/v2.5.0", handler.AdminProtocol);
        using var payload = JsonDocument.Parse(handler.RequestBody!);
        var pending = Convert.FromBase64String(payload.RootElement.GetProperty("pendingNotification").GetString()!);
        var pendingHex = Convert.ToHexString(pending);
        Assert.StartsWith("BF37", pendingHex);
        Assert.Contains("BF2F", pendingHex);
        Assert.Contains("DF370101", pendingHex);
        Assert.Contains(rig.Channel.Commands, command => command.StartsWith("AT+CGLA=", StringComparison.Ordinal) &&
            command.Contains("BF28", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rig.Channel.Commands, command => command.StartsWith("AT+CGLA=", StringComparison.Ordinal) &&
            command.Contains("BF2B", StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public async Task DisconnectedPageShowsReadableEmptyStateAndDisablesProfileRead()
    {
        var page = new EsimPage();
        try
        {
            page.OnConnectionChanged(false);
            Assert.Equal("RefreshButton", page.RefreshButton.Name);
            Assert.False(page.RefreshButton.IsEnabled);
        }
        finally { await page.DisposeAsync(); }
    }

    private static bool IsProfileWrite(string command) =>
        command.StartsWith("AT+CGLA=", StringComparison.Ordinal) &&
        (command.Contains("BF31", StringComparison.OrdinalIgnoreCase) ||
         command.Contains("BF32", StringComparison.OrdinalIgnoreCase) ||
         command.Contains("BF33", StringComparison.OrdinalIgnoreCase) ||
         command.Contains("BF29", StringComparison.OrdinalIgnoreCase));

    private sealed class TestRig : IAsyncDisposable
    {
        private TestRig(TestChannel channel, ModemManager modem, EsimPage page)
        { Channel = channel; Modem = modem; Page = page; }
        public TestChannel Channel { get; }
        public ModemManager Modem { get; }
        public EsimPage Page { get; }

        public static async Task<TestRig> CreateAsync(
            int profileState = 0,
            int profileClass = 2,
            string? nickname = null,
            string activeIccid = "00000000000000000000")
        {
            var channel = new TestChannel(profileState, profileClass, nickname, activeIccid);
            var modem = new ModemManager(new TestProvider(channel));
            if (!await modem.ConnectAsync("synthetic-at")) throw new InvalidOperationException("Synthetic modem failed to connect.");
            var page = new EsimPage();
            page.OnActivated(modem);
            page.OnConnectionChanged(true);
            return new TestRig(channel, modem, page);
        }

        public async Task LoadProfilesAsync()
        {
            await ReadProfilesAsync();
            Assert.True(Page.FindProfileActionForTest("删除")?.IsEnabled == true);
        }

        public async Task ReadProfilesAsync()
        {
            RefreshButtonClick();
            await Page.WaitForActiveOperationsForTestAsync();
        }

        private void RefreshButtonClick() => Page.RefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        public async ValueTask DisposeAsync()
        {
            await Page.DisposeAsync();
            await Modem.DisposeAsync();
        }
    }

    private sealed class TestProvider(TestChannel channel) : IAtChannelProvider
    {
        public StringComparer PortNameComparer => StringComparer.OrdinalIgnoreCase;
        public Task<IAtChannel?> TryOpenUsbAsync(CancellationToken ct = default) => Task.FromResult<IAtChannel?>(null);
        public Task<string?> DetectSerialAsync(IReadOnlyList<string> knownPorts, CancellationToken ct = default) => Task.FromResult<string?>("synthetic-at");
        public IAtChannel CreateSerial(string portName) => channel;
    }

    private sealed class NotificationHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }
        public string? UserAgent { get; private set; }
        public string? AdminProtocol { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            UserAgent = request.Headers.UserAgent.ToString();
            AdminProtocol = request.Headers.TryGetValues("X-Admin-Protocol", out var values)
                ? values.Single()
                : null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    string.Empty,
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class TestChannel(int profileState, int profileClass, string? nickname, string activeIccid) : IAtChannel
    {
        public const string ProfileIccid = "89690012345678902143";
        private readonly Lock _gate = new();
        private byte[] _reply = [];
        private bool _profileDeleted;
        public int ProfileState { get; set; } = profileState;
        public int ProfileClass { get; } = profileClass;
        public string? Nickname { get; } = nickname;
        public string ActiveIccid { get; set; } = activeIccid;
        public string AcceptedIsdrAidHex { get; set; } = LogicalChannelEs10.StandardIsdrAidHex;
        public int CatBusyEnableResponses { get; set; }
        public bool DropNextEnableResponse { get; set; }
        public bool DropNextDisableResponse { get; set; }
        public bool EmitDeleteNotification { get; set; }
        public bool ReturnProfileListThroughGetResponse { get; set; }
        private bool _getResponsePending;
        public int FreeNvramBytes { get; private set; } = 123456;
        public string Description => "synthetic eSIM test channel";
        public AtChannelKind Kind => AtChannelKind.SerialPort;
        public bool IsOpen { get; private set; }
        public List<string> Commands { get; } = [];
        public Task OpenAsync(CancellationToken ct = default) { IsOpen = true; return Task.CompletedTask; }
        public Task CloseAsync() { IsOpen = false; return Task.CompletedTask; }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            var command = Encoding.ASCII.GetString(data.Span).TrimEnd('\r', '\n');
            var (payload, final) = Response(command);
            if (command.Contains("BF33", StringComparison.OrdinalIgnoreCase))
            {
                _profileDeleted = true;
                FreeNvramBytes += 4096;
            }
            lock (_gate) { Commands.Add(command); _reply = Encoding.ASCII.GetBytes($"\r\n{payload}{final}\r\n"); }
            return Task.CompletedTask;
        }

        public Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var size = Math.Min(buffer.Length, _reply.Length);
                _reply.AsMemory(0, size).CopyTo(buffer); _reply = _reply[size..];
                return Task.FromResult(size);
            }
        }

        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }

        private (string Payload, string Final) Response(string command)
        {
            if (command == "AT+CPIN?") return ("+CPIN: READY\r\n", "OK");
            if (command == "AT+QCCID") return ($"+QCCID: {ActiveIccid}\r\n", "OK");
            if (command.StartsWith("AT+CCHO=\"", StringComparison.Ordinal))
                return command.EndsWith(AcceptedIsdrAidHex + "\"", StringComparison.OrdinalIgnoreCase)
                    ? ("+CCHO: 1\r\n", "OK")
                    : (string.Empty, "ERROR");
            if (!command.StartsWith("AT+CGLA=", StringComparison.Ordinal)) return (string.Empty, "OK");
            var match = System.Text.RegularExpressions.Regex.Match(command, "\\\"([0-9A-F]+)\\\"$");
            if (!match.Success) return (string.Empty, "OK");
            var apdu = match.Groups[1].Value;
            if (_getResponsePending && apdu.StartsWith("81C0", StringComparison.OrdinalIgnoreCase))
            {
                _getResponsePending = false;
                return (CGLA(BuildProfileList() + "9000"), "OK");
            }
            var operation = new[] { "BF31", "BF32", "BF33", "BF29" }
                .FirstOrDefault(tag => apdu.Contains(tag, StringComparison.OrdinalIgnoreCase));
            if (apdu.Contains("BF31", StringComparison.OrdinalIgnoreCase))
            {
                if (DropNextEnableResponse)
                {
                    DropNextEnableResponse = false;
                    ProfileState = 1;
                    ActiveIccid = ProfileIccid;
                    return (string.Empty, "ERROR");
                }
                if (CatBusyEnableResponses > 0)
                {
                    CatBusyEnableResponses--;
                    return (CGLA("BF31038001059000"), "OK");
                }
                ProfileState = 1;
                ActiveIccid = ProfileIccid;
            }
            else if (apdu.Contains("BF32", StringComparison.OrdinalIgnoreCase))
            {
                if (DropNextDisableResponse)
                {
                    DropNextDisableResponse = false;
                    ProfileState = 0;
                    ActiveIccid = "00000000000000000000";
                    return (string.Empty, "ERROR");
                }
                ProfileState = 0;
                ActiveIccid = "00000000000000000000";
            }

            if (apdu.Contains("BF28", StringComparison.OrdinalIgnoreCase))
            {
                var notifications = _profileDeleted && EmitDeleteNotification
                    ? BuildNotificationList()
                    : "BF2802A000";
                return (CGLA(notifications + "9000"), "OK");
            }
            if (apdu.Contains("BF2B", StringComparison.OrdinalIgnoreCase))
            {
                var pending = EmitDeleteNotification ? BuildPendingNotification() : "BF2B02A000";
                return (CGLA(pending + "9000"), "OK");
            }
            if (apdu.Contains("BF22", StringComparison.OrdinalIgnoreCase))
                return (CGLA($"BF2207A4058203{FreeNvramBytes:X6}9000"), "OK");

            if (apdu.Contains("BF2D", StringComparison.OrdinalIgnoreCase) && ReturnProfileListThroughGetResponse)
            {
                _getResponsePending = true;
                return (CGLA("6104"), "OK");
            }

            var response = apdu.Contains("BF3E", StringComparison.OrdinalIgnoreCase) ? "6A80"
                : apdu.Contains("CA9F7F", StringComparison.OrdinalIgnoreCase) ? "6A82"
                : apdu.Contains("BF2D", StringComparison.OrdinalIgnoreCase)
                    ? _profileDeleted ? "BF2D02A0009000" : BuildProfileList()
                    : operation is not null ? $"{operation}038001009000" : "9000";
            return (CGLA(response), "OK");
        }

        private static string CGLA(string response) => $"+CGLA: {response.Length / 2},\"{response}\"\r\n";

        private string BuildProfileList()
        {
            var profile = $"5A0A989600214365870912344F10A0000005591010FFFFFFFF89000001009F7001{ProfileState:X2}9501{ProfileClass:X2}";
            if (!string.IsNullOrWhiteSpace(Nickname))
            {
                var nicknameHex = Convert.ToHexString(Encoding.ASCII.GetBytes(Nickname));
                profile += $"90{nicknameHex.Length / 2:X2}{nicknameHex}";
            }
            var profileLength = profile.Length / 2;
            return $"BF2D{profileLength + 4:X2}A0{profileLength + 2:X2}E3{profileLength:X2}{profile}9000";
        }

        private static string BuildNotificationList() =>
            Tlv("BF28", Tlv("A0", BuildNotificationMetadata()));

        private static string BuildPendingNotification()
        {
            var entry = Tlv("BF37", Tlv("BF27", BuildNotificationMetadata() + "A200") + "DF370101");
            return Tlv("BF2B", Tlv("A0", entry));
        }

        private static string BuildNotificationMetadata()
        {
            var address = Convert.ToHexString("example.com"u8.ToArray());
            return Tlv("BF2F", Tlv("8001", "07") + Tlv("8102", "0410") + Tlv("0C0B", address) +
                                 Tlv("5A0A", "98960021436587091234"));
        }

        private static string Tlv(string tagAndLength, string value)
        {
            var tagLength = tagAndLength.StartsWith("BF", StringComparison.OrdinalIgnoreCase) ||
                            tagAndLength.StartsWith("DF", StringComparison.OrdinalIgnoreCase)
                ? 4
                : 2;
            var tag = tagAndLength[..tagLength];
            var length = value.Length / 2;
            var encodedLength = length < 0x80 ? length.ToString("X2") : $"81{length:X2}";
            return $"{tag}{encodedLength}{value}";
        }
    }
}
