using CellPort.Audio.Core.Models;
using CellPort.Audio.Core.Services;
using CellPort.Core.Services;
using Xunit;

namespace CellPort.Audio.Core.Tests;

public sealed class DeviceOperationCoordinatorTests
{
    [Fact]
    public async Task AudioPreparationRequiresFreshCompleteEmptySnapshotForSameModuleGeneration()
    {
        await using var coordinator = new DeviceOperationCoordinator();
        var tracker = new CallTracker();
        var generation = tracker.BeginModuleSession();

        var unknown = await coordinator.TryAcquireAsync(new DeviceOperationRequest(
            DeviceOperationKind.AudioPrepare, tracker.Apply(new ClccCallListResult(ClccQueryOutcome.TimedOut, [])), generation));
        Assert.False(unknown.Succeeded);
        Assert.Equal(AudioErrorCode.CallStateUnknown, unknown.Error!.Code);

        var data = tracker.Apply(new ClccCallListResult(ClccQueryOutcome.Complete,
            [new ClccCallEntry(1, CallDirection.Outgoing, 0, 1, false, null, 129, null, null)]));
        var busy = await coordinator.TryAcquireAsync(new DeviceOperationRequest(DeviceOperationKind.AudioPrepare, data, generation));
        Assert.False(busy.Succeeded);
        Assert.Equal(AudioErrorCode.DeviceBusyData, busy.Error!.Code);

        var empty = tracker.Apply(new ClccCallListResult(ClccQueryOutcome.Complete, []));
        var stale = empty with { SampledAtTimestamp = empty.SampledAtTimestamp - TimeProvider.System.TimestampFrequency * 4 };
        var staleResult = await coordinator.TryAcquireAsync(new DeviceOperationRequest(
            DeviceOperationKind.AudioPrepare, stale, generation));
        Assert.False(staleResult.Succeeded);
        Assert.Equal(AudioErrorCode.CallStateUnknown, staleResult.Error!.Code);

        var wrongGeneration = await coordinator.TryAcquireAsync(new DeviceOperationRequest(
            DeviceOperationKind.AudioPrepare, empty, generation + 1));
        Assert.False(wrongGeneration.Succeeded);
        Assert.Equal(AudioErrorCode.CallStateUnknown, wrongGeneration.Error!.Code);

        var prepare = await coordinator.TryAcquireAsync(new DeviceOperationRequest(
            DeviceOperationKind.AudioPrepare, empty, generation));
        Assert.True(prepare.Succeeded);
        var prepareLease = prepare.Lease!;
        await using (prepareLease)
            prepareLease.CommitAudioSessionOwnership();
        Assert.True(coordinator.AudioSessionOwned);
    }

    [Fact]
    public async Task OwnedAudioSessionSerializesConflictsButAllowsReadOnlyAndCallControl()
    {
        await using var coordinator = new DeviceOperationCoordinator();
        var tracker = new CallTracker();
        var generation = tracker.BeginModuleSession();
        var empty = tracker.Apply(new ClccCallListResult(ClccQueryOutcome.Complete, []));
        var prepare = await coordinator.TryAcquireAsync(new DeviceOperationRequest(
            DeviceOperationKind.AudioPrepare, empty, generation));
        Assert.True(prepare.Succeeded);
        var prepareLease = prepare.Lease!;
        await using (prepareLease)
            prepareLease.CommitAudioSessionOwnership();

        foreach (var conflict in new[]
                 {
                     DeviceOperationKind.SmsWrite,
                     DeviceOperationKind.EsimModification,
                     DeviceOperationKind.CfunChange,
                     DeviceOperationKind.UsbNetworkChange,
                     DeviceOperationKind.ModuleRestart,
                     DeviceOperationKind.RawConsoleCommand,
                     DeviceOperationKind.AudioPrepare,
                 })
        {
            var result = await coordinator.TryAcquireAsync(new DeviceOperationRequest(conflict, empty, generation));
            Assert.False(result.Succeeded);
            Assert.Equal(AudioErrorCode.OperationConflict, result.Error!.Code);
        }

        var query = await coordinator.TryAcquireAsync(new DeviceOperationRequest(DeviceOperationKind.ReadOnlyQuery));
        Assert.True(query.Succeeded);
        await using (query.Lease!) { }
        var callControl = await coordinator.TryAcquireAsync(new DeviceOperationRequest(DeviceOperationKind.CallControl));
        Assert.True(callControl.Succeeded);
        await using (callControl.Lease!) { }
        var readOnlyConsole = await coordinator.TryAcquireAsync(new DeviceOperationRequest(
            DeviceOperationKind.RawConsoleCommand, RawCommandIsReadOnly: true));
        Assert.True(readOnlyConsole.Succeeded);
        await using (readOnlyConsole.Lease!) { }

        var release = await coordinator.TryAcquireAsync(new DeviceOperationRequest(DeviceOperationKind.AudioRelease));
        Assert.True(release.Succeeded);
        var releaseLease = release.Lease!;
        await using (releaseLease)
            releaseLease.CommitVerifiedRestore();
        Assert.False(coordinator.AudioSessionOwned);

        var nowAllowed = await coordinator.TryAcquireAsync(new DeviceOperationRequest(DeviceOperationKind.UsbNetworkChange));
        Assert.True(nowAllowed.Succeeded);
        await using (nowAllowed.Lease!) { }
    }

