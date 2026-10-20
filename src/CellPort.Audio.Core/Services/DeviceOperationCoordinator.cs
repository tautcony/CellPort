using CellPort.Audio.Core.Models;
using CellPort.Core.Services;

namespace CellPort.Audio.Core.Services;

public enum DeviceOperationKind
{
    ReadOnlyQuery,
    CallStateQuery,
    CallControl,
    SmsRead,
    SmsWrite,
    EsimModification,
    CfunChange,
    UsbNetworkChange,
    ModuleRestart,
    RawConsoleCommand,
    AudioPrepare,
    AudioRouteStart,
    AudioRouteStop,
    AudioRelease,
}

public enum AudioPrepareCallImpact
{
    None,
    CancelBeforeUsbCommit,
    MustConvergeLocally,
}

public sealed record DeviceOperationRequest(
    DeviceOperationKind Kind,
    CallSnapshot? Calls = null,
    long? ExpectedModuleGeneration = null,
    bool RawCommandIsReadOnly = false);

public sealed record DeviceOperationAcquireResult(
    DeviceOperationLease? Lease,
    AudioOperationError? Error)
{
    public bool Succeeded => Lease is not null;
}

/// <summary>Serializes module mutations and rejects destructive work that bypasses call occupancy.</summary>
public sealed class DeviceOperationCoordinator : IAsyncDisposable, IModuleOperationAdmission
{
    public static readonly TimeSpan MaximumCallSnapshotAge = TimeSpan.FromSeconds(3);
    private static readonly HashSet<DeviceOperationKind> MutatingOperations =
    [
        DeviceOperationKind.SmsWrite,
        DeviceOperationKind.SmsRead,
        DeviceOperationKind.CallControl,
        DeviceOperationKind.EsimModification,
        DeviceOperationKind.CfunChange,
        DeviceOperationKind.UsbNetworkChange,
        DeviceOperationKind.ModuleRestart,
        DeviceOperationKind.RawConsoleCommand,
        DeviceOperationKind.AudioPrepare,
        DeviceOperationKind.AudioRouteStart,
        DeviceOperationKind.AudioRouteStop,
        DeviceOperationKind.AudioRelease,
    ];

    private readonly SemaphoreSlim _moduleOperation = new(1, 1);
    private readonly Lock _stateGate = new();
    private readonly TimeProvider _timeProvider;
    private bool _audioSessionOwned;
    private bool _audioPrepareInFlight;
    private bool _usbCommitStarted;
    private CancellationTokenSource? _prepareCancellation;
    private CancellationTokenSource? _committedPrepareCancellation;
    private bool _semaphoreDisposed;
    private bool _disposed;

