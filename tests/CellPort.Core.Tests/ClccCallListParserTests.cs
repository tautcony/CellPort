using System.Text.Json;
using CellPort.Core.At;
using CellPort.Core.Services;
using Xunit;

namespace CellPort.Core.Tests;

public sealed class ClccCallListParserTests
{
    [Fact]
    public void RedactedFixturePreservesTwoActiveDataEntriesAsNonVoiceOccupancy()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "redacted-readonly-baseline.json");
        using var fixture = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var lines = fixture.RootElement.GetProperty("clcc").GetProperty("response")
            .EnumerateArray().Select(line => line.GetString()!).ToArray();

        var result = ClccCallListParser.ParseSuccessfulLines(lines);

        Assert.Equal(ClccQueryOutcome.Complete, result.Outcome);
        Assert.Equal(2, result.Entries.Length);
        Assert.Equal(0, result.ActiveVoiceCallCount);
        Assert.Equal(2, result.NonVoiceOccupancyCount);
        Assert.False(result.IsConfirmedEmpty);
        Assert.True(result.HasAnyCallOrOccupancy);
        Assert.All(result.Entries, entry => Assert.Equal(0, entry.Status));
        Assert.All(result.Entries, entry => Assert.Equal(1, entry.Mode));
    }

    [Fact]
    public void SuccessfulResponseWithoutEntriesConfirmsEmptyCallList()
    {
        var result = ClccCallListParser.ParseSuccessfulLines(["OK"]);

        Assert.Equal(ClccQueryOutcome.Complete, result.Outcome);
        Assert.True(result.IsConfirmedEmpty);
        Assert.False(result.HasAnyCallOrOccupancy);
    }

    [Fact]
    public void CommandErrorAndTimeoutAreUnknownRatherThanEmpty()
    {
        var error = ClccCallListParser.Parse(new AtResult(
            "AT+CLCC", false, [], "", TimeSpan.FromMilliseconds(1), AtCompletionStatus.Error));
        var timeout = ClccCallListParser.Parse(new AtResult(
            "AT+CLCC", false, [], "", TimeSpan.FromSeconds(5), AtCompletionStatus.Timeout));

        Assert.Equal(ClccQueryOutcome.CommandError, error.Outcome);
        Assert.Equal(ClccQueryOutcome.TimedOut, timeout.Outcome);
        Assert.False(error.IsConfirmedEmpty);
        Assert.False(timeout.IsConfirmedEmpty);
        Assert.True(error.HasAnyCallOrOccupancy);
        Assert.True(timeout.HasAnyCallOrOccupancy);
    }

    [Theory]
    [InlineData("+CLCC: 1,1,0,1,0,\"<redacted>\"", "missing number type")]
    [InlineData("+CLCC: 1,1,x,0,0", "bad status")]
    [InlineData("+CLCC: 1,1,0,0,0,\"unterminated,128", "bad quoting")]
    public void MalformedEntryInvalidatesTheWholeSnapshot(string malformedEntry, string _)
    {
        var result = ClccCallListParser.ParseSuccessfulLines([
            "+CLCC: 1,0,0,0,0,\"123\",129",
            malformedEntry,
        ]);

        Assert.Equal(ClccQueryOutcome.MalformedResponse, result.Outcome);
        Assert.Empty(result.Entries);
        Assert.False(result.IsConfirmedEmpty);
        Assert.True(result.HasAnyCallOrOccupancy);
    }

    [Fact]
    public void ActiveVoiceAndDataEntriesRemainSeparatelyClassified()
    {
        var result = ClccCallListParser.ParseSuccessfulLines([
            "+CLCC: 1,0,0,0,0,\"123\",129",
            "+CLCC: 2,1,0,1,0,\"<redacted>\",128",
        ]);

        Assert.Equal(1, result.ActiveVoiceCallCount);
        Assert.Equal(1, result.NonVoiceOccupancyCount);
        Assert.True(result.Entries[0].IsActiveVoiceCall);
        Assert.False(result.Entries[1].IsActiveVoiceCall);
    }
}
