using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CellPort.Audio.Core.Resources;
using Xunit;

namespace CellPort.Audio.Core.Tests;

public sealed class RuntimeManifestValidatorTests
{
    [Fact]
    public void ValidSyntheticBundleIsRecognizedAsCandidateAndImportedAtomically()
    {
        using var fixture = new SyntheticBundle();

        var validation = RuntimeManifestValidator.Validate(fixture.Manifest, fixture.BundlePath, fixture.Target);
        Assert.True(validation.IsValid, string.Join(", ", validation.Errors));
        Assert.False(validation.AudioEnableAllowed);
        Assert.Equal("synthetic-qdc507", validation.ProfileId);
        Assert.False(RuntimeAudioAvailability.Evaluate(validation, hostBackendAvailable: true).IsAvailable);

        var importRoot = Path.Combine(fixture.Root, "imported");
        var imported = RuntimeManifestValidator.Import(fixture.Manifest, fixture.BundlePath, importRoot, fixture.Target);
        Assert.True(imported.Imported, string.Join(", ", imported.Errors));
        Assert.True(File.Exists(Path.Combine(imported.Destination!, "runtime", "helper.bin")));
        Assert.True(File.Exists(Path.Combine(imported.Destination!, "runtime", "driver.ko")));
        Assert.True(File.Exists(Path.Combine(imported.Destination!, "manifest.v2.json")));
        Assert.DoesNotContain(Directory.EnumerateDirectories(importRoot),
            path => path.EndsWith(".part", StringComparison.Ordinal));

        var duplicateImport = RuntimeManifestValidator.Import(fixture.Manifest, fixture.BundlePath, importRoot, fixture.Target);
        Assert.False(duplicateImport.Imported);
        Assert.Contains("IMPORT_DESTINATION_EXISTS", duplicateImport.Errors);
    }

    [Fact]
    public void V1ManifestAndUnknownOrMismatchedTargetNeverPass()
    {
        using var fixture = new SyntheticBundle();
        var oldManifest = JsonNode.Parse(fixture.Manifest)!;
        oldManifest["schemaVersion"] = 1;
        var oldResult = RuntimeManifestValidator.Validate(oldManifest.ToJsonString(), fixture.BundlePath, fixture.Target);
        Assert.False(oldResult.IsValid);
        Assert.Contains("MANIFEST_VERSION_UNSUPPORTED", oldResult.Errors);

        var experimental = JsonNode.Parse(fixture.Manifest)!;
        experimental["status"] = "experimental";
        var experimentalResult = RuntimeManifestValidator.Validate(experimental.ToJsonString(),
            fixture.BundlePath, fixture.Target);
        Assert.True(experimentalResult.IsValid, string.Join(", ", experimentalResult.Errors));

        var mismatch = fixture.Target with { FirmwareExact = "different-firmware" };
        var mismatchResult = RuntimeManifestValidator.Validate(fixture.Manifest, fixture.BundlePath, mismatch);
        Assert.False(mismatchResult.IsValid);
        Assert.Contains("TARGET_MISMATCH", mismatchResult.Errors);

        var unknownAbi = fixture.Target with { Eabi = null };
        var unknownResult = RuntimeManifestValidator.Validate(fixture.Manifest, fixture.BundlePath, unknownAbi);
        Assert.False(unknownResult.IsValid);
        Assert.Contains("TARGET_ABI_UNKNOWN", unknownResult.Errors);
    }

