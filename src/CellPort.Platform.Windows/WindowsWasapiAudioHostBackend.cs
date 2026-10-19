using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using CellPort.Audio.Core.Contracts;
using CellPort.Audio.Core.Models;
using CellPort.Audio.Core.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CellPort.Platform.Windows;

public enum WindowsAudioSampleEncoding
{
    Pcm16,
    Float32,
}

/// <summary>Format validation and allocation-free PCM/float decoding used by WASAPI capture callbacks.</summary>
public static class WindowsAudioSampleCodec
{
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    public static bool TryGetSupportedEncoding(WaveFormat format, out WindowsAudioSampleEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(format);
        encoding = default;
        if (format.Channels is < 1 or > 2 || format.SampleRate is < 8000 or > 192000)
            return false;

        var formatEncoding = format.Encoding;
        var subFormat = format is WaveFormatExtensible extensible ? extensible.SubFormat : Guid.Empty;
        if ((formatEncoding == WaveFormatEncoding.IeeeFloat || subFormat == FloatSubFormat) && format.BitsPerSample == 32)
        {
            encoding = WindowsAudioSampleEncoding.Float32;
            return true;
        }
        if ((formatEncoding == WaveFormatEncoding.Pcm || subFormat == PcmSubFormat) && format.BitsPerSample == 16)
        {
            encoding = WindowsAudioSampleEncoding.Pcm16;
            return true;
        }
        return false;
    }

    public static bool TryDecode(ReadOnlySpan<byte> bytes, WaveFormat format, Span<float> samples, out int sampleCount)
    {
        sampleCount = 0;
        if (!TryGetSupportedEncoding(format, out var encoding) || bytes.Length % format.BlockAlign != 0)
            return false;
        var count = bytes.Length / (format.BitsPerSample / 8);
        if (samples.Length < count) return false;
        if (encoding == WindowsAudioSampleEncoding.Float32)
        {
            MemoryMarshal.Cast<byte, float>(bytes).CopyTo(samples);
        }
        else
        {
            for (var i = 0; i < count; i++)
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * sizeof(short), sizeof(short))) / 32768f;
        }
        for (var i = 0; i < count; i++)
            samples[i] = float.IsFinite(samples[i]) ? Math.Clamp(samples[i], -1f, 1f) : 0f;
        sampleCount = count;
        return true;
    }
}

public sealed class WindowsAudioBackendFailureEventArgs(Guid sessionId, AudioErrorCode errorCode) : EventArgs
{
    public Guid SessionId { get; } = sessionId;
    public AudioErrorCode ErrorCode { get; } = errorCode;
}

/// <summary>
/// Four-endpoint Windows shared-mode WASAPI bridge. It never changes system defaults or
/// device drivers. Every stream is pinned to the explicitly selected MMDevice ID.
/// </summary>
public sealed class WindowsWasapiAudioHostBackend : IAudioHostBackend, IAsyncDisposable
{
    private readonly WindowsAudioDeviceCatalog _catalog;
    private readonly ConcurrentDictionary<Guid, WindowsAudioMediaSession> _sessions = new();
    private int _disposed;

    public WindowsWasapiAudioHostBackend(WindowsAudioDeviceCatalog? catalog = null) =>
        _catalog = catalog ?? new WindowsAudioDeviceCatalog();

    public event EventHandler<WindowsAudioBackendFailureEventArgs>? SessionFailed;

    public ValueTask<AudioOperationResult<ImmutableArray<AudioEndpointDescriptor>>> EnumerateEndpointsAsync(
        ModuleDeviceId deviceId,
        CancellationToken cancellationToken = default) =>
        _catalog.EnumerateEndpointsAsync(deviceId, cancellationToken);

    public async ValueTask<AudioOperationResult<AudioHostSession>> OpenMutedAsync(
        AudioEndpointSelection endpoints,
        bool microphonePermissionGranted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!microphonePermissionGranted)
            return Failure<AudioHostSession>(AudioErrorCode.MicrophonePermissionDenied,
                AudioOperationStage.Media, retryable: false);

