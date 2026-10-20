using System.Text.Json;
using CellPort.Core.Services;
using Xunit;

namespace CellPort.Core.Tests;

public sealed class AudioDiagnosticExporterTests
{
    [Fact]
    public void ExportOmitsPcmPhoneSerialBootAndOwnerSecrets()
    {
        var evidence = new AudioDiagnosticEvidence
        {
            HostOs = "Windows 10.0",
            CpuArchitecture = "x64",
            DeviceAvailable = true,
            DeviceModel = "QDC507",
            FirmwareRevision = "QDC507GLEFM21",
            ModuleBuild = "build-2026",
            AdbToolVersion = "35.0.1-11580240",
            VendorLibrarySha256 = new Dictionary<string, string?>
            {
                ["libql_lib_audio.so.1.0.0"] = new('a', 64),
                ["invalid/path"] = new('b', 64),
                ["unknown.so"] = "not-a-hash",
            },
            GetPropStatus = DiagnosticMetadataStatus.Unavailable,
            OsReleaseStatus = DiagnosticMetadataStatus.Unavailable,
            DeviceSerial = "SERIAL-SECRET-001",
            Imei = "IMEI-SECRET-111111111111111",
            PhoneNumber = "+8613800138000",
            BootId = "BOOT-SECRET-22222222",
            OwnerToken = "OWNER-SECRET-33333333",
            Pcm = [1, 2, 3, 4],
        };

        var json = AudioDiagnosticExporter.Serialize(evidence);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.GetProperty("module").GetProperty("available").GetBoolean());
        Assert.Equal("Unavailable", root.GetProperty("optionalMetadata").GetProperty("getProp").GetString());
        Assert.Equal("Unavailable", root.GetProperty("optionalMetadata").GetProperty("osRelease").GetString());
        Assert.Single(root.GetProperty("vendorLibraries").EnumerateObject());
        Assert.Contains("35.0.1-11580240", json);
        Assert.DoesNotContain("SERIAL-SECRET-001", json);
        Assert.DoesNotContain("IMEI-SECRET", json);
        Assert.DoesNotContain("13800138000", json);
        Assert.DoesNotContain("BOOT-SECRET", json);
        Assert.DoesNotContain("OWNER-SECRET", json);
        Assert.DoesNotContain("pcm", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invalid/path", json);
        Assert.DoesNotContain("not-a-hash", json);
    }

    [Fact]
    public void UnavailableOptionalMetadataDoesNotMarkConnectedDeviceUnavailable()
    {
        var evidence = new AudioDiagnosticEvidence
        {
            HostOs = "Linux",
            CpuArchitecture = "x86_64",
            DeviceAvailable = true,
            GetPropStatus = DiagnosticMetadataStatus.Unavailable,
            OsReleaseStatus = DiagnosticMetadataStatus.Unavailable,
        };

        using var document = JsonDocument.Parse(AudioDiagnosticExporter.Serialize(evidence));

        Assert.True(document.RootElement.GetProperty("module").GetProperty("available").GetBoolean());
    }
}
