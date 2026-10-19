using CellPort.Audio.Core.Services;
using Xunit;

namespace CellPort.Audio.Core.Tests;

public sealed class AudioMediaHealthTests
{
    [Theory]
    [InlineData(100d)]
    [InlineData(-100d)]
    [InlineData(300d)]
    [InlineData(-300d)]
    public void DriftCorrectionTracksSmallMeasuredOffsets(double measuredPpm)
    {
        var controller = new AudioClockDriftController(maximumCorrectionPpm: 500);
        var ratio = controller.Update(measuredPpm);

        Assert.Equal(measuredPpm, controller.CorrectionPpm);
        Assert.Equal(1.0 + measuredPpm / 1_000_000.0, ratio, precision: 9);
        Assert.Equal(ratio, controller.ResamplingRatio);
    }

    [Fact]
    public void DriftCorrectionAndConverterRemainBounded()
    {
        var controller = new AudioClockDriftController(maximumCorrectionPpm: 300);
        var converter = new AudioFormatConverter(48000, 1, 44100, 1);
        var ratio = controller.Update(900);
        converter.SetResamplingRatio(ratio);

        Assert.Equal(300, controller.CorrectionPpm);
        Assert.Equal(1.0003, converter.ResamplingRatio, precision: 9);
        Assert.Throws<ArgumentOutOfRangeException>(() => converter.SetResamplingRatio(1.01));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.Update(double.NaN));
    }

    [Fact]
    public void AdvancingSilenceIsHealthyButOneStoppedDirectionClosesOutgoingAfterOneSecond()
    {
        var time = new ManualTimeProvider();
        var monitor = new AudioMediaHealthMonitor(time);
        var closeCount = 0;
        monitor.OutgoingMustClose += (_, _) => closeCount++;
        monitor.Start();

        time.Advance(TimeSpan.FromMilliseconds(500));
        monitor.ObserveCaptureFramePosition(480); // Frames advance even when sample values are all zero.
        monitor.ObservePlaybackFramePosition(480);
        Assert.Null(monitor.Evaluate().StalledDirection);

        time.Advance(TimeSpan.FromMilliseconds(500));
        monitor.ObserveCaptureFramePosition(960);
        Assert.Null(monitor.Evaluate().StalledDirection);

        time.Advance(TimeSpan.FromMilliseconds(500));
        monitor.ObserveCaptureFramePosition(1440);
        var stalled = monitor.Evaluate();
        Assert.Equal(AudioMediaDirection.Playback, stalled.StalledDirection);
        Assert.True(stalled.CaptureAdvancing);
        Assert.False(stalled.PlaybackAdvancing);
        Assert.True(stalled.OutgoingMustClose);
        Assert.Equal(1, closeCount);
        Assert.True(monitor.Evaluate().OutgoingMustClose);
        Assert.Equal(1, closeCount);
    }

    [Fact]
    public void RemovedEndpointClosesOutgoingImmediately()
    {
        var monitor = new AudioMediaHealthMonitor(new ManualTimeProvider());
        var closeCount = 0;
        monitor.OutgoingMustClose += (_, _) => closeCount++;
        monitor.Start();

        monitor.SetEndpointAvailable(AudioMediaDirection.Capture, false);

        Assert.Equal(1, closeCount);
        Assert.Equal(AudioMediaDirection.Capture, monitor.Evaluate().StalledDirection);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, duration.Ticks);
    }
}
