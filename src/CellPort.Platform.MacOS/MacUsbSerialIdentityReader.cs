using System.Runtime.InteropServices;

namespace CellPort.Platform.MacOS;

/// <summary>Associates macOS serial callout paths with USB identity by walking IORegistry parents.</summary>
internal static class MacUsbSerialIdentityReader
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8Encoding = 0x08000100;
    private const int SInt32NumberType = 3;
    private const int SInt64NumberType = 4;

    internal static IReadOnlyDictionary<string, MacUsbSerialIdentity> GetPortIdentities()
    {
        var result = new Dictionary<string, MacUsbSerialIdentity>(StringComparer.Ordinal);
        if (!OperatingSystem.IsMacOS()) return result;

        try { ReadRegistry(result); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (BadImageFormatException) { }
        catch (MarshalDirectiveException) { }
        return result;
    }

    internal static IReadOnlyList<string> GetCalloutPorts()
    {
        var result = new List<string>();
        if (!OperatingSystem.IsMacOS()) return result;

        try { ReadRegistry(result, null); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (BadImageFormatException) { }
        catch (MarshalDirectiveException) { }
        return result;
    }

    /// <summary>Compact USB identities for diagnostics, including devices with no IOSerialBSDClient callout.</summary>
    internal static IReadOnlyList<MacUsbDeviceIdentity> GetUsbDeviceIdentities()
    {
        var result = new List<MacUsbDeviceIdentity>();
        if (!OperatingSystem.IsMacOS()) return result;
        var matching = IOServiceMatching("IOUSBHostDevice");
        if (matching == IntPtr.Zero) return result;
        if (IOServiceGetMatchingServices(0, matching, out var iterator) != 0 || iterator == 0) return result;
        try
        {
            while (IOIteratorNext(iterator) is var entry && entry != 0)
            {
                try
                {
                    var vendor = GetNumberProperty(entry, "idVendor");
                    var product = GetNumberProperty(entry, "idProduct");
                    var location = GetUnsignedNumberProperty(entry, "locationID");
                    if (vendor is not null && product is not null)
                        result.Add(new MacUsbDeviceIdentity(vendor.Value, product.Value, location));
                }
                finally { IOObjectRelease(entry); }
            }
        }
        finally { IOObjectRelease(iterator); }
        return result.Distinct().ToArray();
    }

    private static void ReadRegistry(Dictionary<string, MacUsbSerialIdentity> result)
        => ReadRegistry(null, result);

    private static void ReadRegistry(List<string>? calloutPorts, Dictionary<string, MacUsbSerialIdentity>? result)
    {
        var matching = IOServiceMatching("IOSerialBSDClient");
        if (matching == IntPtr.Zero) return;

        var status = IOServiceGetMatchingServices(0, matching, out var iterator);
        if (status != 0 || iterator == 0) return;

        try
        {
            while (IOIteratorNext(iterator) is var entry && entry != 0)
            {
                try
                {
                    var calloutDevice = GetStringProperty(entry, "IOCalloutDevice");
                    if (string.IsNullOrEmpty(calloutDevice) || !calloutDevice.StartsWith("/dev/cu.", StringComparison.Ordinal)) continue;
                    calloutPorts?.Add(calloutDevice);
                    if (result is not null && TryReadUsbIdentity(entry, out var identity)) result[calloutDevice] = identity;
                }
                finally { IOObjectRelease(entry); }
            }
        }
        finally { IOObjectRelease(iterator); }
    }

    private static bool TryReadUsbIdentity(uint serialService, out MacUsbSerialIdentity identity)
    {
        int? vendorId = null;
        int? productId = null;
        int? interfaceNumber = null;
        var node = serialService;
        var ownsNode = false;

        try
        {
            for (var depth = 0; depth < 32; depth++)
            {
                vendorId ??= GetNumberProperty(node, "idVendor");
                productId ??= GetNumberProperty(node, "idProduct");
                interfaceNumber ??= GetNumberProperty(node, "bInterfaceNumber")
                    ?? GetNumberProperty(node, "USB Interface Number");

                if (vendorId is not null && productId is not null) break;
                if (IORegistryEntryGetParentEntry(node, "IOService", out var parent) != 0 || parent == 0) break;
                if (ownsNode) IOObjectRelease(node);
                node = parent;
                ownsNode = true;
            }
        }
        finally
        {
            if (ownsNode) IOObjectRelease(node);
        }

        if (vendorId is not null && productId is not null)
        {
            identity = new MacUsbSerialIdentity(vendorId.Value, productId.Value, interfaceNumber);
            return true;
        }

        identity = default;
        return false;
    }

    private static string? GetStringProperty(uint entry, string name)
    {
        var property = CreateProperty(entry, name);
        if (property == IntPtr.Zero) return null;
        try
        {
            var buffer = new byte[1024];
            if (!CFStringGetCString(property, buffer, buffer.Length, Utf8Encoding)) return null;
            var terminator = Array.IndexOf(buffer, (byte)0);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, terminator < 0 ? buffer.Length : terminator);
        }
        finally { CFRelease(property); }
    }

    private static int? GetNumberProperty(uint entry, string name)
    {
        var property = CreateProperty(entry, name);
        if (property == IntPtr.Zero) return null;
        try { return CFNumberGetValue(property, SInt32NumberType, out int value) ? value : null; }
        finally { CFRelease(property); }
    }

    private static uint? GetUnsignedNumberProperty(uint entry, string name)
    {
        var property = CreateProperty(entry, name);
        if (property == IntPtr.Zero) return null;
        try
        {
            return CFNumberGetValue(property, SInt64NumberType, out long value) && value is >= 0 and <= uint.MaxValue
                ? (uint)value : null;
        }
        finally { CFRelease(property); }
    }

    private static IntPtr CreateProperty(uint entry, string name)
    {
        var key = CFStringCreateWithCString(IntPtr.Zero, name, Utf8Encoding);
        if (key == IntPtr.Zero) return IntPtr.Zero;
        try { return IORegistryEntryCreateCFProperty(entry, key, IntPtr.Zero, 0); }
        finally { CFRelease(key); }
    }

    [DllImport(IOKit, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr IOServiceMatching([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(IOKit, CallingConvention = CallingConvention.Cdecl)]
    private static extern int IOServiceGetMatchingServices(uint mainPort, IntPtr matching, out uint iterator);

    [DllImport(IOKit, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint IOIteratorNext(uint iterator);

    [DllImport(IOKit, CallingConvention = CallingConvention.Cdecl)]
    private static extern int IORegistryEntryGetParentEntry(uint entry, [MarshalAs(UnmanagedType.LPUTF8Str)] string plane, out uint parent);

    [DllImport(IOKit, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);

    [DllImport(IOKit, CallingConvention = CallingConvention.Cdecl)]
    private static extern int IOObjectRelease(uint value);

    [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

    [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(IntPtr value, byte[] buffer, nint bufferSize, uint encoding);

    [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFNumberGetValue(IntPtr value, int numberType, out int number);

    [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFNumberGetValue(IntPtr value, int numberType, out long number);

    [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
    private static extern void CFRelease(IntPtr value);
}