    [Fact]
    public async Task ConflictingOperationsDoNotBypassAnInFlightMutation()
    {
        await using var coordinator = new DeviceOperationCoordinator();
        var esim = await coordinator.TryAcquireAsync(new DeviceOperationRequest(DeviceOperationKind.EsimModification));
        Assert.True(esim.Succeeded);

        var tracker = new CallTracker();
        var generation = tracker.BeginModuleSession();
        var empty = tracker.Apply(new ClccCallListResult(ClccQueryOutcome.Complete, []));
        var prepare = await coordinator.TryAcquireAsync(new DeviceOperationRequest(
            DeviceOperationKind.AudioPrepare, empty, generation));
        Assert.False(prepare.Succeeded);
        Assert.Equal(AudioErrorCode.OperationConflict, prepare.Error!.Code);

        await esim.Lease!.DisposeAsync();
        var retry = await coordinator.TryAcquireAsync(new DeviceOperationRequest(
            DeviceOperationKind.AudioPrepare, empty, generation));
        Assert.True(retry.Succeeded);
        await retry.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task CallDuringPreparationCancelsBeforeUsbCommitAndRequiresLocalConvergenceAfterCommit()
    {
        await using (var coordinator = new DeviceOperationCoordinator())
        {
            var tracker = new CallTracker();
            var generation = tracker.BeginModuleSession();
            var empty = tracker.Apply(new ClccCallListResult(ClccQueryOutcome.Complete, []));
            var prepare = await coordinator.TryAcquireAsync(new DeviceOperationRequest(
                DeviceOperationKind.AudioPrepare, empty, generation));
            Assert.True(prepare.Succeeded);
            var lease = prepare.Lease!;
            Assert.True(lease.TryBeginUsbCommit());
            var incoming = tracker.Apply(new ClccCallListResult(ClccQueryOutcome.Complete,
                [new ClccCallEntry(1, CallDirection.Incoming, 4, 0, false, "<redacted>", 129, null, null)]));

            Assert.Equal(AudioPrepareCallImpact.MustConvergeLocally, coordinator.NotifyCallSnapshot(incoming));
            Assert.False(lease.OperationCancellationToken.IsCancellationRequested);
            lease.CommitAudioSessionOwnership();
            await lease.DisposeAsync();
            Assert.True(coordinator.AudioSessionOwned);
        }

        await using (var coordinator = new DeviceOperationCoordinator())
        {
            var tracker = new CallTracker();
            var generation = tracker.BeginModuleSession();
            var empty = tracker.Apply(new ClccCallListResult(ClccQueryOutcome.Complete, []));
            var prepare = await coordinator.TryAcquireAsync(new DeviceOperationRequest(
                DeviceOperationKind.AudioPrepare, empty, generation));
            Assert.True(prepare.Succeeded);
            var lease = prepare.Lease!;
            var incoming = tracker.Apply(new ClccCallListResult(ClccQueryOutcome.Complete,
                [new ClccCallEntry(1, CallDirection.Incoming, 4, 0, false, "<redacted>", 129, null, null)]));

            Assert.Equal(AudioPrepareCallImpact.CancelBeforeUsbCommit, coordinator.NotifyCallSnapshot(incoming));
            Assert.True(lease.OperationCancellationToken.IsCancellationRequested);
            Assert.False(lease.TryBeginUsbCommit());
            await lease.DisposeAsync();
            Assert.False(coordinator.AudioSessionOwned);
        }
    }
}