        try
        {
            var resolved = await ResolveSelectionAsync(endpoints, cancellationToken).ConfigureAwait(false);
            if (resolved.Error is not null)
                return AudioOperationResult<AudioHostSession>.Failure(resolved.Error);

            using var enumerator = new MMDeviceEnumerator();
            var devices = new Dictionary<AudioEndpointRole, MMDevice>();
            try
            {
                foreach (var descriptor in resolved.Endpoints)
                {
                    var device = enumerator.GetDevice(descriptor.Id.Value);
                    devices.Add(descriptor.Role, device);
                    if (!ValidateCurrentEndpoint(device, descriptor, resolved.ModuleId!, resolved.ModuleContainerId,
                            resolved.KnownModuleContainers))
                        return Failure<AudioHostSession>(AudioErrorCode.EndpointMissing,
                            AudioOperationStage.Routing, retryable: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var session = WindowsAudioMediaSession.Open(
                    endpoints,
                    devices[AudioEndpointRole.LocalMicrophoneCapture],
                    devices[AudioEndpointRole.LocalPlayback],
                    devices[AudioEndpointRole.ModulePlayback],
                    devices[AudioEndpointRole.ModuleCapture],
                    OnSessionFailure);
                devices.Clear(); // Ownership moved into the session.
                if (!_sessions.TryAdd(session.Token.SessionId, session))
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                    return Failure<AudioHostSession>(AudioErrorCode.OperationConflict,
                        AudioOperationStage.Ownership, retryable: true);
                }
                return AudioOperationResult<AudioHostSession>.Success(session.Token);
            }
            finally
            {
                foreach (var device in devices.Values) device.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnsupportedAudioFormatException)
        {
            return Failure<AudioHostSession>(AudioErrorCode.UnsupportedFormat,
                AudioOperationStage.Routing, retryable: false);
        }
        catch
        {
            return Failure<AudioHostSession>(AudioErrorCode.EndpointMissing,
                AudioOperationStage.Routing, retryable: true);
        }
    }

    public ValueTask<AudioOperationResult> SetOutgoingMutedAsync(
        AudioHostSession session,
        bool muted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        if (_sessions.TryGetValue(session.SessionId, out var active) && active.Token == session)
        {
            active.SetMuted(muted);
            return ValueTask.FromResult(AudioOperationResult.Success());
        }
        return ValueTask.FromResult(AudioOperationResult.Failure(
            Error(AudioErrorCode.OwnerMismatch, AudioOperationStage.Ownership, retryable: false)));
    }

    public async ValueTask<AudioOperationResult> StopAsync(
        AudioHostSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryRemove(session.SessionId, out var active))
            return AudioOperationResult.Success();
        if (active.Token != session)
        {
            _sessions.TryAdd(active.Token.SessionId, active);
            return AudioOperationResult.Failure(
                Error(AudioErrorCode.OwnerMismatch, AudioOperationStage.Ownership, retryable: false));
        }
        await active.DisposeAsync().ConfigureAwait(false);
        return AudioOperationResult.Success();
    }