    public DeviceOperationCoordinator(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    public async ValueTask<ModuleOperationAdmissionResult> TryAcquireAsync(
        ModuleOperationKind kind,
        bool rawCommandIsReadOnly = false,
        CancellationToken cancellationToken = default)
    {
        var deviceKind = kind switch
        {
            ModuleOperationKind.ReadOnlyQuery => DeviceOperationKind.ReadOnlyQuery,
            ModuleOperationKind.CallControl => DeviceOperationKind.CallControl,
            ModuleOperationKind.SmsRead => DeviceOperationKind.SmsRead,
            ModuleOperationKind.SmsWrite => DeviceOperationKind.SmsWrite,
            ModuleOperationKind.EsimModification => DeviceOperationKind.EsimModification,
            ModuleOperationKind.CfunChange => DeviceOperationKind.CfunChange,
            ModuleOperationKind.UsbNetworkChange => DeviceOperationKind.UsbNetworkChange,
            ModuleOperationKind.ModuleRestart => DeviceOperationKind.ModuleRestart,
            ModuleOperationKind.RawConsoleCommand => DeviceOperationKind.RawConsoleCommand,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var acquired = await TryAcquireAsync(
            new DeviceOperationRequest(deviceKind, RawCommandIsReadOnly: rawCommandIsReadOnly),
            cancellationToken).ConfigureAwait(false);
        return acquired.Succeeded
            ? ModuleOperationAdmissionResult.Permit(acquired.Lease)
            : ModuleOperationAdmissionResult.Reject(acquired.Error!.Code.ToString());
    }

    public bool AudioSessionOwned
    {
        get { lock (_stateGate) return _audioSessionOwned; }
    }

    public async ValueTask<DeviceOperationAcquireResult> TryAcquireAsync(
        DeviceOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (request is { Kind: DeviceOperationKind.RawConsoleCommand, RawCommandIsReadOnly: true })
            return new DeviceOperationAcquireResult(new DeviceOperationLease(this, request.Kind, ownsSemaphore: false), null);
        if (!MutatingOperations.Contains(request.Kind))
            return new DeviceOperationAcquireResult(new DeviceOperationLease(this, request.Kind, ownsSemaphore: false), null);

        if (!await _moduleOperation.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return Failure(AudioErrorCode.OperationConflict, AudioOperationStage.Ownership, retryable: true);

        lock (_stateGate)
        {
            if (_disposed)
            {
                _moduleOperation.Release();
                return Failure(AudioErrorCode.OperationConflict, AudioOperationStage.Ownership, retryable: false);
            }

            if (ConflictsWithOwnedAudioSession(request))
            {
                _moduleOperation.Release();
                return Failure(AudioErrorCode.OperationConflict, AudioOperationStage.Ownership, retryable: true);
            }

            var gateError = GetAdmissionError(request);
            if (gateError is not null)
            {
                _moduleOperation.Release();
                return new DeviceOperationAcquireResult(null, gateError);
            }
        }

        CancellationTokenSource? prepareCancellation = null;
        if (request.Kind == DeviceOperationKind.AudioPrepare)
        {
            prepareCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_stateGate)
            {
                _prepareCancellation = prepareCancellation;
                _audioPrepareInFlight = true;
                _usbCommitStarted = false;
            }
        }
        return new DeviceOperationAcquireResult(new DeviceOperationLease(this, request.Kind, ownsSemaphore: true,
            prepareCancellation), null);
    }

    /// <summary>Signals a prepare operation when a new call, occupancy, or unknown state arrives.</summary>
    public AudioPrepareCallImpact NotifyCallSnapshot(CallSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        CancellationTokenSource? cancel = null;
        lock (_stateGate)
        {
            if (!_audioPrepareInFlight) return AudioPrepareCallImpact.None;
            var age = _timeProvider.GetElapsedTime(snapshot.SampledAtTimestamp);
            var unsafeSnapshot = snapshot.IsUnknown || !snapshot.IsConfirmedEmpty || age < TimeSpan.Zero ||
                                 age >= MaximumCallSnapshotAge;
            if (!unsafeSnapshot) return AudioPrepareCallImpact.None;
            if (_usbCommitStarted) return AudioPrepareCallImpact.MustConvergeLocally;
            cancel = _prepareCancellation;
        }
        cancel?.Cancel();
        return AudioPrepareCallImpact.CancelBeforeUsbCommit;
    }

    internal bool TryBeginUsbCommit()
    {
        lock (_stateGate)
        {
            if (!_audioPrepareInFlight || _prepareCancellation?.IsCancellationRequested != false) return false;
            _usbCommitStarted = true;
            return true;
        }
    }

    internal void CommitAudioPrepare()
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_audioPrepareInFlight || _prepareCancellation?.IsCancellationRequested != false)
                throw new OperationCanceledException("Audio preparation was cancelled by a call-state change.");
            _audioSessionOwned = true;
            _audioPrepareInFlight = false;
            _usbCommitStarted = false;

            // 不能在这里 Dispose：lease.OperationCancellationToken 仍被外部持有，
            // 之后再 Register 会抛 ObjectDisposedException。令牌源随会话一起释放。
            _committedPrepareCancellation = _prepareCancellation;
            _prepareCancellation = null;
        }
    }

    internal void CancelUncommittedAudioPrepare()
    {
        CancellationTokenSource? cancellation;
        lock (_stateGate)
        {
            if (!_audioPrepareInFlight) return;
            _audioPrepareInFlight = false;
            _usbCommitStarted = false;
            cancellation = _prepareCancellation;
            _prepareCancellation = null;
        }
        cancellation?.Cancel();
    }

    internal void CommitVerifiedAudioRelease()
    {
        CancellationTokenSource? committed;
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _audioSessionOwned = false;
            committed = _committedPrepareCancellation;
            _committedPrepareCancellation = null;
        }
        // 会话已确认归还，prepare 令牌源此时才可安全释放。
        committed?.Dispose();
    }

    internal void ReleaseSemaphore()
    {
        // 协调器已释放后，仍在飞的租约回收时不能再 Release 已 Dispose 的信号量。
        lock (_stateGate)
        {
            if (_semaphoreDisposed)
            {
                return;
            }
        }

        try
        {
            _moduleOperation.Release();
        }
        catch (ObjectDisposedException)
        {
            // 与 DisposeAsync 竞争：忽略即可，信号量本身已不再使用。
        }
    }

    private bool ConflictsWithOwnedAudioSession(DeviceOperationRequest request)
    {
        if (!_audioSessionOwned) return false;
        return request.Kind is DeviceOperationKind.SmsWrite
            or DeviceOperationKind.EsimModification
            or DeviceOperationKind.CfunChange
            or DeviceOperationKind.UsbNetworkChange
            or DeviceOperationKind.ModuleRestart
            or DeviceOperationKind.RawConsoleCommand
            or DeviceOperationKind.AudioPrepare;
    }

    private AudioOperationError? GetAdmissionError(DeviceOperationRequest request)
    {
        if (request.Kind == DeviceOperationKind.AudioPrepare)
            return ValidateSnapshot(request, requireEmpty: true);
        if (request.Kind == DeviceOperationKind.AudioRouteStart)
        {
            if (!_audioSessionOwned)
                return Error(AudioErrorCode.RouteNotReady, AudioOperationStage.Routing, retryable: true);
            var snapshotError = ValidateSnapshot(request, requireEmpty: false);
            if (snapshotError is not null) return snapshotError;
            var selected = request.Calls!.SelectedVoiceCall;
            if (selected is null || selected.State != TrackedCallState.Active)
                return Error(AudioErrorCode.RouteNotReady, AudioOperationStage.CallState, retryable: true);
        }
        if ((request.Kind is DeviceOperationKind.AudioRouteStop or DeviceOperationKind.AudioRelease) && !_audioSessionOwned)
            return Error(AudioErrorCode.OwnerMismatch, AudioOperationStage.Ownership, retryable: false);
        return null;
    }

    private AudioOperationError? ValidateSnapshot(DeviceOperationRequest request, bool requireEmpty)
    {
        var snapshot = request.Calls;
        if (snapshot is null || snapshot.IsUnknown || request.ExpectedModuleGeneration is not long expected ||
            snapshot.ModuleGeneration != expected)
            return Error(AudioErrorCode.CallStateUnknown, AudioOperationStage.CallState, retryable: true);

        var age = _timeProvider.GetElapsedTime(snapshot.SampledAtTimestamp);
        if (age < TimeSpan.Zero || age >= MaximumCallSnapshotAge)
            return Error(AudioErrorCode.CallStateUnknown, AudioOperationStage.CallState, retryable: true);
        if (requireEmpty && !snapshot.IsConfirmedEmpty)
        {
            return Error(snapshot.NonVoiceOccupancyCount > 0 ? AudioErrorCode.DeviceBusyData : AudioErrorCode.OperationConflict,
                AudioOperationStage.CallState, retryable: true);
        }
        return null;
    }

    private static DeviceOperationAcquireResult Failure(AudioErrorCode code, AudioOperationStage stage, bool retryable) =>
        new(null, Error(code, stage, retryable));

    private static AudioOperationError Error(AudioErrorCode code, AudioOperationStage stage, bool retryable) =>
        new(code, stage, retryable, code == AudioErrorCode.DeviceBusyData
            ? AudioRecoveryStatus.RestorePendingCall
            : AudioRecoveryStatus.None);

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? committed;
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true;
            committed = _committedPrepareCancellation;
        }

        CancelUncommittedAudioPrepare();

        // 等待在飞操作结束，但绝不无限等待：租约泄漏时也应能完成释放流程。
        var acquired = await _moduleOperation.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        if (!acquired)
        {
            // 仍有租约未归还：在租约释放后完成清理，不能销毁仍被占用的信号量。
            _ = DisposeAfterActiveOperationAsync(committed);
            return;
        }

        CompleteDisposal(committed);
    }

    private async Task DisposeAfterActiveOperationAsync(CancellationTokenSource? committed)
    {
        await _moduleOperation.WaitAsync().ConfigureAwait(false);
        CompleteDisposal(committed);
    }

    private void CompleteDisposal(CancellationTokenSource? committed)
    {
        lock (_stateGate)
        {
            _semaphoreDisposed = true;
            if (ReferenceEquals(_committedPrepareCancellation, committed))
            {
                _committedPrepareCancellation = null;
            }
        }
        // 活动操作结束后再释放已提交会话的 CTS，避免持有令牌的租约在释放期间注册回调。
        committed?.Dispose();
        _moduleOperation.Release();
        _moduleOperation.Dispose();
    }
}