    [Fact]
    public void DigestPartialFileDuplicateSourceAndUnsafePathsAreRejected()
    {
        using var fixture = new SyntheticBundle();
        var badDigest = JsonNode.Parse(fixture.Manifest)!;
        badDigest["resources"]![0]!["sha256"] = new string('0', 64);
        var digestResult = RuntimeManifestValidator.Validate(badDigest.ToJsonString(), fixture.BundlePath, fixture.Target);
        Assert.False(digestResult.IsValid);
        Assert.Contains("RESOURCE_HASH_MISMATCH", digestResult.Errors);

        File.Delete(Path.Combine(fixture.BundlePath, "runtime", "driver.ko"));
        var partialResult = RuntimeManifestValidator.Validate(fixture.Manifest, fixture.BundlePath, fixture.Target);
        Assert.False(partialResult.IsValid);
        Assert.Contains("RESOURCE_MISSING", partialResult.Errors);

        fixture.RecreateDriver();
        File.WriteAllText(Path.Combine(fixture.BundlePath, "runtime", "driver.ko"), "partial");
        var truncatedResult = RuntimeManifestValidator.Validate(fixture.Manifest, fixture.BundlePath, fixture.Target);
        Assert.False(truncatedResult.IsValid);
        Assert.Contains("RESOURCE_SIZE_MISMATCH", truncatedResult.Errors);

        fixture.RecreateDriver();
        var duplicatePath = JsonNode.Parse(fixture.Manifest)!;
        var original = duplicatePath["resources"]![0]!.DeepClone();
        original!["source"]!["revision"] = new string('2', 40);
        duplicatePath["resources"]!.AsArray().Add(original);
        var duplicateResult = RuntimeManifestValidator.Validate(duplicatePath.ToJsonString(), fixture.BundlePath, fixture.Target);
        Assert.False(duplicateResult.IsValid);
        Assert.Contains("RESOURCE_DUPLICATE_NAME", duplicateResult.Errors);

        var traversal = JsonNode.Parse(fixture.Manifest)!;
        traversal["resources"]![0]!["path"] = "../outside.bin";
        var traversalResult = RuntimeManifestValidator.Validate(traversal.ToJsonString(), fixture.BundlePath, fixture.Target);
        Assert.False(traversalResult.IsValid);
        Assert.Contains("RESOURCE_PATH_INVALID", traversalResult.Errors);
    }

    [Fact]
    public void UserProvidedResourceNeedsExplicitOptInAndValidatedStateNeedsEvidence()
    {
        using var fixture = new SyntheticBundle(distribution: "userProvided");
        var withoutConsent = RuntimeManifestValidator.Validate(fixture.Manifest, fixture.BundlePath, fixture.Target);
        Assert.False(withoutConsent.IsValid);
        Assert.Contains("RESOURCE_DISTRIBUTION_BLOCKED", withoutConsent.Errors);

        var withConsent = RuntimeManifestValidator.Validate(fixture.Manifest, fixture.BundlePath, fixture.Target,
            userSuppliedResources: true);
        Assert.True(withConsent.IsValid, string.Join(", ", withConsent.Errors));

        var validated = JsonNode.Parse(fixture.Manifest)!;
        validated["status"] = "validated";
        var noEvidence = RuntimeManifestValidator.Validate(validated.ToJsonString(), fixture.BundlePath,
            fixture.Target, userSuppliedResources: true);
        Assert.False(noEvidence.IsValid);
        Assert.Contains("VALIDATION_EVIDENCE_INCOMPLETE", noEvidence.Errors);
        Assert.False(noEvidence.AudioEnableAllowed);

        var completeEvidence = JsonNode.Parse(fixture.Manifest)!;
        completeEvidence["status"] = "validated";
        completeEvidence["evidence"]!["sourceAudit"] = true;
        completeEvidence["evidence"]!["abiAudit"] = true;
        completeEvidence["evidence"]!["routeAudit"] = true;
        completeEvidence["evidence"]!["calibration"] = true;
        completeEvidence["evidence"]!["hardware"] = true;
        var completeResult = RuntimeManifestValidator.Validate(completeEvidence.ToJsonString(), fixture.BundlePath,
            fixture.Target, userSuppliedResources: true);
        Assert.True(completeResult.IsValid, string.Join(", ", completeResult.Errors));
        Assert.True(completeResult.AudioEnableAllowed);
        Assert.False(RuntimeAudioAvailability.Evaluate(completeResult, hostBackendAvailable: false).IsAvailable);
        Assert.True(RuntimeAudioAvailability.Evaluate(completeResult, hostBackendAvailable: true).IsAvailable);
        Assert.False(RuntimeAudioAvailability.Evaluate(profile: null, hostBackendAvailable: false).IsAvailable);

        var unsafeHelper = JsonNode.Parse(fixture.Manifest)!;
        unsafeHelper["helperCapabilities"]!["startWritesAudioEnable"] = true;
        var helperResult = RuntimeManifestValidator.Validate(unsafeHelper.ToJsonString(), fixture.BundlePath,
            fixture.Target, userSuppliedResources: true);
        Assert.False(helperResult.IsValid);
        Assert.Contains("HELPER_USB_SIDE_EFFECT", helperResult.Errors);
    }