    private async ValueTask<ResolvedWindowsAudioSelection> ResolveSelectionAsync(
        AudioEndpointSelection selection,
        CancellationToken cancellationToken)
    {
        var modules = await _catalog.EnumerateModulesAsync(cancellationToken).ConfigureAwait(false);
        if (!modules.Succeeded || modules.Value.IsDefaultOrEmpty)
            return ResolvedWindowsAudioSelection.Fail(Error(AudioErrorCode.DeviceNotFound,
                AudioOperationStage.DeviceDiscovery, retryable: true));

        foreach (var module in modules.Value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (module.IdentityStatus != ModuleIdentityStatus.Verified) continue;
            var listed = await _catalog.EnumerateEndpointsAsync(module.Id, cancellationToken).ConfigureAwait(false);
            if (!listed.Succeeded) continue;
            var selected = listed.Value.Where(endpoint =>
                    endpoint.Id == selection.LocalMicrophone || endpoint.Id == selection.LocalPlayback ||
                    endpoint.Id == selection.ModulePlayback || endpoint.Id == selection.ModuleCapture)
                .ToImmutableArray();
            if (selected.Length != 4) continue;
            var modulePlayback = listed.Value.Single(endpoint => endpoint.Id == selection.ModulePlayback);
            var moduleCapture = listed.Value.Single(endpoint => endpoint.Id == selection.ModuleCapture);
            if (modulePlayback.AssociatedModule != module.Id || moduleCapture.AssociatedModule != module.Id)
                continue;
            var error = WindowsAudioEndpointMapper.ValidateSelection(selection, listed.Value);
            if (error is not null) return ResolvedWindowsAudioSelection.Fail(error);
            if (!_catalog.TryGetContainerId(module.Id, out var containerId))
                return ResolvedWindowsAudioSelection.Fail(Error(AudioErrorCode.DeviceIdentityAmbiguous,
                    AudioOperationStage.DeviceDiscovery, retryable: false));
            return new(selected, module.Id, containerId, _catalog.GetKnownModuleContainers(), null);
        }
        return ResolvedWindowsAudioSelection.Fail(Error(AudioErrorCode.DeviceIdentityAmbiguous,
            AudioOperationStage.Routing, retryable: false));
    }

    private bool ValidateCurrentEndpoint(
        MMDevice device,
        AudioEndpointDescriptor descriptor,
        ModuleDeviceId selectedModule,
        Guid selectedContainer,
        IReadOnlyDictionary<Guid, ModuleDeviceId> knownModuleContainers)
    {
        if (device.State != DeviceState.Active) return false;
        var expectedFlow = descriptor.Role is AudioEndpointRole.LocalMicrophoneCapture or AudioEndpointRole.ModuleCapture
            ? DataFlow.Capture
            : DataFlow.Render;
        if (device.DataFlow != expectedFlow) return false;
        var containerId = WindowsMmDeviceEndpointSource.ReadContainerId(device);
        if (containerId is null) return false;
        if (descriptor.Role is AudioEndpointRole.ModuleCapture or AudioEndpointRole.ModulePlayback)
            return descriptor.AssociatedModule == selectedModule && containerId == selectedContainer;
        return descriptor.AssociatedModule is null && containerId != selectedContainer &&
               !knownModuleContainers.ContainsKey(containerId.Value);
    }

