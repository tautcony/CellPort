using CellPort.Audio.Core.Services;
using Xunit;

namespace CellPort.Audio.Core.Tests;

public sealed class AudioFrameBufferTests
{
    [Fact]
    public void CapacityIsCappedAt120MillisecondsAndOverrunsAreCounted()
    {
        var buffer = new AudioFrameBuffer(48000, 2);
        Assert.Equal(5760, buffer.CapacityFrames);
        Assert.Equal(TimeSpan.FromMilliseconds(120), buffer.Capacity);
        var source = Enumerable.Repeat(0.25f, 5762 * 2).ToArray();

        Assert.Equal(5760, buffer.Write(source));
        Assert.Equal(5760, buffer.FramesAvailable);
        Assert.Equal(2, buffer.FramesDropped);

        var destination = new float[5763 * 2];
        Assert.Equal(5760, buffer.Read(destination));
        Assert.All(destination.Take(5760 * 2), sample => Assert.Equal(0.25f, sample));
        Assert.All(destination.Skip(5760 * 2), sample => Assert.Equal(0f, sample));
        Assert.Equal(3, buffer.UnderrunFrames);
    }

    [Fact]
    public void ConsumerCanDiscardStaleFramesAndTheBufferDoesNotShareAControlLock()
    {
        var buffer = new AudioFrameBuffer(8000, 1);
        Assert.Equal(5, buffer.Write([1, 2, 3, 4, 5]));
        Assert.Equal(2, buffer.DiscardOldest(2));
        Span<float> output = stackalloc float[3];
        Assert.Equal(3, buffer.Read(output));
        Assert.Equal(new float[] { 3, 4, 5 }, output.ToArray());
        Assert.Equal(2, buffer.FramesDropped);

        ReadOnlySpan<float> tail = stackalloc float[] { 6, 7 };
        var before = GC.GetAllocatedBytesForCurrentThread();
        buffer.Write(tail);
        buffer.Read(output[..2]);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ConverterHandlesVariableBlocksChannelMappingAndResamplingWithoutAllocating()
    {
        var converter = new AudioFormatConverter(48000, 2, 44100, 1);
        var blockA = Enumerable.Range(0, 96 * 2).Select(i => MathF.Sin(i * 0.03f)).ToArray();
        var blockB = Enumerable.Range(0, 257 * 2).Select(i => MathF.Cos(i * 0.02f)).ToArray();
        var output = new float[300];

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var firstFrames = converter.Convert(blockA, output);
        var secondFrames = converter.Convert(blockB, output);
        var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal((int)Math.Floor(96 * 44100d / 48000d), firstFrames);
        Assert.Equal((int)Math.Floor(257 * 44100d / 48000d), secondFrames);
        Assert.All(output.Take(secondFrames), sample => Assert.True(float.IsFinite(sample)));
        Assert.Equal(0, allocatedAfter - allocatedBefore);
    }

    [Fact]
    public void ConverterUpsamplesMonoToStereoAndRejectsPartialFrames()
    {
        var converter = new AudioFormatConverter(8000, 1, 48000, 2);
        var output = new float[26];
        var frames = converter.Convert([0.5f], output);
        Assert.Equal(1, frames);
        frames += converter.Convert([0.5f], output.AsSpan(frames * 2));
        frames += converter.Convert([0.5f], output.AsSpan(frames * 2));
        Assert.Equal(13, frames);
        Assert.Equal(Enumerable.Repeat(0.5f, 26), output[..26]);
        Assert.Throws<ArgumentException>(() => converter.Convert([0.1f], output[..3]));
    }
}
