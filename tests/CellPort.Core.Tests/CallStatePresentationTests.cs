using System.Text.Json;
using CellPort.Core.Services;
using Xunit;

namespace CellPort.Core.Tests;

public sealed class CallStatePresentationTests
{
    [Fact]
    public void ActiveDataFixtureNeverPresentsVoiceAsConnected()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "redacted-readonly-baseline.json");
        using var fixture = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var lines = fixture.RootElement.GetProperty("clcc").GetProperty("response")
            .EnumerateArray().Select(line => line.GetString()!).ToArray();
        var tracker = new CallTracker();
        tracker.BeginModuleSession();
        var snapshot = tracker.Apply(ClccCallListParser.ParseSuccessfulLines(lines));

        var text = CallStatePresentation.GetStatusText(CallState.Active, snapshot);

        Assert.Equal("仅数据占用；语音未接通", text);
        Assert.DoesNotContain("通话中", text);
        Assert.DoesNotContain("语音已接通", text);
    }

    [Fact]
    public void FailedSnapshotUsesUnknownWordingInsteadOfIdleOrActive()
    {
        var tracker = new CallTracker();
        tracker.BeginModuleSession();
        var snapshot = tracker.Apply(new ClccCallListResult(ClccQueryOutcome.CommandError, []));

        Assert.Equal("通话状态未知", CallStatePresentation.GetStatusText(CallState.Idle, snapshot));
    }

    [Fact]
    public void OnlyUniqueActiveVoiceSnapshotUsesConnectedWording()
    {
        var tracker = new CallTracker();
        tracker.BeginModuleSession();
        var snapshot = tracker.Apply(ClccCallListParser.ParseSuccessfulLines([
            "+CLCC: 1,0,0,0,0,\"123\",129",
        ]));

        Assert.Equal("语音通话中", CallStatePresentation.GetStatusText(CallState.Active, snapshot));
        Assert.Equal("挂断未确认", CallStatePresentation.GetStatusText(CallState.HangupUnconfirmed, snapshot));
    }
}
