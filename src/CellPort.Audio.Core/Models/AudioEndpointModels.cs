namespace CellPort.Audio.Core.Models;

/// <summary>Opaque identifier supplied by the active host audio backend.</summary>
public sealed record AudioEndpointId
{
    public AudioEndpointId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => "[audio-endpoint]";
}

public enum AudioEndpointRole
{
    LocalMicrophoneCapture,
    LocalPlayback,
    ModulePlayback,
    ModuleCapture,
}

/// <summary>Endpoint descriptor without OS-specific endpoint/API types.</summary>
public sealed record AudioEndpointDescriptor(
    AudioEndpointId Id,
    string DisplayName,
    AudioEndpointRole Role,
    ModuleDeviceId? AssociatedModule,
    bool IsAvailable,
    string? FormatSummary);

/// <summary>The four independently selected capture/render endpoints.</summary>
public sealed record AudioEndpointSelection(
    AudioEndpointId LocalMicrophone,
    AudioEndpointId LocalPlayback,
    AudioEndpointId ModulePlayback,
    AudioEndpointId ModuleCapture);
