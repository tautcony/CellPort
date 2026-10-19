namespace CellPort.Audio.Core.Services;

/// <summary>
/// Preallocated single-producer/single-consumer audio ring. Capacity is fixed at
/// 120 ms and operations use monotonic indices with no lock or per-call allocation.
/// </summary>
public sealed class AudioFrameBuffer
{
    public static readonly TimeSpan MaximumBufferedDuration = TimeSpan.FromMilliseconds(120);

    private readonly float[] _samples;
    private readonly int _channels;
    private readonly int _capacityFrames;
    private long _readFrame;
    private long _writeFrame;
    private long _framesWritten;
    private long _framesDropped;
    private long _underrunFrames;

    public AudioFrameBuffer(int sampleRate, int channels)
    {
        if (sampleRate is < 8000 or > 192000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
        SampleRate = sampleRate;
        _channels = channels;
        _capacityFrames = Math.Max(1, (int)Math.Floor(sampleRate * MaximumBufferedDuration.TotalSeconds));
        _samples = new float[checked(_capacityFrames * channels)];
    }

    public int SampleRate { get; }
    public int Channels => _channels;
    public int CapacityFrames => _capacityFrames;
    public TimeSpan Capacity => TimeSpan.FromSeconds((double)_capacityFrames / SampleRate);
    public long FramesWritten => Interlocked.Read(ref _framesWritten);
    public long FramesDropped => Interlocked.Read(ref _framesDropped);
    public long UnderrunFrames => Interlocked.Read(ref _underrunFrames);

    public int FramesAvailable
    {
        get
        {
            var available = Volatile.Read(ref _writeFrame) - Volatile.Read(ref _readFrame);
            return (int)Math.Clamp(available, 0, _capacityFrames);
        }
    }

    /// <summary>Writes as much of this callback as fits; excess frames are counted and dropped.</summary>
    public int Write(ReadOnlySpan<float> interleavedSamples)
    {
        if (interleavedSamples.Length % _channels != 0)
            throw new ArgumentException("The sample count must contain complete frames.", nameof(interleavedSamples));
        var inputFrames = interleavedSamples.Length / _channels;
        if (inputFrames == 0) return 0;

        var write = _writeFrame;
        var read = Volatile.Read(ref _readFrame);
        var used = Math.Clamp(write - read, 0, _capacityFrames);
        var accepted = Math.Min(inputFrames, _capacityFrames - (int)used);
        for (var frame = 0; frame < accepted; frame++)
        {
            var target = (int)((write + frame) % _capacityFrames) * _channels;
            interleavedSamples.Slice(frame * _channels, _channels).CopyTo(_samples.AsSpan(target, _channels));
        }
        Volatile.Write(ref _writeFrame, write + accepted);
        Interlocked.Add(ref _framesWritten, accepted);
        Interlocked.Add(ref _framesDropped, inputFrames - accepted);
        return accepted;
    }

    /// <summary>Reads frames and zero-fills the rest, so old/uninitialized samples never escape.</summary>
    public int Read(Span<float> destination)
    {
        if (destination.Length % _channels != 0)
            throw new ArgumentException("The sample count must contain complete frames.", nameof(destination));
        var requested = destination.Length / _channels;
        if (requested == 0) return 0;

        var read = _readFrame;
        var write = Volatile.Read(ref _writeFrame);
        var available = (int)Math.Clamp(write - read, 0, _capacityFrames);
        var count = Math.Min(requested, available);
        for (var frame = 0; frame < count; frame++)
        {
            var source = (int)((read + frame) % _capacityFrames) * _channels;
            _samples.AsSpan(source, _channels).CopyTo(destination.Slice(frame * _channels, _channels));
        }
        destination[(count * _channels)..].Clear();
        Volatile.Write(ref _readFrame, read + count);
        Interlocked.Add(ref _underrunFrames, requested - count);
        return count;
    }

    /// <summary>Consumer-side stale-frame discard; safe to call before reading an old queue.</summary>
    public int DiscardOldest(int frames)
    {
        if (frames < 0) throw new ArgumentOutOfRangeException(nameof(frames));
        var read = _readFrame;
        var write = Volatile.Read(ref _writeFrame);
        var available = (int)Math.Clamp(write - read, 0, _capacityFrames);
        var discarded = Math.Min(frames, available);
        Volatile.Write(ref _readFrame, read + discarded);
        Interlocked.Add(ref _framesDropped, discarded);
        return discarded;
    }
}

/// <summary>Stateful low-pass-filtered linear resampler with mono/stereo conversion.</summary>
public sealed class AudioFormatConverter
{
    private readonly double _nominalStep;
    private double _step;
    private readonly double _filterAlpha;
    private readonly int _inputChannels;
    private readonly int _outputChannels;
    private readonly double[] _filterState;
    private readonly float[] _pendingSamples;
    private readonly int _pendingFrameCapacity;
    private int _pendingHeadFrame;
    private int _pendingFrameCount;
    private double _nextOutputPosition;
    private bool _filterInitialized;

