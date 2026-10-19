namespace CellPort.Audio.Core.Models;

/// <summary>
/// Opaque host-independent key for one selected physical module. Do not use a
/// friendly name, COM path, interface number, or ADB serial as this key.
/// </summary>
public sealed record ModuleDeviceId
{
    public ModuleDeviceId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    // Keep accidental string interpolation from exposing physical identifiers.
    public override string ToString() => "[module-device]";
}

public enum ModuleIdentityStatus
{
    Verified,
    Ambiguous,
    Unverified,
    Changed,
}

/// <summary>Platform-neutral summary of a discovered module candidate.</summary>
public sealed record ModuleDeviceCandidate(
    ModuleDeviceId Id,
    string DisplayName,
    ModuleIdentityStatus IdentityStatus,
    bool HasAtControlChannel,
    bool HasAdbControlChannel,
    int AssociatedAudioEndpointCount);
