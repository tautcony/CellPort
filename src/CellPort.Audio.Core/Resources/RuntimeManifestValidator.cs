using System.Security.Cryptography;
using System.Text.Json;

namespace CellPort.Audio.Core.Resources;

public sealed record RuntimeTargetIdentity(
    string? VendorId,
    string? ProductId,
    string? Model,
    string? FirmwareExact,
    string? KernelRelease,
    string? KernelBuildId,
    string? Vermagic,
    string? ModuleSymversSha256,
    string? Architecture,
    string? Eabi,
    string? FloatAbi,
    string? Loader,
    string? Libc,
    IReadOnlyDictionary<string, string>? Libraries);

public sealed record RuntimeBundleValidationResult(
    bool IsValid,
    bool AudioEnableAllowed,
    string? ProfileId,
    IReadOnlyList<string> Errors);

public sealed record RuntimeBundleImportResult(
    bool Imported,
    string? Destination,
    IReadOnlyList<string> Errors);

/// <summary>Validates a fixed offline resource bundle before any deployment or activation.</summary>
public static class RuntimeManifestValidator
{
    private const long MaxResourceBytes = 512L * 1024L * 1024L;
    private static readonly HashSet<string> TopProperties = Set(
        "schemaVersion", "status", "profileId", "device", "kernel", "abi", "resources",
        "loadOrder", "audio", "calibration", "usb", "helperCapabilities", "evidence");

