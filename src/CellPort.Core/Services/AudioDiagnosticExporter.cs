using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CellPort.Core.Services;

public enum DiagnosticMetadataStatus
{
    Available,
    Unavailable,
    Failed,
    NotAttempted,
}

/// <summary>Collected diagnostic evidence. Sensitive fields are accepted only to make their exclusion explicit.</summary>
public sealed record AudioDiagnosticEvidence
{
    public required string HostOs { get; init; }
    public required string CpuArchitecture { get; init; }
    public required bool DeviceAvailable { get; init; }
    public string? DeviceModel { get; init; }
    public string? FirmwareRevision { get; init; }
    public string? ModuleBuild { get; init; }
    public string? AdbToolVersion { get; init; }
    public IReadOnlyDictionary<string, string?> VendorLibrarySha256 { get; init; } =
        new Dictionary<string, string?>();
    public DiagnosticMetadataStatus GetPropStatus { get; init; }
    public DiagnosticMetadataStatus OsReleaseStatus { get; init; }
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? DeviceSerial { get; init; }
    public string? Imei { get; init; }
    public string? PhoneNumber { get; init; }
    public string? BootId { get; init; }
    public string? OwnerToken { get; init; }
    public byte[]? Pcm { get; init; }

    public override string ToString() => "AudioDiagnosticEvidence(sensitive fields redacted)";
}

/// <summary>Exports only an explicit allowlist of redacted diagnostic fields.</summary>
public static partial class AudioDiagnosticExporter
{
    private static readonly Regex SensitiveDigitSequence = new(
        @"\+?\d(?:[\s().-]*\d){6,}", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SafeFingerprintName = new(
        @"^[A-Za-z0-9._-]{1,80}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Sha256Pattern = new(
        @"^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SafeVersionPattern = new(
        @"^[0-9]+(?:\.[0-9]+){1,3}(?:-[A-Za-z0-9._-]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Serialize(AudioDiagnosticEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var libraries = evidence.VendorLibrarySha256
            .Where(pair => SafeFingerprintName.IsMatch(pair.Key) && pair.Value is not null &&
                           Sha256Pattern.IsMatch(pair.Value))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value!.ToLowerInvariant(), StringComparer.Ordinal);

        var document = new SafeDiagnosticDocument(
            SchemaVersion: 1,
            CapturedAtUtc: evidence.CapturedAtUtc,
            Host: new SafeHost(
                RedactRequired(evidence.HostOs),
                RedactRequired(evidence.CpuArchitecture)),
            Module: new SafeModule(
                evidence.DeviceAvailable,
                Redact(evidence.DeviceModel),
                Redact(evidence.FirmwareRevision),
                Redact(evidence.ModuleBuild)),
            Tools: new SafeTools(SafeVersionPattern.IsMatch(evidence.AdbToolVersion ?? string.Empty) &&
                                 evidence.AdbToolVersion!.Length <= 80
                ? evidence.AdbToolVersion
                : Redact(evidence.AdbToolVersion)),
            VendorLibraries: libraries,
            OptionalMetadata: new SafeOptionalMetadata(evidence.GetPropStatus, evidence.OsReleaseStatus));

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Serialize(document, options);
    }

    private static string RedactRequired(string value) =>
        SensitiveDigitSequence.Replace(value, "[redacted]").Trim();

    private static string? Redact(string? value) => value is null
        ? null
        : SensitiveDigitSequence.Replace(value, "[redacted]").Trim();

    private sealed record SafeDiagnosticDocument(
        int SchemaVersion,
        DateTimeOffset CapturedAtUtc,
        SafeHost Host,
        SafeModule Module,
        SafeTools Tools,
        IReadOnlyDictionary<string, string> VendorLibraries,
        SafeOptionalMetadata OptionalMetadata);

    private sealed record SafeHost(string Os, string CpuArchitecture);
    private sealed record SafeModule(bool Available, string? Model, string? FirmwareRevision, string? Build);
    private sealed record SafeTools(string? AdbVersion);
    private sealed record SafeOptionalMetadata(
        DiagnosticMetadataStatus GetProp,
        DiagnosticMetadataStatus OsRelease);
}
