using System.Collections.Immutable;
using System.Globalization;

namespace CellPort.Core.Transport;

public sealed record AdbDeviceCandidate(
    string Serial,
    string State,
    long? TransportId,
    string? PhysicalIdentityKey,
    bool SerialReliable);

public sealed record AdbTransportSelection(
    long TransportId,
    string? Serial,
    bool SerialReliable,
    string PhysicalIdentityKey,
    long CatalogGeneration);

public enum AdbSelectionStatus
{
    Selected,
    NoMatchingDevice,
    IdentityUnassociated,
    Ambiguous,
    TransportUnavailable,
}

public sealed record AdbSelectionResult(AdbSelectionStatus Status, AdbTransportSelection? Selection);

public enum ModuleIdentityRevalidationStatus
{
    Valid,
    StaleSession,
    IdentityIncomplete,
    PhysicalIdentityChanged,
    BootChanged,
    OwnerChanged,
}

/// <summary>Revalidation evidence. Its string form deliberately omits serial, boot ID, and owner token.</summary>
public sealed class ModuleIdentityEvidence
{
    public ModuleIdentityEvidence(
        Guid containerId,
        string parentInstanceId,
        IEnumerable<string> locationPaths,
        string? reliableSerial,
        string bootId,
        string ownerToken)
    {
        ContainerId = containerId;
        ParentInstanceId = parentInstanceId ?? string.Empty;
        LocationPaths = [.. locationPaths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        ReliableSerial = string.IsNullOrWhiteSpace(reliableSerial) ? null : reliableSerial.Trim();
        BootId = bootId ?? string.Empty;
        OwnerToken = ownerToken ?? string.Empty;
    }

    public Guid ContainerId { get; }
    public string ParentInstanceId { get; }
    public IReadOnlyList<string> LocationPaths { get; }
    public string? ReliableSerial { get; }
    public string BootId { get; }
    public string OwnerToken { get; }

    public override string ToString() => "ModuleIdentityEvidence(physical and session secrets redacted)";
}

public sealed class ModuleSessionLease
{
    internal ModuleSessionLease(long generation, Guid sessionId, ModuleIdentityEvidence evidence)
    {
        Generation = generation;
        SessionId = sessionId;
        Evidence = evidence;
    }

    public long Generation { get; }
    public Guid SessionId { get; }
    internal ModuleIdentityEvidence Evidence { get; }
    public override string ToString() => $"ModuleSessionLease(Generation={Generation}, SessionId={SessionId})";
}

/// <summary>Owns identity generations and invalidates late results after re-enumeration or reboot.</summary>
public sealed class ModuleIdentitySession
{
    private readonly object _gate = new();
    private long _generation;
    private ModuleSessionLease? _current;

    public ModuleSessionLease Begin(ModuleIdentityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!HasPhysicalIdentity(evidence) || string.IsNullOrWhiteSpace(evidence.BootId) ||
            string.IsNullOrWhiteSpace(evidence.OwnerToken))
        {
            throw new ArgumentException("Physical identity, boot ID, and owner token must be present.", nameof(evidence));
        }

        lock (_gate)
        {
            _generation = checked(_generation + 1);
            return _current = new ModuleSessionLease(_generation, Guid.NewGuid(), evidence);
        }
    }

    public ModuleIdentityRevalidationStatus Revalidate(
        ModuleSessionLease lease,
        ModuleIdentityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(evidence);
        lock (_gate)
        {
            if (!ReferenceEquals(_current, lease) || lease.Generation != _generation)
            {
                return ModuleIdentityRevalidationStatus.StaleSession;
            }
            if (!HasPhysicalIdentity(evidence))
            {
                InvalidateLocked();
                return ModuleIdentityRevalidationStatus.IdentityIncomplete;
            }
            if (!SamePhysicalIdentity(lease.Evidence, evidence))
            {
                InvalidateLocked();
                return ModuleIdentityRevalidationStatus.PhysicalIdentityChanged;
            }
            if (!string.Equals(lease.Evidence.BootId, evidence.BootId, StringComparison.Ordinal))
            {
                InvalidateLocked();
                return ModuleIdentityRevalidationStatus.BootChanged;
            }
            if (!string.Equals(lease.Evidence.OwnerToken, evidence.OwnerToken, StringComparison.Ordinal))
            {
                InvalidateLocked();
                return ModuleIdentityRevalidationStatus.OwnerChanged;
            }

            return ModuleIdentityRevalidationStatus.Valid;
        }
    }

