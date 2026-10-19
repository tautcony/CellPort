using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CellPort.Core.Transport;

public enum AdbCommandStatus
{
    Succeeded,
    Failed,
    TimedOut,
    StaleTransport,
    InvalidSelection,
}

public sealed record AdbCommandResult(
    AdbCommandStatus Status,
    int? ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool Succeeded => Status == AdbCommandStatus.Succeeded;
}

public interface IAdbProcessRunner
{
    Task<AdbCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

/// <summary>Runs ADB commands only against a currently verified transport using the explicit -t selector.</summary>
public sealed class AdbTransportClient
{
    private readonly AdbDeviceCatalog _catalog;
    private readonly string _adbExecutable;
    private readonly IAdbProcessRunner _processRunner;

    public AdbTransportClient(
        AdbDeviceCatalog catalog,
        string adbExecutable = "adb",
        IAdbProcessRunner? processRunner = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _adbExecutable = string.IsNullOrWhiteSpace(adbExecutable) ? "adb" : adbExecutable;
        _processRunner = processRunner ?? new SystemAdbProcessRunner();
    }

    public async Task<AdbCommandResult> RunAsync(
        AdbTransportSelection selection,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(arguments);
        if (selection.TransportId <= 0 || string.IsNullOrWhiteSpace(selection.PhysicalIdentityKey) ||
            arguments.Count == 0 || arguments.Any(argument => argument is null) ||
            (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan))
        {
            return Failure(AdbCommandStatus.InvalidSelection, "Invalid transport selection or command.");
        }
        if (!_catalog.IsCurrent(selection))
        {
            return Failure(AdbCommandStatus.StaleTransport, "ADB transport changed; revalidation is required.");
        }

        var commandArguments = BuildArguments(selection, arguments);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            timeoutCts.CancelAfter(timeout);
        }

        try
        {
            return await _processRunner.RunAsync(_adbExecutable, commandArguments, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(AdbCommandStatus.TimedOut, "ADB control request timed out.");
        }
    }

    public static IReadOnlyList<string> BuildArguments(
        AdbTransportSelection selection,
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(arguments);
        if (selection.TransportId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(selection), "Transport ID must be positive.");
        }

        return ["-t", selection.TransportId.ToString(CultureInfo.InvariantCulture), .. arguments];
    }

    private static AdbCommandResult Failure(AdbCommandStatus status, string diagnostic) =>
        new(status, null, string.Empty, diagnostic);
}

internal sealed class SystemAdbProcessRunner : IAdbProcessRunner
{
    public async Task<AdbCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new AdbCommandResult(AdbCommandStatus.Failed, null, string.Empty,
                    "ADB process did not start.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AdbCommandResult(AdbCommandStatus.Failed, null, string.Empty,
                $"ADB could not start: {ex.GetType().Name}.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new AdbCommandResult(process.ExitCode == 0 ? AdbCommandStatus.Succeeded : AdbCommandStatus.Failed,
                process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch
            {
                // Preserve cancellation; the caller distinguishes timeout from explicit cancellation.
            }
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Preserve cancellation if the process has already exited.
        }
    }
}