public sealed class DeviceOperationLease : IModuleOperationLease, IDisposable
{
    private readonly DeviceOperationCoordinator _coordinator;
    private readonly bool _ownsSemaphore;
    private readonly CancellationTokenSource? _prepareCancellation;
    private bool _audioPrepareCommitted;
    private int _disposed;

    internal DeviceOperationLease(DeviceOperationCoordinator coordinator, DeviceOperationKind kind, bool ownsSemaphore,
        CancellationTokenSource? prepareCancellation = null)
    {
        _coordinator = coordinator;
        Kind = kind;
        _ownsSemaphore = ownsSemaphore;
        _prepareCancellation = prepareCancellation;
        OperationCancellationToken = prepareCancellation?.Token ?? CancellationToken.None;
    }

    public DeviceOperationKind Kind { get; }
    public CancellationToken OperationCancellationToken { get; }

    public bool TryBeginUsbCommit()
    {
        if (Kind != DeviceOperationKind.AudioPrepare) throw new InvalidOperationException("Only prepare starts a USB transaction.");
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _coordinator.TryBeginUsbCommit();
    }

    public void CommitAudioSessionOwnership()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Kind != DeviceOperationKind.AudioPrepare) throw new InvalidOperationException("Only prepare can establish audio ownership.");
        _coordinator.CommitAudioPrepare();
        _audioPrepareCommitted = true;
    }

    public void CommitVerifiedRestore()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Kind != DeviceOperationKind.AudioRelease) throw new InvalidOperationException("Only release can confirm restoration.");
        _coordinator.CommitVerifiedAudioRelease();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsSemaphore)
        {
            if (Kind == DeviceOperationKind.AudioPrepare && !_audioPrepareCommitted)
            {
                _coordinator.CancelUncommittedAudioPrepare();
                _prepareCancellation?.Dispose();
            }
            _coordinator.ReleaseSemaphore();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