    private void OnSessionFailure(AudioErrorCode errorCode, Guid sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session)) session.SetMuted(true);
        var handler = SessionFailed;
        if (handler is null) return;
        var args = new WindowsAudioBackendFailureEventArgs(sessionId, errorCode);
        foreach (EventHandler<WindowsAudioBackendFailureEventArgs> subscriber in handler.GetInvocationList())
        {
            try { subscriber(this, args); }
            catch { /* Diagnostics cannot reopen a failed media route. */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var (id, session) in _sessions.ToArray())
        {
            if (_sessions.TryRemove(id, out _)) await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static AudioOperationResult<T> Failure<T>(AudioErrorCode code, AudioOperationStage stage, bool retryable) =>
        AudioOperationResult<T>.Failure(Error(code, stage, retryable));

    private static AudioOperationError Error(AudioErrorCode code, AudioOperationStage stage, bool retryable) =>
        new(code, stage, retryable, AudioRecoveryStatus.None);

    private sealed record ResolvedWindowsAudioSelection(
        ImmutableArray<AudioEndpointDescriptor> Endpoints,
        ModuleDeviceId? ModuleId,
        Guid ModuleContainerId,
        IReadOnlyDictionary<Guid, ModuleDeviceId> KnownModuleContainers,
        AudioOperationError? Error)
    {
        public static ResolvedWindowsAudioSelection Fail(AudioOperationError error) =>
            new([], null, Guid.Empty, new Dictionary<Guid, ModuleDeviceId>(), error);
    }
}

internal sealed class WindowsAudioMediaSession : IAsyncDisposable
{
    private readonly MMDevice[] _devices;
    private WasapiRecorder _microphoneRecorder = null!;
    private WasapiRecorder _moduleCaptureRecorder = null!;
    private WasapiPlayer _modulePlaybackPlayer = null!;
    private WasapiPlayer _localPlaybackPlayer = null!;
    private readonly AudioFrameBuffer _uplinkBuffer = new(8000, 1);
    private readonly AudioFrameBuffer _downlinkBuffer = new(48000, 2);
    private readonly float[] _inputSamples = new float[4096];
    private readonly float[] _convertedSamples = new float[32768];
    private readonly FloatRingWaveProvider _modulePlaybackProvider;
    private readonly FloatRingWaveProvider _localPlaybackProvider;
    private readonly Action<AudioErrorCode, Guid> _failure;
    private AudioFormatConverter? _microphoneConverter;
    private AudioFormatConverter? _moduleCaptureConverter;
    private int _muted = 1;
    private int _stopped;
    private long _microphoneFramePosition;
    private long _moduleCaptureFramePosition;
    private long _modulePlaybackFramePosition;
    private long _localPlaybackFramePosition;

    public long MicrophoneFramePosition => Interlocked.Read(ref _microphoneFramePosition);
    public long ModuleCaptureFramePosition => Interlocked.Read(ref _moduleCaptureFramePosition);
    public long ModulePlaybackFramePosition => Interlocked.Read(ref _modulePlaybackFramePosition);
    public long LocalPlaybackFramePosition => Interlocked.Read(ref _localPlaybackFramePosition);

    private WindowsAudioMediaSession(
        AudioHostSession token,
        MMDevice localMicrophone,
        MMDevice localPlayback,
        MMDevice modulePlayback,
        MMDevice moduleCapture,
        Action<AudioErrorCode, Guid> failure)
    {
        Token = token;
        _devices = [localMicrophone, localPlayback, modulePlayback, moduleCapture];
        _failure = failure;
        _modulePlaybackProvider = new FloatRingWaveProvider(_uplinkBuffer, 8000, 1,
            () => Volatile.Read(ref _muted) != 0,
            frames => Interlocked.Add(ref _modulePlaybackFramePosition, frames));
        _localPlaybackProvider = new FloatRingWaveProvider(_downlinkBuffer, 48000, 2,
            () => false, frames => Interlocked.Add(ref _localPlaybackFramePosition, frames));

        WasapiRecorder? microphoneRecorder = null;
        WasapiRecorder? moduleCaptureRecorder = null;
        WasapiPlayer? modulePlaybackPlayer = null;
        WasapiPlayer? localPlaybackPlayer = null;
        try
        {
            microphoneRecorder = BuildRecorder(localMicrophone, 48000, 2);
            moduleCaptureRecorder = BuildRecorder(moduleCapture, 8000, 1);
            if (!WindowsAudioSampleCodec.TryGetSupportedEncoding(microphoneRecorder.WaveFormat, out _) ||
                !WindowsAudioSampleCodec.TryGetSupportedEncoding(moduleCaptureRecorder.WaveFormat, out _))
                throw new UnsupportedAudioFormatException();
            _microphoneConverter = new AudioFormatConverter(microphoneRecorder.WaveFormat.SampleRate,
                microphoneRecorder.WaveFormat.Channels, 8000, 1);
            _moduleCaptureConverter = new AudioFormatConverter(moduleCaptureRecorder.WaveFormat.SampleRate,
                moduleCaptureRecorder.WaveFormat.Channels, 48000, 2);

            modulePlaybackPlayer = BuildPlayer(modulePlayback, _modulePlaybackProvider);
            localPlaybackPlayer = BuildPlayer(localPlayback, _localPlaybackProvider);

            _microphoneRecorder = microphoneRecorder;
            _moduleCaptureRecorder = moduleCaptureRecorder;
            _modulePlaybackPlayer = modulePlaybackPlayer;
            _localPlaybackPlayer = localPlaybackPlayer;

            _microphoneRecorder.DataAvailable += OnMicrophoneData;
            _moduleCaptureRecorder.DataAvailable += OnModuleCaptureData;
            _microphoneRecorder.RecordingStopped += OnMicrophoneStopped;
            _moduleCaptureRecorder.RecordingStopped += OnModuleCaptureStopped;
            _modulePlaybackPlayer.PlaybackStopped += OnModulePlaybackStopped;
            _localPlaybackPlayer.PlaybackStopped += OnLocalPlaybackStopped;
        }
        catch
        {
            microphoneRecorder?.Dispose();
            moduleCaptureRecorder?.Dispose();
            modulePlaybackPlayer?.Dispose();
            localPlaybackPlayer?.Dispose();
            throw;
        }
    }

    public AudioHostSession Token { get; }

    public static WindowsAudioMediaSession Open(
        AudioEndpointSelection endpoints,
        MMDevice localMicrophone,
        MMDevice localPlayback,
        MMDevice modulePlayback,
        MMDevice moduleCapture,
        Action<AudioErrorCode, Guid> failure)
    {
        var session = new WindowsAudioMediaSession(
            new AudioHostSession(Guid.NewGuid(), endpoints),
            localMicrophone, localPlayback, modulePlayback, moduleCapture, failure);
        try
        {
            session._modulePlaybackPlayer.Play();
            session._localPlaybackPlayer.Play();
            session._moduleCaptureRecorder.StartRecording();
            session._microphoneRecorder.StartRecording();
            return session;
        }
        catch
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    public void SetMuted(bool muted) => Volatile.Write(ref _muted, muted ? 1 : 0);

    private static WasapiRecorder BuildRecorder(MMDevice device, int rate, int channels) =>
        new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithBufferLength(30)
            .WithMmcssThreadPriority("Audio")
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(rate, channels))
            .Build();

    private static WasapiPlayer BuildPlayer(MMDevice device, IWaveProvider provider)
    {
        var player = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(40)
            .WithMmcssThreadPriority("Audio")
            .Build();
        try
        {
            player.Init(provider);
            return player;
        }
        catch
        {
            player.Dispose();
            throw;
        }
    }

    private void OnMicrophoneData(ReadOnlySpan<byte> bytes, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var format = _microphoneRecorder.WaveFormat;
        var frames = bytes.Length / Math.Max(1, format.BlockAlign);
        ObserveFramePosition(ref _microphoneFramePosition, devicePosition + frames);
        if (Volatile.Read(ref _muted) != 0 || _microphoneConverter is null) return;
        ProcessCapture(bytes, format, _microphoneConverter, _uplinkBuffer,
            (flags & AudioClientBufferFlags.Silent) != 0);
    }

    private void OnModuleCaptureData(ReadOnlySpan<byte> bytes, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var format = _moduleCaptureRecorder.WaveFormat;
        var frames = bytes.Length / Math.Max(1, format.BlockAlign);
        ObserveFramePosition(ref _moduleCaptureFramePosition, devicePosition + frames);
        if (_moduleCaptureConverter is null) return;
        ProcessCapture(bytes, format, _moduleCaptureConverter, _downlinkBuffer,
            (flags & AudioClientBufferFlags.Silent) != 0);
    }

    private void ProcessCapture(ReadOnlySpan<byte> bytes, WaveFormat format,
        AudioFormatConverter converter, AudioFrameBuffer destination, bool silent)
    {
        if (bytes.Length % format.BlockAlign != 0)
        {
            Fail(AudioErrorCode.UnsupportedFormat);
            return;
        }

        var remainingFrames = bytes.Length / format.BlockAlign;
        var sourceFrame = 0;
        while (remainingFrames > 0)
        {
            var inputFrameCapacity = _inputSamples.Length / format.Channels;
            var frames = Math.Min(remainingFrames, inputFrameCapacity);
            var sourceBytes = bytes.Slice(sourceFrame * format.BlockAlign, frames * format.BlockAlign);
            var input = _inputSamples.AsSpan(0, frames * format.Channels);
            int sampleCount;
            if (silent)
            {
                input.Clear();
                sampleCount = input.Length;
            }
            else if (!WindowsAudioSampleCodec.TryDecode(sourceBytes, format, input, out sampleCount))
            {
                Fail(AudioErrorCode.UnsupportedFormat);
                return;
            }
            var output = _convertedSamples.AsSpan();
            var convertedFrames = converter.Convert(input[..sampleCount], output);
            destination.Write(output[..(convertedFrames * converter.OutputChannels)]);
            sourceFrame += frames;
            remainingFrames -= frames;
        }
    }

    private static void ObserveFramePosition(ref long position, long observed)
    {
        while (true)
        {
            var previous = Interlocked.Read(ref position);
            if (observed <= previous || Interlocked.CompareExchange(ref position, observed, previous) == previous)
                return;
        }
    }

    private void OnMicrophoneStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null) Fail(AudioErrorCode.EndpointMissing);
    }

    private void OnModuleCaptureStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null) Fail(AudioErrorCode.EndpointMissing);
    }

    private void OnModulePlaybackStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null) Fail(AudioErrorCode.EndpointMissing);
    }

    private void OnLocalPlaybackStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null) Fail(AudioErrorCode.EndpointMissing);
    }

    private void Fail(AudioErrorCode code)
    {
        SetMuted(true);
        try { _failure(code, Token.SessionId); }
        catch { /* The backend remains muted if a listener fails. */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        SetMuted(true);
        _microphoneRecorder.DataAvailable -= OnMicrophoneData;
        _moduleCaptureRecorder.DataAvailable -= OnModuleCaptureData;
        _microphoneRecorder.RecordingStopped -= OnMicrophoneStopped;
        _moduleCaptureRecorder.RecordingStopped -= OnModuleCaptureStopped;
        _modulePlaybackPlayer.PlaybackStopped -= OnModulePlaybackStopped;
        _localPlaybackPlayer.PlaybackStopped -= OnLocalPlaybackStopped;
        try { _microphoneRecorder.StopRecording(); } catch { }
        try { _moduleCaptureRecorder.StopRecording(); } catch { }
        try { _modulePlaybackPlayer.Stop(); } catch { }
        try { _localPlaybackPlayer.Stop(); } catch { }
        await _microphoneRecorder.DisposeAsync().ConfigureAwait(false);
        await _moduleCaptureRecorder.DisposeAsync().ConfigureAwait(false);
        await _modulePlaybackPlayer.DisposeAsync().ConfigureAwait(false);
        await _localPlaybackPlayer.DisposeAsync().ConfigureAwait(false);
        foreach (var device in _devices) device.Dispose();
    }
}

