using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using CellPort.Core.At;

namespace CellPort.Core.Services;

/// <summary>Outcome of a complete CLCC query. Only Complete can establish an empty list.</summary>
public enum ClccQueryOutcome
{
    Complete,
    CommandError,
    TimedOut,
    TransportFailure,
    MalformedResponse,
}

/// <summary>A parsed +CLCC entry. Status and mode retain modem values for forward compatibility.</summary>
public sealed record ClccCallEntry(
    int Index,
    CallDirection Direction,
    int Status,
    int Mode,
    bool IsMultiparty,
    string? Number,
    int? NumberType,
    string? Alpha,
    int? Priority)
{
    public bool IsActiveVoiceCall => Status == 0 && Mode == 0;
    public bool IsNonVoiceOccupancy => Mode != 0;
}

/// <summary>Immutable result for one full CLCC command.</summary>
public sealed record ClccCallListResult(
    ClccQueryOutcome Outcome,
    ImmutableArray<ClccCallEntry> Entries,
    string? Diagnostic = null)
{
    public bool IsComplete => Outcome == ClccQueryOutcome.Complete;
    public bool IsConfirmedEmpty => IsComplete && Entries.IsEmpty;
    public int ActiveVoiceCallCount => IsComplete ? Entries.Count(entry => entry.IsActiveVoiceCall) : 0;
    public int NonVoiceOccupancyCount => IsComplete ? Entries.Count(entry => entry.IsNonVoiceOccupancy) : 0;
    public bool HasAnyCallOrOccupancy => !IsComplete || !Entries.IsEmpty;
}

/// <summary>
/// Parses the entire response to AT+CLCC. Partial or malformed responses never become an empty list.
/// </summary>
public static class ClccCallListParser
{
    public static ClccCallListResult Parse(AtResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.Succeeded)
        {
            return result.Completion switch
            {
                AtCompletionStatus.Error => Failure(ClccQueryOutcome.CommandError, "AT+CLCC returned ERROR."),
                AtCompletionStatus.Timeout => Failure(ClccQueryOutcome.TimedOut, "AT+CLCC timed out."),
                AtCompletionStatus.TransportFailure => Failure(ClccQueryOutcome.TransportFailure,
                    "AT+CLCC transport failed."),
                _ => Failure(ClccQueryOutcome.TransportFailure, "AT+CLCC did not complete successfully."),
            };
        }

        return ParseSuccessfulLines(result.Lines);
    }

    public static ClccCallListResult ParseSuccessfulLines(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var entries = ImmutableArray.CreateBuilder<ClccCallEntry>();

        foreach (var rawLine in lines)
        {
            if (rawLine is null)
            {
                return Failure(ClccQueryOutcome.MalformedResponse, "Response contains a null line.");
            }

            var line = rawLine.Trim();
            if (!line.StartsWith("+CLCC", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!line.StartsWith("+CLCC:", StringComparison.OrdinalIgnoreCase) ||
                !TryParseEntry(line[6..], out var entry))
            {
                return Failure(ClccQueryOutcome.MalformedResponse, "A +CLCC entry is malformed.");
            }

            entries.Add(entry!);
        }

        return new ClccCallListResult(ClccQueryOutcome.Complete, entries.ToImmutable());
    }

    private static bool TryParseEntry(string text, out ClccCallEntry? entry)
    {
        entry = null;
        if (!TrySplitFields(text, out var fields) || fields.Count is not (5 or 7 or 8 or 9))
        {
            return false;
        }

        if (!TryInteger(fields[0], out var index) || index <= 0 ||
            !TryInteger(fields[1], out var direction) || direction is < 0 or > 1 ||
            !TryInteger(fields[2], out var status) || status is < 0 or > 5 ||
            !TryInteger(fields[3], out var mode) || mode < 0 ||
            !TryInteger(fields[4], out var multiparty) || multiparty is < 0 or > 1)
        {
            return false;
        }

        string? number = null;
        int? numberType = null;
        string? alpha = null;
        int? priority = null;

        if (fields.Count >= 7)
        {
            number = fields[5].Length == 0 ? null : fields[5];
            if (!TryInteger(fields[6], out var parsedNumberType) || parsedNumberType is < 0 or > 255)
            {
                return false;
            }
            numberType = parsedNumberType;
        }
        if (fields.Count >= 8)
        {
            alpha = fields[7].Length == 0 ? null : fields[7];
        }
        if (fields.Count == 9)
        {
            if (!TryInteger(fields[8], out var parsedPriority) || parsedPriority < 0)
            {
                return false;
            }
            priority = parsedPriority;
        }

        entry = new ClccCallEntry(index,
            direction == 0 ? CallDirection.Outgoing : CallDirection.Incoming,
            status, mode, multiparty == 1, number, numberType, alpha, priority);
        return true;
    }

    private static bool TrySplitFields(string text, out List<string> fields)
    {
        fields = [];
        var current = new StringBuilder();
        var quoted = false;
        var fieldWasQuoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
                continue;
            }

            if (c == ',')
            {
                fields.Add(fieldWasQuoted ? current.ToString() : current.ToString().Trim());
                current.Clear();
                fieldWasQuoted = false;
                continue;
            }

            if (c == '"')
            {
                if (current.ToString().Trim().Length != 0 || fieldWasQuoted)
                {
                    return false;
                }
                current.Clear();
                fieldWasQuoted = true;
                quoted = true;
                continue;
            }

            if (fieldWasQuoted && !char.IsWhiteSpace(c))
            {
                return false;
            }
            current.Append(c);
        }

        if (quoted)
        {
            return false;
        }

        fields.Add(fieldWasQuoted ? current.ToString() : current.ToString().Trim());
        return true;
    }

    private static bool TryInteger(string value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);

    private static ClccCallListResult Failure(ClccQueryOutcome outcome, string diagnostic) =>
        new(outcome, ImmutableArray<ClccCallEntry>.Empty, diagnostic);
}