    public AudioFormatConverter(int inputRate, int inputChannels, int outputRate, int outputChannels)
    {
        if (inputRate is < 8000 or > 192000) throw new ArgumentOutOfRangeException(nameof(inputRate));
        if (outputRate is < 8000 or > 192000) throw new ArgumentOutOfRangeException(nameof(outputRate));
        if (inputChannels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(inputChannels));
        if (outputChannels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(outputChannels));
        InputRate = inputRate;
        OutputRate = outputRate;
        _inputChannels = inputChannels;
        _outputChannels = outputChannels;
        _nominalStep = (double)inputRate / outputRate;
        _step = _nominalStep;
        var cutoffHz = Math.Min(inputRate, outputRate) * 0.45;
        _filterAlpha = 1.0 - Math.Exp(-2.0 * Math.PI * cutoffHz / inputRate);
        _filterState = new double[inputChannels];
        _pendingFrameCapacity = checked((int)Math.Ceiling((double)inputRate / outputRate) + 4);
        _pendingSamples = new float[checked(_pendingFrameCapacity * inputChannels)];
    }

    public int InputRate { get; }
    public int OutputRate { get; }
    public int InputChannels => _inputChannels;
    public int OutputChannels => _outputChannels;

    public double ResamplingRatio => Volatile.Read(ref _step) / _nominalStep;

    /// <summary>Applies a bounded clock-drift correction supplied by the health controller.</summary>
    public void SetResamplingRatio(double ratio)
    {
        if (!double.IsFinite(ratio) || ratio is < 0.999 or > 1.001)
            throw new ArgumentOutOfRangeException(nameof(ratio));
        Volatile.Write(ref _step, _nominalStep * ratio);
    }

    /// <summary>
    /// Converts a variable-sized callback into caller-provided storage. Filter history and
    /// fractional resampling phase carry across callbacks; incomplete edge interpolation is
    /// retained for the next callback instead of resetting at packet boundaries.
    /// </summary>
    public int Convert(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length % _inputChannels != 0)
            throw new ArgumentException("Input must contain complete frames.", nameof(input));
        if (output.Length % _outputChannels != 0)
            throw new ArgumentException("Output must contain complete frames.", nameof(output));
        var inputFrames = input.Length / _inputChannels;
        if (inputFrames == 0) return 0;
        var outputCapacity = output.Length / _outputChannels;
        var step = Volatile.Read(ref _step);
        var requiredOutputFrames = CountRenderableFrames(inputFrames, step);
        if (requiredOutputFrames > outputCapacity)
            throw new ArgumentException("Output storage is too small for this input block.", nameof(output));

        var outputFrame = 0;
        for (var inputFrame = 0; inputFrame < inputFrames; inputFrame++)
        {
            AppendFilteredFrame(input.Slice(inputFrame * _inputChannels, _inputChannels));
            while (CanRender(_nextOutputPosition, _pendingFrameCount))
            {
                RenderFrame(output.Slice(outputFrame * _outputChannels, _outputChannels));
                outputFrame++;
                _nextOutputPosition += step;
                DiscardConsumedFrames();
            }
        }
        return outputFrame;
    }

    private int CountRenderableFrames(int newInputFrames, double step)
    {
        var availableFrames = (double)_pendingFrameCount + newInputFrames;
        var position = _nextOutputPosition;
        var count = 0;
        while (CanRender(position, availableFrames))
        {
            count++;
            position += step;
        }
        return count;
    }

    private static bool CanRender(double position, double availableFrames)
    {
        if (position >= availableFrames) return false;
        var left = Math.Floor(position);
        var fraction = position - left;
        return fraction < 1e-12 || left + 1 < availableFrames;
    }

    private void AppendFilteredFrame(ReadOnlySpan<float> inputFrame)
    {
        if (_pendingFrameCount == _pendingFrameCapacity)
            throw new InvalidOperationException("The resampling history exceeded its bounded capacity.");
        var targetFrame = (_pendingHeadFrame + _pendingFrameCount) % _pendingFrameCapacity;
        var targetOffset = targetFrame * _inputChannels;
        for (var channel = 0; channel < _inputChannels; channel++)
        {
            var raw = inputFrame[channel];
            _filterState[channel] = _filterInitialized
                ? _filterState[channel] + (_filterAlpha * (raw - _filterState[channel]))
                : raw;
            _pendingSamples[targetOffset + channel] = (float)_filterState[channel];
        }
        _filterInitialized = true;
        _pendingFrameCount++;
    }

    private void RenderFrame(Span<float> destination)
    {
        var leftFrame = (int)Math.Floor(_nextOutputPosition);
        var fraction = (float)(_nextOutputPosition - leftFrame);
        var rightFrame = fraction < 1e-12f ? leftFrame : leftFrame + 1;
        var leftOffset = ((_pendingHeadFrame + leftFrame) % _pendingFrameCapacity) * _inputChannels;
        var rightOffset = ((_pendingHeadFrame + rightFrame) % _pendingFrameCapacity) * _inputChannels;
        var sampleLeft = Interpolate(_pendingSamples[leftOffset], _pendingSamples[rightOffset], fraction);
        var sampleRight = _inputChannels == 1
            ? sampleLeft
            : Interpolate(_pendingSamples[leftOffset + 1], _pendingSamples[rightOffset + 1], fraction);

        if (_outputChannels == 1)
            destination[0] = (sampleLeft + sampleRight) * 0.5f;
        else
        {
            destination[0] = sampleLeft;
            destination[1] = sampleRight;
        }
    }

    private static float Interpolate(float left, float right, float fraction) =>
        left + ((right - left) * fraction);

    private void DiscardConsumedFrames()
    {
        var wholeFrames = Math.Min(_pendingFrameCount, (int)Math.Floor(_nextOutputPosition));
        if (wholeFrames <= 0) return;
        _pendingHeadFrame = (_pendingHeadFrame + wholeFrames) % _pendingFrameCapacity;
        _pendingFrameCount -= wholeFrames;
        _nextOutputPosition -= wholeFrames;
    }
}