internal sealed class FloatRingWaveProvider : IWaveProvider
{
    private readonly AudioFrameBuffer _buffer;
    private readonly int _channels;
    private readonly float[] _scratch = new float[8192];
    private readonly Func<bool> _muted;
    private readonly Action<int> _onFramesRendered;

    public FloatRingWaveProvider(AudioFrameBuffer buffer, int sampleRate, int channels,
        Func<bool> muted, Action<int> onFramesRendered)
    {
        _buffer = buffer;
        _channels = channels;
        _muted = muted;
        _onFramesRendered = onFramesRendered;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<byte> buffer)
    {
        var alignedCount = buffer.Length - (buffer.Length % WaveFormat.BlockAlign);
        var destination = buffer[..alignedCount];
        var remainingFrames = alignedCount / WaveFormat.BlockAlign;
        var framePosition = 0;
        while (remainingFrames > 0)
        {
            var frames = Math.Min(remainingFrames, _scratch.Length / _channels);
            var samples = _scratch.AsSpan(0, frames * _channels);
            _buffer.Read(samples);
            var bytes = destination.Slice(framePosition * WaveFormat.BlockAlign, frames * WaveFormat.BlockAlign);
            if (_muted()) bytes.Clear();
            else
            {
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = float.IsFinite(samples[i]) ? Math.Clamp(samples[i], -1f, 1f) : 0f;
                MemoryMarshal.AsBytes(samples).CopyTo(bytes);
            }
            _onFramesRendered(frames);
            framePosition += frames;
            remainingFrames -= frames;
        }
        return alignedCount;
    }
}

internal sealed class UnsupportedAudioFormatException : Exception { }