    public bool IsCurrent(ModuleSessionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            return ReferenceEquals(_current, lease) && lease.Generation == _generation;
        }
    }

    public bool IsCurrentGeneration(long generation)
    {
        lock (_gate)
        {
            return _current is not null && generation == _generation;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            InvalidateLocked();
        }
    }

    private void InvalidateLocked()
    {
        _generation = checked(_generation + 1);
        _current = null;
    }

    private static bool HasPhysicalIdentity(ModuleIdentityEvidence evidence) =>
        evidence.ContainerId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(evidence.ParentInstanceId) &&
        evidence.LocationPaths.Count > 0;

    private static bool SamePhysicalIdentity(ModuleIdentityEvidence left, ModuleIdentityEvidence right) =>
        left.ContainerId == right.ContainerId &&
        string.Equals(left.ParentInstanceId, right.ParentInstanceId, StringComparison.OrdinalIgnoreCase) &&
        left.LocationPaths.ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(right.LocationPaths) &&
        (left.ReliableSerial is null && right.ReliableSerial is null ||
         string.Equals(left.ReliableSerial, right.ReliableSerial, StringComparison.Ordinal));
}

/// <summary>
/// Parses current ADB inventory and issues short-lived transport selections only after a separate
/// physical USB association has identified the module. Transport IDs are invalidated on refresh.
/// </summary>
public sealed class AdbDeviceCatalog
{
    private readonly object _gate = new();
    private ImmutableArray<AdbDeviceCandidate> _devices = [];
    private long _generation;

    public long UpdateFromDevicesOutput(string output, Func<string, long?, string?>? physicalIdentityResolver = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        var candidates = ParseDevicesOutput(output, physicalIdentityResolver);
        lock (_gate)
        {
            _generation = checked(_generation + 1);
            _devices = candidates;
            return _generation;
        }
    }

    public long Update(IEnumerable<AdbDeviceCandidate> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var candidates = MarkDuplicateSerialsUnreliable(devices.ToArray());
        lock (_gate)
        {
            _generation = checked(_generation + 1);
            _devices = candidates;
            return _generation;
        }
    }

    public AdbSelectionResult SelectByPhysicalIdentity(string? physicalIdentityKey)
    {
        if (string.IsNullOrWhiteSpace(physicalIdentityKey))
        {
            return new AdbSelectionResult(AdbSelectionStatus.IdentityUnassociated, null);
        }

        lock (_gate)
        {
            var matches = _devices.Where(device =>
                    string.Equals(device.PhysicalIdentityKey, physicalIdentityKey, StringComparison.Ordinal) &&
                    device.State.Equals("device", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length == 0)
            {
                return new AdbSelectionResult(AdbSelectionStatus.NoMatchingDevice, null);
            }
            if (matches.Length > 1)
            {
                return new AdbSelectionResult(AdbSelectionStatus.Ambiguous, null);
            }

            var device = matches[0];
            if (device.TransportId is not > 0)
            {
                return new AdbSelectionResult(AdbSelectionStatus.TransportUnavailable, null);
            }

            return new AdbSelectionResult(AdbSelectionStatus.Selected,
                new AdbTransportSelection(device.TransportId.Value,
                    device.SerialReliable ? device.Serial : null,
                    device.SerialReliable,
                    physicalIdentityKey,
                    _generation));
        }
    }

    public bool IsCurrent(AdbTransportSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        lock (_gate)
        {
            return selection.CatalogGeneration == _generation &&
                   _devices.Any(device => device.TransportId == selection.TransportId &&
                       device.State.Equals("device", StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(device.PhysicalIdentityKey, selection.PhysicalIdentityKey,
                           StringComparison.Ordinal));
        }
    }

    public static ImmutableArray<AdbDeviceCandidate> ParseDevicesOutput(
        string output,
        Func<string, long?, string?>? physicalIdentityResolver = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        var parsed = new List<AdbDeviceCandidate>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("*", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2)
            {
                continue;
            }

            long? transportId = null;
            foreach (var field in fields.Skip(2))
            {
                if (field.StartsWith("transport_id:", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(field.AsSpan("transport_id:".Length), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var parsedId) && parsedId > 0)
                {
                    transportId = parsedId;
                }
            }

            parsed.Add(new AdbDeviceCandidate(
                fields[0],
                fields[1],
                transportId,
                physicalIdentityResolver?.Invoke(fields[0], transportId),
                IsReliableSerial(fields[0])));
        }

        return MarkDuplicateSerialsUnreliable(parsed);
    }

    private static ImmutableArray<AdbDeviceCandidate> MarkDuplicateSerialsUnreliable(
        IReadOnlyList<AdbDeviceCandidate> devices)
    {
        var duplicated = devices.Where(device => IsReliableSerial(device.Serial))
            .GroupBy(device => device.Serial, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. devices.Select(device => device with
        {
            SerialReliable = IsReliableSerial(device.Serial) && !duplicated.Contains(device.Serial),
        })];
    }

    private static bool IsReliableSerial(string? serial) =>
        !string.IsNullOrWhiteSpace(serial) && serial.Trim() != "?";
}
