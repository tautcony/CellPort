using System.Text.Json;
using CellPort.Core.Transport;
using Xunit;

namespace CellPort.Core.Tests;

public sealed class AdbDeviceCatalogTests
{
    [Fact]
    public void QuestionMarkSerialsRequirePhysicalAssociationAndUseExplicitTransport()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "adb-invalid-serials.json");
        using var fixture = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var output = fixture.RootElement.GetProperty("devicesOutput").GetString()!;
        var catalog = new AdbDeviceCatalog();
        catalog.UpdateFromDevicesOutput(output, (_, transportId) => transportId switch
        {
            41 => "container-a",
            42 => "container-b",
            _ => null,
        });

        var noIdentity = catalog.SelectByPhysicalIdentity(null);
        var selected = catalog.SelectByPhysicalIdentity("container-b");

        Assert.Equal(AdbSelectionStatus.IdentityUnassociated, noIdentity.Status);
        Assert.Equal(AdbSelectionStatus.Selected, selected.Status);
        Assert.Equal(42, selected.Selection!.TransportId);
        Assert.False(selected.Selection.SerialReliable);
        Assert.Null(selected.Selection.Serial);
        Assert.True(catalog.IsCurrent(selected.Selection));
        Assert.Equal(new[] { "-t", "42", "shell", "id" },
            AdbTransportClient.BuildArguments(selected.Selection, ["shell", "id"]));
    }

    [Fact]
    public void DuplicateSerialsAreUnreliableAndNeverResolveBySerialOrder()
    {
        var catalog = new AdbDeviceCatalog();
        catalog.Update([
            new AdbDeviceCandidate("DUPLICATE", "device", 1, "module-a", true),
            new AdbDeviceCandidate("duplicate", "device", 2, "module-b", true),
        ]);

        var first = catalog.SelectByPhysicalIdentity("module-a");
        var second = catalog.SelectByPhysicalIdentity("module-b");

        Assert.Equal(AdbSelectionStatus.Selected, first.Status);
        Assert.Equal(AdbSelectionStatus.Selected, second.Status);
        Assert.False(first.Selection!.SerialReliable);
        Assert.Null(first.Selection.Serial);
        Assert.Equal(2, second.Selection!.TransportId);
    }

    [Fact]
    public void MultipleCandidatesForOnePhysicalIdentityAreRejected()
    {
        var catalog = new AdbDeviceCatalog();
        catalog.Update([
            new AdbDeviceCandidate("?", "device", 1, "same-module", false),
            new AdbDeviceCandidate("?", "device", 2, "same-module", false),
        ]);

        var result = catalog.SelectByPhysicalIdentity("same-module");

        Assert.Equal(AdbSelectionStatus.Ambiguous, result.Status);
        Assert.Null(result.Selection);
    }

    [Fact]
    public void ReenumerationInvalidatesOldTransportAndIssuesNewGeneration()
    {
        var catalog = new AdbDeviceCatalog();
        catalog.Update([new AdbDeviceCandidate("?", "device", 41, "module", false)]);
        var old = catalog.SelectByPhysicalIdentity("module").Selection!;

        catalog.Update([new AdbDeviceCandidate("?", "device", 88, "module", false)]);
        var current = catalog.SelectByPhysicalIdentity("module").Selection!;

        Assert.False(catalog.IsCurrent(old));
        Assert.True(catalog.IsCurrent(current));
        Assert.Equal(88, current.TransportId);
        Assert.NotEqual(old.CatalogGeneration, current.CatalogGeneration);
    }

    [Fact]
    public async Task ControlChannelTimeoutIsBoundedAndUsesOnlyTheSelectedTransport()
    {
        var catalog = new AdbDeviceCatalog();
        catalog.Update([new AdbDeviceCandidate("?", "device", 41, "module", false)]);
        var selection = catalog.SelectByPhysicalIdentity("module").Selection!;
        var runner = new BlockingRunner();
        var client = new AdbTransportClient(catalog, "adb-test", runner);

        var result = await client.RunAsync(selection, ["exec-out", "inspect"], TimeSpan.FromMilliseconds(60));

        Assert.Equal(AdbCommandStatus.TimedOut, result.Status);
        Assert.Equal(new[] { "-t", "41", "exec-out", "inspect" }, runner.Arguments);
        Assert.True(runner.CancellationObserved);
    }

    [Fact]
    public async Task StaleTransportIsRejectedBeforeSpawningACommand()
    {
        var catalog = new AdbDeviceCatalog();
        catalog.Update([new AdbDeviceCandidate("?", "device", 41, "module", false)]);
        var selection = catalog.SelectByPhysicalIdentity("module").Selection!;
        catalog.Update([new AdbDeviceCandidate("?", "device", 88, "module", false)]);
        var runner = new BlockingRunner();
        var client = new AdbTransportClient(catalog, processRunner: runner);

        var result = await client.RunAsync(selection, ["exec-out", "inspect"], TimeSpan.FromSeconds(1));

        Assert.Equal(AdbCommandStatus.StaleTransport, result.Status);
        Assert.Null(runner.Arguments);
    }

    private sealed class BlockingRunner : IAdbProcessRunner
    {
        public IReadOnlyList<string>? Arguments { get; private set; }
        public bool CancellationObserved { get; private set; }

        public async Task<AdbCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Arguments = arguments.ToArray();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }

            return new AdbCommandResult(AdbCommandStatus.Succeeded, 0, string.Empty, string.Empty);
        }
    }
}
