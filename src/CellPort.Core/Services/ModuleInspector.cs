using System.Collections.Immutable;
using System.Text.RegularExpressions;
using CellPort.Core.At;
using CellPort.Core.Transport;

namespace CellPort.Core.Services;

public enum ModuleInspectStatus
{
    Inspected,
    DeviceBusyData,
    ActiveVoiceCall,
    CallStateUnknown,
    TransportFailure,
}

/// <summary>Redacted read-only inspection report; it never includes phone numbers or raw AT output.</summary>
public sealed record ModuleInspectReport(
    ModuleInspectStatus Status,
    string Code,
    string? Model,
    string? Firmware,
    ClccQueryOutcome ClccOutcome,
    int NonVoiceOccupancyCount,
    int ActiveVoiceCallCount,
    ImmutableArray<string> ReadCommands);

/// <summary>Reads a small allowlisted set of module properties without connection initialization writes.</summary>
public static partial class ModuleInspector
{
    private static readonly ImmutableArray<string> AllowedCommands =
        ["AT", "AT+CGMM", "AT+CGMR", "AT+CLCC"];

    public static async Task<ModuleInspectReport> InspectAsync(
        IAtChannel channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        try
        {
            await channel.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            return new ModuleInspectReport(ModuleInspectStatus.TransportFailure, "CONTROL_CHANNEL_UNAVAILABLE",
                null, null, ClccQueryOutcome.TransportFailure, 0, 0, []);
        }

        await using var engine = new AtEngine(channel);
        var commands = ImmutableArray.CreateBuilder<string>(AllowedCommands.Length);
        var probe = await ExecuteReadAsync(engine, AllowedCommands[0], commands, cancellationToken)
            .ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            return new ModuleInspectReport(ModuleInspectStatus.TransportFailure, "CONTROL_CHANNEL_UNAVAILABLE",
                null, null, ClccQueryOutcome.TransportFailure, 0, 0, commands.ToImmutable());
        }

        var modelResult = await ExecuteReadAsync(engine, AllowedCommands[1], commands, cancellationToken)
            .ConfigureAwait(false);
        var firmwareResult = await ExecuteReadAsync(engine, AllowedCommands[2], commands, cancellationToken)
            .ConfigureAwait(false);
        var clccAtResult = await ExecuteReadAsync(engine, AllowedCommands[3], commands, cancellationToken,
            AtCommandPriority.CallState).ConfigureAwait(false);
        var clcc = ClccCallListParser.Parse(clccAtResult);

        var status = clcc.Outcome switch
        {
            not ClccQueryOutcome.Complete => ModuleInspectStatus.CallStateUnknown,
            _ when clcc.NonVoiceOccupancyCount > 0 => ModuleInspectStatus.DeviceBusyData,
            _ when clcc.ActiveVoiceCallCount > 0 => ModuleInspectStatus.ActiveVoiceCall,
            _ => ModuleInspectStatus.Inspected,
        };
        var code = status switch
        {
            ModuleInspectStatus.DeviceBusyData => "DEVICE_BUSY_DATA",
            ModuleInspectStatus.ActiveVoiceCall => "VOICE_CALL_ACTIVE",
            ModuleInspectStatus.CallStateUnknown => "CALL_STATE_UNKNOWN",
            _ => "INSPECTED",
        };

        return new ModuleInspectReport(
            status,
            code,
            modelResult.Succeeded ? FirstValue(modelResult) : null,
            firmwareResult.Succeeded ? FirstValue(firmwareResult) : null,
            clcc.Outcome,
            clcc.NonVoiceOccupancyCount,
            clcc.ActiveVoiceCallCount,
            commands.ToImmutable());
    }

    private static async Task<AtResult> ExecuteReadAsync(
        AtEngine engine,
        string command,
        ImmutableArray<string>.Builder executedCommands,
        CancellationToken cancellationToken,
        AtCommandPriority priority = AtCommandPriority.Background)
    {
        if (!AllowedCommands.Contains(command, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Inspection command is not allowlisted.");
        }
        executedCommands.Add(command);
        return await engine.ExecuteAsync(command, priority, TimeSpan.FromSeconds(5), cancellationToken)
            .ConfigureAwait(false);
    }

    private static string? FirstValue(AtResult result)
    {
        var line = result.Lines.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (line is null) return null;
        var safe = RedactedValuePattern().Replace(line.Trim(), "[redacted]");
        return safe.Length <= 128 ? safe : safe[..128];
    }

    [GeneratedRegex("\\+?\\d{5,}", RegexOptions.CultureInvariant)]
    private static partial Regex RedactedValuePattern();
}