    public static RuntimeBundleValidationResult Validate(
        string manifestJson,
        string bundleDirectory,
        RuntimeTargetIdentity target,
        bool userSuppliedResources = false)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(manifestJson) || string.IsNullOrWhiteSpace(bundleDirectory))
        {
            return Invalid("MANIFEST_INPUT_INVALID");
        }
        if (target is null) return Invalid("TARGET_IDENTITY_UNKNOWN");

        try
        {
            using var document = JsonDocument.Parse(manifestJson, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid("MANIFEST_MALFORMED");
            }

            CheckProperties(root, TopProperties, errors);
            if (!TryInt32(root, "schemaVersion", out var schemaVersion))
            {
                errors.Add("MANIFEST_VERSION_UNSUPPORTED");
            }
            else if (schemaVersion != 2)
            {
                errors.Add("MANIFEST_VERSION_UNSUPPORTED");
            }

            var status = GetString(root, "status", errors);
            if (status is not ("candidate" or "experimental" or "validated"))
            {
                errors.Add("MANIFEST_STATUS_INVALID");
            }

            var profileId = GetId(root, "profileId", errors);
            var device = RequiredObject(root, "device", errors);
            CheckProperties(device, Set("vendorId", "productId", "model", "firmwareExact"), errors);
            CompareTarget(GetString(device, "vendorId", errors), target.VendorId,
                "TARGET_IDENTITY_UNKNOWN", "TARGET_MISMATCH", errors, ignoreCase: true);
            CompareTarget(GetString(device, "productId", errors), target.ProductId,
                "TARGET_IDENTITY_UNKNOWN", "TARGET_MISMATCH", errors, ignoreCase: true);
            ValidateHexId(device, "vendorId", errors);
            ValidateHexId(device, "productId", errors);
            CompareTarget(GetString(device, "model", errors), target.Model, "TARGET_MODEL_UNKNOWN", "TARGET_MISMATCH", errors);
            CompareTarget(GetString(device, "firmwareExact", errors), target.FirmwareExact,
                "TARGET_FIRMWARE_UNKNOWN", "TARGET_MISMATCH", errors);

            var kernel = RequiredObject(root, "kernel", errors);
            CheckProperties(kernel, Set("release", "buildId", "vermagic", "moduleSymversSha256"), errors);
            CompareTarget(GetString(kernel, "release", errors), target.KernelRelease,
                "TARGET_KERNEL_UNKNOWN", "TARGET_MISMATCH", errors);
            CompareTarget(GetString(kernel, "buildId", errors), target.KernelBuildId,
                "TARGET_KERNEL_UNKNOWN", "TARGET_MISMATCH", errors);
            CompareTarget(GetString(kernel, "vermagic", errors), target.Vermagic,
                "TARGET_ABI_UNKNOWN", "TARGET_MISMATCH", errors);
            CompareTarget(GetString(kernel, "moduleSymversSha256", errors), target.ModuleSymversSha256,
                "TARGET_ABI_UNKNOWN", "TARGET_MISMATCH", errors, ignoreCase: true);
            if (!IsSha256(GetString(kernel, "moduleSymversSha256", errors))) errors.Add("MANIFEST_DIGEST_INVALID");

            var abi = RequiredObject(root, "abi", errors);
            CheckProperties(abi, Set("architecture", "eabi", "floatAbi", "loader", "libc", "libraries"), errors);
            CompareTarget(GetString(abi, "architecture", errors), target.Architecture,
                "TARGET_ABI_UNKNOWN", "TARGET_MISMATCH", errors);
            CompareTarget(GetString(abi, "eabi", errors), target.Eabi,
                "TARGET_ABI_UNKNOWN", "TARGET_MISMATCH", errors);
            CompareTarget(GetString(abi, "floatAbi", errors), target.FloatAbi,
                "TARGET_ABI_UNKNOWN", "TARGET_MISMATCH", errors);
            CompareTarget(GetString(abi, "loader", errors), target.Loader,
                "TARGET_ABI_UNKNOWN", "TARGET_MISMATCH", errors);
            CompareTarget(GetString(abi, "libc", errors), target.Libc,
                "TARGET_ABI_UNKNOWN", "TARGET_MISMATCH", errors);
            ValidateLibraries(abi, target.Libraries, errors);

            var resources = ValidateResources(root, bundleDirectory, userSuppliedResources, errors);
            ValidateLoadOrder(root, resources, errors);
            ValidateAudio(root, errors);
            ValidateCalibration(root, errors);
            ValidateUsb(root, errors);
            var routeOnly = ValidateCapabilities(root, errors);
            var evidenceComplete = ValidateEvidence(root, errors);
            if (status == "validated" && (!evidenceComplete || !routeOnly))
            {
                errors.Add("VALIDATION_EVIDENCE_INCOMPLETE");
            }

            var distinctErrors = errors.Distinct().ToArray();
            var audioEnableAllowed = distinctErrors.Length == 0 && status == "validated" && evidenceComplete && routeOnly;
            return new RuntimeBundleValidationResult(distinctErrors.Length == 0, audioEnableAllowed,
                profileId, distinctErrors);
        }
        catch (JsonException)
        {
            return Invalid("MANIFEST_MALFORMED");
        }
        catch (IOException)
        {
            return Invalid("RESOURCE_IO_ERROR");
        }
        catch (UnauthorizedAccessException)
        {
            return Invalid("RESOURCE_IO_ERROR");
        }
        catch (ArgumentException)
        {
            return Invalid("MANIFEST_MALFORMED");
        }
        catch (InvalidOperationException)
        {
            return Invalid("MANIFEST_MALFORMED");
        }

        RuntimeBundleValidationResult Invalid(string code) => new(false, false, null, [code]);
    }

    public static RuntimeBundleImportResult Import(
        string manifestJson,
        string bundleDirectory,
        string destinationRoot,
        RuntimeTargetIdentity target,
        bool userSuppliedResources = false)
    {
        var validation = Validate(manifestJson, bundleDirectory, target, userSuppliedResources);
        if (!validation.IsValid || string.IsNullOrWhiteSpace(validation.ProfileId))
        {
            return new RuntimeBundleImportResult(false, null, validation.Errors);
        }

        string? stagingDirectory = null;
        try
        {
            var destinationRootFull = Path.GetFullPath(destinationRoot);
            Directory.CreateDirectory(destinationRootFull);
            var finalDirectory = Path.Combine(destinationRootFull, validation.ProfileId);
            if (Directory.Exists(finalDirectory) || File.Exists(finalDirectory))
            {
                return new RuntimeBundleImportResult(false, null, ["IMPORT_DESTINATION_EXISTS"]);
            }

            stagingDirectory = Path.Combine(destinationRootFull,
                $".{validation.ProfileId}.{Guid.NewGuid():N}.part");
            Directory.CreateDirectory(stagingDirectory);
            using var document = JsonDocument.Parse(manifestJson, new JsonDocumentOptions { MaxDepth = 32 });
            var resources = document.RootElement.GetProperty("resources");
            foreach (var resource in resources.EnumerateArray())
            {
                var relativePath = resource.GetProperty("path").GetString()!;
                var sourcePath = ResolveBundlePath(bundleDirectory, relativePath);
                var targetPath = ResolveBundlePath(stagingDirectory, relativePath, allowMissing: true);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                CopyAndVerify(sourcePath, targetPath,
                    resource.GetProperty("size").GetInt64(), resource.GetProperty("sha256").GetString()!);
            }

            var manifestPath = Path.Combine(stagingDirectory, "manifest.v2.json");
            using (var output = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(output, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(manifestJson);
                writer.Flush();
                output.Flush(flushToDisk: true);
            }

            var stagedValidation = Validate(manifestJson, stagingDirectory, target, userSuppliedResources);
            if (!stagedValidation.IsValid)
            {
                return new RuntimeBundleImportResult(false, null, stagedValidation.Errors);
            }
            Directory.Move(stagingDirectory, finalDirectory);
            stagingDirectory = null;
            return new RuntimeBundleImportResult(true, finalDirectory, []);
        }
        catch (IOException)
        {
            return new RuntimeBundleImportResult(false, null, ["IMPORT_IO_ERROR"]);
        }
        catch (UnauthorizedAccessException)
        {
            return new RuntimeBundleImportResult(false, null, ["IMPORT_IO_ERROR"]);
        }
        catch (ArgumentException)
        {
            return new RuntimeBundleImportResult(false, null, ["IMPORT_PATH_INVALID"]);
        }
        finally
        {
            if (stagingDirectory is not null && Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private static List<ResourceRecord> ValidateResources(JsonElement root, string bundleDirectory,
        bool userSuppliedResources, List<string> errors)
    {
        var result = new List<ResourceRecord>();
        if (!root.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array ||
            resources.GetArrayLength() == 0)
        {
            errors.Add("RESOURCE_LIST_INVALID");
            return result;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moduleNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in resources.EnumerateArray())
        {
            if (resource.ValueKind != JsonValueKind.Object)
            {
                errors.Add("RESOURCE_ENTRY_INVALID");
                continue;
            }
            CheckProperties(resource, Set("path", "kind", "moduleName", "size", "sha256", "mode", "source", "licenseId", "distribution"), errors);
            var relativePath = GetString(resource, "path", errors);
            var kind = GetString(resource, "kind", errors);
            var moduleName = OptionalString(resource, "moduleName");
            var digest = GetString(resource, "sha256", errors);
            var sizeValid = TryInt64(resource, "size", out var size) && size is > 0 and <= MaxResourceBytes;
            var modeValid = TryInt32(resource, "mode", out var mode) && mode is >= 0 and <= 511 && (mode & 18) == 0;
            var license = GetString(resource, "licenseId", errors);
            var distribution = GetString(resource, "distribution", errors);
            ValidateSource(resource, errors);

            if (kind is not ("kernelModule" or "helper" or "vendorLibrary")) errors.Add("RESOURCE_KIND_INVALID");
            if (kind == "kernelModule" && !IsId(moduleName)) errors.Add("RESOURCE_MODULE_NAME_INVALID");
            if (moduleName is not null && !moduleNames.Add(moduleName)) errors.Add("RESOURCE_DUPLICATE_MODULE");
            if (relativePath is null || !IsSafeRelativePath(relativePath)) errors.Add("RESOURCE_PATH_INVALID");
            else if (!names.Add(relativePath)) errors.Add("RESOURCE_DUPLICATE_NAME");
            if (!sizeValid || !modeValid || !IsSha256(digest) || string.IsNullOrWhiteSpace(license))
                errors.Add("RESOURCE_METADATA_INVALID");
            if (distribution == "blocked" || distribution == "userProvided" && !userSuppliedResources ||
                distribution is not ("redistributable" or "userProvided" or "blocked"))
                errors.Add("RESOURCE_DISTRIBUTION_BLOCKED");

            if (relativePath is null || !IsSafeRelativePath(relativePath) || !sizeValid || !IsSha256(digest)) continue;
            try
            {
                var fullPath = ResolveBundlePath(bundleDirectory, relativePath);
                var attributes = File.GetAttributes(fullPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) != 0)
                {
                    errors.Add("RESOURCE_PATH_INVALID");
                    continue;
                }
                var info = new FileInfo(fullPath);
                if (info.Length != size)
                {
                    errors.Add("RESOURCE_SIZE_MISMATCH");
                    continue;
                }
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var actualDigest = Convert.ToHexString(SHA256.HashData(stream));
                if (!actualDigest.Equals(digest, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("RESOURCE_HASH_MISMATCH");
                    continue;
                }
                result.Add(new ResourceRecord(relativePath, kind ?? string.Empty, moduleName));
            }
            catch (FileNotFoundException)
            {
                errors.Add("RESOURCE_MISSING");
            }
            catch (DirectoryNotFoundException)
            {
                errors.Add("RESOURCE_MISSING");
            }
        }
        return result;
    }

    private static void ValidateSource(JsonElement resource, List<string> errors)
    {
        if (!resource.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
        {
            errors.Add("RESOURCE_SOURCE_INVALID");
            return;
        }
        CheckProperties(source, Set("repository", "revision", "sourcePath"), errors);
        var repository = GetString(source, "repository", errors);
        var revision = GetString(source, "revision", errors);
        var sourcePath = GetString(source, "sourcePath", errors);
        if (!Uri.TryCreate(repository, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            revision?.Length is not (40 or 64) || revision.Any(c => !Uri.IsHexDigit(c)) ||
            sourcePath is null || !IsSafeRelativePath(sourcePath))
            errors.Add("RESOURCE_SOURCE_INVALID");
    }

    private static void ValidateLoadOrder(JsonElement root, IReadOnlyList<ResourceRecord> resources, List<string> errors)
    {
        if (!root.TryGetProperty("loadOrder", out var loadOrder) || loadOrder.ValueKind != JsonValueKind.Array)
        {
            errors.Add("LOAD_ORDER_INVALID");
            return;
        }
        var modules = resources.Where(x => x.Kind == "kernelModule").Select(x => x.ModuleName!).ToHashSet(StringComparer.Ordinal);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in loadOrder.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !IsId(item.GetString()) ||
                !listed.Add(item.GetString()!) || !modules.Contains(item.GetString()!))
                errors.Add("LOAD_ORDER_INVALID");
        }
        if (!modules.SetEquals(listed)) errors.Add("LOAD_ORDER_INVALID");
    }

    private static void ValidateLibraries(JsonElement abi, IReadOnlyDictionary<string, string>? targetLibraries,
        List<string> errors)
    {
        if (abi.ValueKind != JsonValueKind.Object || !abi.TryGetProperty("libraries", out var libraries) ||
            libraries.ValueKind != JsonValueKind.Array)
        {
            errors.Add("ABI_LIBRARIES_INVALID");
            return;
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries.EnumerateArray())
        {
            if (library.ValueKind != JsonValueKind.Object)
            {
                errors.Add("ABI_LIBRARIES_INVALID");
                continue;
            }
            CheckProperties(library, Set("name", "sha256"), errors);
            var name = GetString(library, "name", errors);
            var digest = GetString(library, "sha256", errors);
            if (!IsId(name) || !names.Add(name!) || !IsSha256(digest))
            {
                errors.Add("ABI_LIBRARIES_INVALID");
                continue;
            }
            found[name!] = digest!;
        }
        if (targetLibraries is null || targetLibraries.Count != found.Count ||
            found.Any(pair => !targetLibraries.TryGetValue(pair.Key, out var expected) ||
                              !pair.Value.Equals(expected, StringComparison.OrdinalIgnoreCase)))
            errors.Add("TARGET_ABI_UNKNOWN");
    }

    private static void ValidateAudio(JsonElement root, List<string> errors)
    {
        var audio = RequiredObject(root, "audio", errors);
        CheckProperties(audio, Set("cardName", "requiredDevices"), errors);
        if (audio.ValueKind != JsonValueKind.Object) return;
        _ = GetId(audio, "cardName", errors);
        if (!audio.TryGetProperty("requiredDevices", out var devices) || devices.ValueKind != JsonValueKind.Array ||
            devices.GetArrayLength() == 0)
        {
            errors.Add("AUDIO_DEVICES_INVALID");
            return;
        }
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in devices.EnumerateArray())
        {
            var value = device.ValueKind == JsonValueKind.String ? device.GetString() : null;
            if (value is null || !value.StartsWith("/dev/", StringComparison.Ordinal) || !unique.Add(value))
                errors.Add("AUDIO_DEVICES_INVALID");
        }
    }

    private static void ValidateCalibration(JsonElement root, List<string> errors)
    {
        var calibration = RequiredObject(root, "calibration", errors);
        CheckProperties(calibration, Set("required", "service", "readinessEvidence"), errors);
        if (calibration.ValueKind != JsonValueKind.Object) return;
        if (!calibration.TryGetProperty("required", out var required) ||
            required.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) errors.Add("CALIBRATION_INVALID");
        _ = GetId(calibration, "service", errors);
        if (string.IsNullOrWhiteSpace(GetString(calibration, "readinessEvidence", errors)))
            errors.Add("CALIBRATION_INVALID");
    }

    private static void ValidateUsb(JsonElement root, List<string> errors)
    {
        var usb = RequiredObject(root, "usb", errors);
        CheckProperties(usb, Set("functionsAllowlist", "transitionOrder"), errors);
        if (usb.ValueKind != JsonValueKind.Object) return;
        if (!usb.TryGetProperty("functionsAllowlist", out var functions) || functions.ValueKind != JsonValueKind.Array ||
            functions.GetArrayLength() == 0 || functions.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || !IsId(x.GetString())))
            errors.Add("USB_PROFILE_INVALID");
        if (!usb.TryGetProperty("transitionOrder", out var order) || order.ValueKind != JsonValueKind.Array ||
            order.GetArrayLength() == 0 || order.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String ||
                x.GetString() is not ("disable" or "setAudioEnable" or "setFunctions" or "setEnable" or "verify")))
            errors.Add("USB_PROFILE_INVALID");
    }

    private static bool ValidateCapabilities(JsonElement root, List<string> errors)
    {
        var capabilities = RequiredObject(root, "helperCapabilities", errors);
        CheckProperties(capabilities, Set("routeOnly", "startWritesFunctions", "startWritesEnable",
            "startWritesAudioEnable", "stopWritesFunctions", "stopWritesEnable", "stopWritesAudioEnable"), errors);
        if (capabilities.ValueKind != JsonValueKind.Object) return false;
        var values = new List<bool>();
        foreach (var key in new[] { "routeOnly", "startWritesFunctions", "startWritesEnable", "startWritesAudioEnable",
                     "stopWritesFunctions", "stopWritesEnable", "stopWritesAudioEnable" })
        {
            if (capabilities.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                values.Add(value.GetBoolean());
            else
            {
                errors.Add("HELPER_CAPABILITIES_INVALID");
                values.Add(false);
            }
        }
        var routeOnly = values[0];
        if (routeOnly && values.Skip(1).Any(x => x)) errors.Add("HELPER_USB_SIDE_EFFECT");
        return routeOnly && values.Skip(1).All(x => !x);
    }

    private static bool ValidateEvidence(JsonElement root, List<string> errors)
    {
        var evidence = RequiredObject(root, "evidence", errors);
        CheckProperties(evidence, Set("sourceAudit", "abiAudit", "routeAudit", "calibration", "hardware"), errors);
        if (evidence.ValueKind != JsonValueKind.Object) return false;
        var complete = true;
        foreach (var key in new[] { "sourceAudit", "abiAudit", "routeAudit", "calibration", "hardware" })
        {
            if (!evidence.TryGetProperty(key, out var value) ||
                value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                errors.Add("MANIFEST_FIELD_INVALID");
                complete = false;
            }
            else if (value.ValueKind != JsonValueKind.True)
            {
                complete = false;
            }
        }
        return complete;
    }

    private static void CopyAndVerify(string source, string destination, long expectedSize, string expectedDigest)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Reparse resource rejected.");
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            total += read;
            if (total > expectedSize) throw new IOException("Resource size changed during import.");
            output.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
        }
        output.Flush(flushToDisk: true);
        if (total != expectedSize || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedDigest, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Resource changed during import.");
    }

    private static string ResolveBundlePath(string root, string relativePath, bool allowMissing = false)
    {
        if (!IsSafeRelativePath(relativePath)) throw new ArgumentException("Unsafe relative path.");
        var rootFull = Path.GetFullPath(root);
        if (Directory.Exists(rootFull) && (File.GetAttributes(rootFull) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Reparse bundle root rejected.");
        var full = Path.GetFullPath(Path.Combine(rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Path escaped root.");

        var relative = Path.GetRelativePath(rootFull, full);
        var current = rootFull;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                if (allowMissing) continue;
                throw new FileNotFoundException("Bundle resource missing.", current);
            }
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse path rejected.");
        }
        return full;
    }

    private static bool IsSafeRelativePath(string value) =>
        !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value) && !value.Contains('\\') &&
        !value.Contains(':') && value.Split('/').All(x => x.Length > 0 && x is not ("." or ".."));

    private static void CompareTarget(string? actual, string? expected, string unknownCode, string mismatchCode,
        List<string> errors, bool ignoreCase = false)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected) || expected.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(unknownCode);
            return;
        }
        if (!string.Equals(actual, expected, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            errors.Add(mismatchCode);
    }

    private static JsonElement RequiredObject(JsonElement parent, string name, List<string> errors)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
            return value;
        errors.Add("MANIFEST_FIELD_INVALID");
        return default;
    }

    private static string? GetId(JsonElement parent, string name, List<string> errors)
    {
        var value = GetString(parent, name, errors);
        if (!IsId(value)) errors.Add("MANIFEST_FIELD_INVALID");
        return value;
    }

    private static string? GetString(JsonElement parent, string name, List<string> errors)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        errors.Add("MANIFEST_FIELD_INVALID");
        return null;
    }

    private static string? OptionalString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryInt32(JsonElement parent, string name, out int result)
    {
        result = 0;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.TryGetInt32(out result);
    }

    private static bool TryInt64(JsonElement parent, string name, out long result)
    {
        result = 0;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.TryGetInt64(out result);
    }

    private static void ValidateHexId(JsonElement parent, string name, List<string> errors)
    {
        var value = GetString(parent, name, errors);
        if (value is null || value.Length != 4 || value.Any(c => !Uri.IsHexDigit(c))) errors.Add("DEVICE_ID_INVALID");
    }

    private static bool IsId(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-');

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void CheckProperties(JsonElement value, HashSet<string> allowed, List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name)) errors.Add("MANIFEST_UNKNOWN_FIELD");
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);

    private sealed record ResourceRecord(string Path, string Kind, string? ModuleName);
}
