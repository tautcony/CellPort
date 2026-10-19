using System.Collections.Immutable;
using CellPort.Audio.Core.Models;
using CellPort.Core.Transport;
using CellPort.Platform.Windows;
using NAudio.Wave;
using Xunit;

namespace CellPort.Platform.Windows.Tests;

public sealed class WindowsAudioEndpointCatalogTests
{
    [Fact]
    public void SameFriendlyNamesRemainSeparateAndMapByPhysicalContainer()
    {
        var modules = new[] { Guid.Parse("2f123456-1111-2222-3333-000000000001"),
            Guid.Parse("2f123456-1111-2222-3333-000000000002") };
        var moduleIds = modules.Select(WindowsAudioDeviceCatalog.CreateDeviceId).ToArray();
        Assert.NotEqual(moduleIds[0], moduleIds[1]);

        var endpoints = WindowsAudioEndpointMapper.Map([
            Info("local-mic", "Microphone", WindowsAudioDataFlow.Capture, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            Info("local-speaker", "Speakers", WindowsAudioDataFlow.Render, Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
            Info("module-a-in", "USB Audio", WindowsAudioDataFlow.Capture, modules[0]),
            Info("module-a-out", "USB Audio", WindowsAudioDataFlow.Render, modules[0]),
            Info("module-b-in", "USB Audio", WindowsAudioDataFlow.Capture, modules[1]),
            Info("module-b-out", "USB Audio", WindowsAudioDataFlow.Render, modules[1]),
        ], modules.ToDictionary(id => id, WindowsAudioDeviceCatalog.CreateDeviceId));

        Assert.Equal(6, endpoints.Length);
        Assert.Equal(4, endpoints.Count(endpoint => endpoint.DisplayName == "USB Audio" && endpoint.IsAvailable));
        Assert.Equal(AudioEndpointRole.ModuleCapture,
            Assert.Single(endpoints.Where(endpoint => endpoint.Id.Value == "module-a-in")).Role);
        Assert.Equal(moduleIds[0], endpoints.Single(endpoint => endpoint.Id.Value == "module-a-out").AssociatedModule);
        Assert.Equal(moduleIds[1], endpoints.Single(endpoint => endpoint.Id.Value == "module-b-out").AssociatedModule);

        var selection = new AudioEndpointSelection(
            new("local-mic"), new("local-speaker"), new("module-a-out"), new("module-a-in"));
        Assert.Null(WindowsAudioEndpointMapper.ValidateSelection(selection, endpoints));
    }

    [Fact]
    public void ModuleDefaultSpeakerCannotBeSelectedAsLocalOutputAndDefaultsAreNotChanged()
    {
        var container = Guid.Parse("2f123456-1111-2222-3333-000000000001");
        var module = WindowsAudioDeviceCatalog.CreateDeviceId(container);
        var endpoints = WindowsAudioEndpointMapper.Map([
            Info("mic", "Microphone", WindowsAudioDataFlow.Capture,
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            Info("default-module-output", "Speakers", WindowsAudioDataFlow.Render, container),
            Info("module-input", "Microphone", WindowsAudioDataFlow.Capture, container),
        ], new Dictionary<Guid, ModuleDeviceId> { [container] = module });

        var selection = new AudioEndpointSelection(new("mic"), new("default-module-output"),
            new("default-module-output"), new("module-input"));

        Assert.Equal(AudioErrorCode.EndpointMissing, WindowsAudioEndpointMapper.ValidateSelection(selection, endpoints)?.Code);
        Assert.Equal(AudioEndpointRole.ModulePlayback,
            endpoints.Single(endpoint => endpoint.Id.Value == "default-module-output").Role);
        // Mapping and validation are read-only; no API here mutates Windows default devices.
    }

    [Fact]
    public void MissingOrRemovedEndpointAndUnassociatedEndpointFailClosed()
    {
        var container = Guid.Parse("2f123456-1111-2222-3333-000000000001");
        var module = WindowsAudioDeviceCatalog.CreateDeviceId(container);
        var modules = new Dictionary<Guid, ModuleDeviceId> { [container] = module };
        var endpoints = WindowsAudioEndpointMapper.Map([
            Info("mic", "Microphone", WindowsAudioDataFlow.Capture,
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            Info("speaker", "Speakers", WindowsAudioDataFlow.Render,
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
            Info("module-out", "USB Audio", WindowsAudioDataFlow.Render, container),
            Info("module-in", "USB Audio", WindowsAudioDataFlow.Capture, container) with { IsActive = false },
            Info("unknown", "Unknown endpoint", WindowsAudioDataFlow.Capture, null),
        ], modules);

        var selection = new AudioEndpointSelection(new("mic"), new("speaker"), new("module-out"), new("module-in"));
        Assert.Equal(AudioErrorCode.EndpointMissing,
            WindowsAudioEndpointMapper.ValidateSelection(selection, endpoints)?.Code);
        Assert.False(endpoints.Single(endpoint => endpoint.Id.Value == "unknown").IsAvailable);
    }

    [Fact]
    public void EndpointsFromOtherModulesAreNotSelectableForTheCurrentModule()
    {
        var firstContainer = Guid.Parse("2f123456-1111-2222-3333-000000000001");
        var secondContainer = Guid.Parse("2f123456-1111-2222-3333-000000000002");
        var firstModule = WindowsAudioDeviceCatalog.CreateDeviceId(firstContainer);
        var secondModule = WindowsAudioDeviceCatalog.CreateDeviceId(secondContainer);
        var endpoints = WindowsAudioEndpointMapper.Map([
            Info("module-a-out", "USB Audio", WindowsAudioDataFlow.Render, firstContainer),
            Info("module-a-in", "USB Audio", WindowsAudioDataFlow.Capture, firstContainer),
            Info("module-b-out", "USB Audio", WindowsAudioDataFlow.Render, secondContainer),
            Info("module-b-in", "USB Audio", WindowsAudioDataFlow.Capture, secondContainer),
        ], new Dictionary<Guid, ModuleDeviceId> { [firstContainer] = firstModule, [secondContainer] = secondModule },
            selectableModule: firstModule);

        Assert.True(endpoints.Single(endpoint => endpoint.Id.Value == "module-a-out").IsAvailable);
        Assert.False(endpoints.Single(endpoint => endpoint.Id.Value == "module-b-out").IsAvailable);
    }

    [Theory]
    [InlineData(8000, 1)]
    [InlineData(44100, 2)]
    [InlineData(48000, 2)]
    public void SupportsSharedModeFloatAndPcm16CaptureFormats(int sampleRate, int channels)
    {
        var floatFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        var pcmFormat = new WaveFormat(sampleRate, 16, channels);

        Assert.True(WindowsAudioSampleCodec.TryGetSupportedEncoding(floatFormat, out var floatEncoding));
        Assert.Equal(WindowsAudioSampleEncoding.Float32, floatEncoding);
        Assert.True(WindowsAudioSampleCodec.TryGetSupportedEncoding(pcmFormat, out var pcmEncoding));
        Assert.Equal(WindowsAudioSampleEncoding.Pcm16, pcmEncoding);
    }

    [Fact]
    public void PcmAndFloatCallbacksDecodeAndUnsupportedFormatsFailExplicitly()
    {
        var floatFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
        var floats = new[] { -0.5f, 0f, 0.75f, float.NaN, 1.5f };
        var floatBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(floats.AsSpan()).ToArray();
        var floatOutput = new float[floats.Length];
        Assert.True(WindowsAudioSampleCodec.TryDecode(floatBytes, floatFormat, floatOutput, out var floatCount));
        Assert.Equal(floats.Length, floatCount);
        Assert.Equal(new[] { -0.5f, 0f, 0.75f, 0f, 1f }, floatOutput);

        var pcmFormat = new WaveFormat(8000, 16, 1);
        var pcmBytes = new byte[] { 0x00, 0x80, 0x00, 0x00, 0x00, 0x40 };
        var pcmOutput = new float[3];
        Assert.True(WindowsAudioSampleCodec.TryDecode(pcmBytes, pcmFormat, pcmOutput, out var pcmCount));
        Assert.Equal(3, pcmCount);
        Assert.Equal(new[] { -1f, 0f, 0.5f }, pcmOutput);

        var unsupported = new WaveFormat(48000, 24, 2);
        Assert.False(WindowsAudioSampleCodec.TryGetSupportedEncoding(unsupported, out _));
        Assert.False(WindowsAudioSampleCodec.TryDecode([0, 0, 0, 0, 0, 0], unsupported, new float[2], out _));
    }

    [Fact]
    public async Task CatalogListsAndRevalidatesDuplicateNameModulesByContainerId()
    {
        var nodes = new[]
        {
            Pnp("2f123456-1111-2222-3333-000000000001", "A", "AT Control Port"),
            Pnp("2f123456-1111-2222-3333-000000000001", "A", "ADB Interface"),
            Pnp("2f123456-1111-2222-3333-000000000002", "B", "AT Control Port"),
            Pnp("2f123456-1111-2222-3333-000000000002", "B", "ADB Interface"),
        };
        var endpoints = ImmutableArray.Create(
            Info("a-in", "USB Audio", WindowsAudioDataFlow.Capture,
                Guid.Parse("2f123456-1111-2222-3333-000000000001")),
            Info("a-out", "USB Audio", WindowsAudioDataFlow.Render,
                Guid.Parse("2f123456-1111-2222-3333-000000000001")),
            Info("b-in", "USB Audio", WindowsAudioDataFlow.Capture,
                Guid.Parse("2f123456-1111-2222-3333-000000000002")),
            Info("b-out", "USB Audio", WindowsAudioDataFlow.Render,
                Guid.Parse("2f123456-1111-2222-3333-000000000002")));
        var catalog = new WindowsAudioDeviceCatalog(() => nodes, new FakeEndpointSource(endpoints));

        var result = await catalog.EnumerateModulesAsync();
        Assert.True(result.Succeeded);
        var candidates = result.Value;
        Assert.Equal(2, candidates.Length);
        Assert.All(candidates, candidate => Assert.Equal(ModuleIdentityStatus.Verified, candidate.IdentityStatus));
        Assert.All(candidates, candidate => Assert.Equal(2, candidate.AssociatedAudioEndpointCount));
        Assert.NotEqual(candidates[0].Id, candidates[1].Id);

        var current = await catalog.RevalidateModuleAsync(candidates[0].Id);
        Assert.True(current.Succeeded);
        Assert.Equal(candidates[0].Id, current.Value!.Id);
    }

    private static WindowsAudioEndpointInfo Info(string id, string name, WindowsAudioDataFlow flow, Guid? container) =>
        new(id, name, flow, container, IsActive: true, "48000 Hz, 2 ch, 32-bit IeeeFloat");

    private static WindowsPnpDeviceNode Pnp(string container, string suffix, string name)
    {
        var containerId = Guid.Parse(container);
        return new WindowsPnpDeviceNode(
            $"USB\\VID_2CA3&PID_4006&MI_{(suffix == "A" ? "01" : "02")}\\DEVICE_{suffix}_{name.Replace(' ', '_')}",
            $"USB\\VID_2CA3&PID_4006\\PARENT_{suffix}",
            containerId,
            [$"PCIROOT(0)#USBROOT(0)#USB({suffix})"],
            ["USB\\VID_2CA3&PID_4006"],
            name);
    }

    private sealed class FakeEndpointSource(ImmutableArray<WindowsAudioEndpointInfo> endpoints)
        : IWindowsAudioEndpointSource
    {
        public ImmutableArray<WindowsAudioEndpointInfo> Enumerate() => endpoints;
    }
}