    [Fact]
    public void UnknownFieldsAndMalformedJsonAreRejected()
    {
        using var fixture = new SyntheticBundle();
        var unknown = JsonNode.Parse(fixture.Manifest)!;
        unknown["shell"] = "should-not-be-accepted";
        var unknownResult = RuntimeManifestValidator.Validate(unknown.ToJsonString(), fixture.BundlePath, fixture.Target);
        Assert.False(unknownResult.IsValid);
        Assert.Contains("MANIFEST_UNKNOWN_FIELD", unknownResult.Errors);

        var malformed = RuntimeManifestValidator.Validate("{", fixture.BundlePath, fixture.Target);
        Assert.False(malformed.IsValid);
        Assert.Contains("MANIFEST_MALFORMED", malformed.Errors);
    }

    private sealed class SyntheticBundle : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly byte[] _helperBytes = [.. "synthetic route helper"u8];
        private readonly byte[] _driverBytes = [.. "synthetic module bytes"u8];

        public SyntheticBundle(string distribution = "redistributable")
        {
            Root = Directory.CreateTempSubdirectory("cellport-audio-manifest-").FullName;
            BundlePath = Path.Combine(Root, "bundle");
            Directory.CreateDirectory(Path.Combine(BundlePath, "runtime"));
            File.WriteAllBytes(Path.Combine(BundlePath, "runtime", "helper.bin"), _helperBytes);
            RecreateDriver();
            var helperDigest = Convert.ToHexString(SHA256.HashData(_helperBytes));
            var driverDigest = Convert.ToHexString(SHA256.HashData(_driverBytes));
            var libraryDigest = new string('a', 64);
            var manifest = new
            {
                schemaVersion = 2,
                status = "candidate",
                profileId = "synthetic-qdc507",
                device = new { vendorId = "2CA3", productId = "4006", model = "QDC507", firmwareExact = "QDC507-test-build" },
                kernel = new
                {
                    release = "3.18.44-test",
                    buildId = "kernel-build-test",
                    vermagic = "3.18.44-test ARMv7",
                    moduleSymversSha256 = new string('b', 64)
                },
                abi = new
                {
                    architecture = "armv7",
                    eabi = "EABI5",
                    floatAbi = "softfp",
                    loader = "/system/bin/linker",
                    libc = "bionic",
                    libraries = new[] { new { name = "libaudio.so.1", sha256 = libraryDigest } }
                },
                resources = new object[]
                {
                    new
                    {
                        path = "runtime/helper.bin", kind = "helper", size = (long)_helperBytes.Length,
                        sha256 = helperDigest, mode = 493,
                        source = new { repository = "https://example.invalid/runtime", revision = new string('1', 40), sourcePath = "src/helper.c" },
                        licenseId = "MIT", distribution
                    },
                    new
                    {
                        path = "runtime/driver.ko", kind = "kernelModule", moduleName = "qdc507_test",
                        size = (long)_driverBytes.Length, sha256 = driverDigest, mode = 420,
                        source = new { repository = "https://example.invalid/kernel", revision = new string('2', 40), sourcePath = "drivers/test.c" },
                        licenseId = "GPL-2.0-only", distribution
                    }
                },
                loadOrder = new[] { "qdc507_test" },
                audio = new { cardName = "test-card", requiredDevices = new[] { "/dev/snd/pcmC0D0p" } },
                calibration = new { required = true, service = "test-calibration", readinessEvidence = "explicit synthetic ready fixture" },
                usb = new { functionsAllowlist = new[] { "diag", "serial" }, transitionOrder = new[] { "disable", "setFunctions", "setEnable", "verify" } },
                helperCapabilities = new
                {
                    routeOnly = true,
                    startWritesFunctions = false,
                    startWritesEnable = false,
                    startWritesAudioEnable = false,
                    stopWritesFunctions = false,
                    stopWritesEnable = false,
                    stopWritesAudioEnable = false
                },
                evidence = new { sourceAudit = false, abiAudit = false, routeAudit = false, calibration = false, hardware = false }
            };
            Manifest = JsonSerializer.Serialize(manifest, JsonOptions);
            Target = new RuntimeTargetIdentity("2CA3", "4006", "QDC507", "QDC507-test-build", "3.18.44-test",
                "kernel-build-test", "3.18.44-test ARMv7", new string('b', 64), "armv7", "EABI5", "softfp",
                "/system/bin/linker", "bionic", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["libaudio.so.1"] = libraryDigest
                });
        }

        public string Root { get; }
        public string BundlePath { get; }
        public string Manifest { get; }
        public RuntimeTargetIdentity Target { get; }

        public void RecreateDriver() => File.WriteAllBytes(Path.Combine(BundlePath, "runtime", "driver.ko"), _driverBytes);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
