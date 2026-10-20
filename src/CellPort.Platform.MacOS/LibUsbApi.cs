using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace CellPort.Platform.MacOS;

internal readonly record struct LibUsbDevice(nint NativeDevice, int VendorId, int ProductId, uint? LocationId);
internal readonly record struct LibUsbEndpoint(byte Address, byte Attributes);
internal readonly record struct LibUsbAltSetting(int InterfaceNumber, int AlternateSetting, IReadOnlyList<LibUsbEndpoint> Endpoints);

internal interface ILibUsbDeviceList : IDisposable
{
    IReadOnlyList<LibUsbDevice> Devices { get; }
}

internal interface ILibUsbApi
{
    int TimeoutErrorCode { get; }
    nint Init();
    ILibUsbDeviceList GetDeviceList(nint context);
    int Open(LibUsbDevice device, out nint handle);
    IReadOnlyList<LibUsbAltSetting> GetActiveConfiguration(nint handle);
    int ClaimInterface(nint handle, int interfaceNumber);
    int SetAlternateSetting(nint handle, int interfaceNumber, int alternateSetting);
    int ReleaseInterface(nint handle, int interfaceNumber);
    int BulkTransfer(nint handle, byte endpoint, byte[] buffer, int length, out int transferred, uint timeoutMs);
    void Close(nint handle);
    void Exit(nint context);
    string ErrorName(int errorCode);
}

/// <summary>Thin libusb binding. Native loading prefers the copy staged beside the app.</summary>
internal sealed class NativeLibUsbApi : ILibUsbApi
{
    private const string ImportName = "libusb-1.0";
    private static int _resolverInstalled;

    internal NativeLibUsbApi() => EnsureResolver();

    public int TimeoutErrorCode => -7;
    public nint Init() => Native.libusb_init(out var context) == 0 ? context : 0;

    public ILibUsbDeviceList GetDeviceList(nint context)
    {
        var count = Native.libusb_get_device_list(context, out var list);
        if (count < 0) throw new IOException("libusb device enumeration failed: " + ErrorName((int)count));
        try
        {
            var devices = new List<LibUsbDevice>();
            for (nint i = 0; i < count; i++)
            {
                var device = Marshal.ReadIntPtr(list, checked((int)i * IntPtr.Size));
                if (device == 0 || Native.libusb_get_device_descriptor(device, out var descriptor) != 0) continue;
                var location = GetLocation(device);
                devices.Add(new LibUsbDevice(device, descriptor.idVendor, descriptor.idProduct, location));
            }
            return new DeviceList(list, devices);
        }
        catch { Native.libusb_free_device_list(list, 1); throw; }
    }

    public int Open(LibUsbDevice device, out nint handle)
    {
        var result = Native.libusb_open(device.NativeDevice, out handle);
        if (result == 0) _openedDevices[handle] = device.NativeDevice;
        return result;
    }

    public IReadOnlyList<LibUsbAltSetting> GetActiveConfiguration(nint handle)
    {
        var result = new List<LibUsbAltSetting>();
        if (Native.libusb_get_active_config_descriptor(GetOpenedDevice(handle), out var config) != 0 || config == 0) return result;
        try
        {
            var descriptor = Marshal.PtrToStructure<ConfigDescriptor>(config);
            var interfaces = descriptor.interface_array;
            for (var i = 0; i < descriptor.bNumInterfaces; i++)
            {
                var ifaceOffset = checked(i * Marshal.SizeOf<InterfaceDescriptor>());
                var iface = Marshal.PtrToStructure<InterfaceDescriptor>(IntPtr.Add(interfaces, ifaceOffset));
                for (var a = 0; a < iface.num_altsetting; a++)
                {
                    var alt = Marshal.PtrToStructure<AltSettingDescriptor>(IntPtr.Add(iface.altsetting, a * Marshal.SizeOf<AltSettingDescriptor>()));
                    var endpoints = new List<LibUsbEndpoint>();
                    for (var e = 0; e < alt.bNumEndpoints; e++)
                    {
                        var ep = Marshal.PtrToStructure<EndpointDescriptor>(IntPtr.Add(alt.endpoint, e * Marshal.SizeOf<EndpointDescriptor>()));
                        endpoints.Add(new LibUsbEndpoint(ep.bEndpointAddress, ep.bmAttributes));
                    }
                    result.Add(new LibUsbAltSetting(alt.bInterfaceNumber, alt.bAlternateSetting, endpoints));
                }
            }
            return result;
        }
        finally { Native.libusb_free_config_descriptor(config); }
    }

    // libusb exposes the device via the handle; retain it in the managed handle map.
    private readonly ConcurrentDictionary<nint, nint> _openedDevices = new();
    private nint GetOpenedDevice(nint handle) => _openedDevices.TryGetValue(handle, out var device) ? device : 0;

    public int ClaimInterface(nint handle, int interfaceNumber) => Native.libusb_claim_interface(handle, interfaceNumber);
    public int SetAlternateSetting(nint handle, int interfaceNumber, int alternateSetting) => Native.libusb_set_interface_alt_setting(handle, interfaceNumber, alternateSetting);
    public int ReleaseInterface(nint handle, int interfaceNumber) => Native.libusb_release_interface(handle, interfaceNumber);
    public int BulkTransfer(nint handle, byte endpoint, byte[] buffer, int length, out int transferred, uint timeoutMs) => Native.libusb_bulk_transfer(handle, endpoint, buffer, length, out transferred, timeoutMs);
    public void Close(nint handle) { _openedDevices.TryRemove(handle, out _); Native.libusb_close(handle); }
    public void Exit(nint context) => Native.libusb_exit(context);
    public string ErrorName(int errorCode) => Marshal.PtrToStringUTF8(Native.libusb_error_name(errorCode)) ?? $"libusb error {errorCode}";

    private static uint? GetLocation(nint device)
    {
        var bus = Native.libusb_get_bus_number(device);
        var ports = new byte[8];
        var count = Native.libusb_get_port_numbers(device, ports, ports.Length);
        if (count <= 0) return null;
        uint location = (uint)bus << 24;
        for (var i = 0; i < count && i < 4; i++) location |= (uint)ports[i] << (20 - i * 4);
        return location;
    }

    private static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref _resolverInstalled, 1) != 0) return;
        NativeLibrary.SetDllImportResolver(typeof(NativeLibUsbApi).Assembly, (name, _, _) =>
        {
            if (name != ImportName) return 0;
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "libusb-1.0.dylib"),
                "/opt/homebrew/lib/libusb-1.0.dylib",
                "/usr/local/lib/libusb-1.0.dylib",
            };
            foreach (var path in candidates) if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle)) return handle;
            return 0;
        });
    }

    private sealed class DeviceList(nint list, IReadOnlyList<LibUsbDevice> devices) : ILibUsbDeviceList
    {
        public IReadOnlyList<LibUsbDevice> Devices { get; } = devices;
        public void Dispose() => Native.libusb_free_device_list(list, 1);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct DeviceDescriptor
    {
        public byte bLength, bDescriptorType; public ushort bcdUSB; public byte bDeviceClass, bDeviceSubClass, bDeviceProtocol, bMaxPacketSize0;
        public ushort idVendor, idProduct, bcdDevice; public byte iManufacturer, iProduct, iSerialNumber, bNumConfigurations;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ConfigDescriptor
    {
        public byte bLength, bDescriptorType; public ushort wTotalLength; public byte bNumInterfaces, bConfigurationValue, iConfiguration, bmAttributes, MaxPower;
        public IntPtr interface_array, extra; public int extra_length;
    }
    [StructLayout(LayoutKind.Sequential)] private struct InterfaceDescriptor
    { public IntPtr altsetting; public int num_altsetting; }
    [StructLayout(LayoutKind.Sequential)] private struct AltSettingDescriptor
    {
        public byte bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints, bInterfaceClass, bInterfaceSubClass, bInterfaceProtocol, iInterface;
        public IntPtr endpoint, extra; public int extra_length;
    }
    [StructLayout(LayoutKind.Sequential)] private struct EndpointDescriptor
    {
        public byte bLength, bDescriptorType, bEndpointAddress, bmAttributes; public ushort wMaxPacketSize; public byte bInterval, bRefresh, bSynchAddress;
        public IntPtr extra; public int extra_length;
    }

    private static class Native
    {
        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)] internal static extern int libusb_init(out nint context);
        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)] internal static extern nint libusb_get_device_list(nint context, out nint list);
        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)] internal static extern void libusb_free_device_list(nint list, int unrefDevices);
        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)] internal static extern int libusb_get_device_descriptor(nint device, out DeviceDescriptor descriptor);
        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)] internal static extern byte libusb_get_bus_number(nint device);
        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)] internal static extern int libusb_get_port_numbers(nint device, byte[] ports, int portCount);
        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)] internal static extern int libusb_open(nint device, out nint handle);
        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)] internal static extern int libusb_get_active_config_descriptor(nint device, out nint config);
        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)] internal static extern void libusb_free_config_descriptor(nint config);
        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)] internal static extern int libusb_claim_interface(nint handle, int interfaceNumber);
        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)] internal static extern int libusb_set_interface_alt_setting(nint handle, int interfaceNumber, int alternateSetting);
        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)] internal static extern int libusb_release_interface(nint handle, int interfaceNumber);
        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)] internal static extern int libusb_bulk_transfer(nint handle, byte endpoint, byte[] data, int length, out int transferred, uint timeout);
        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)] internal static extern void libusb_close(nint handle);
        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)] internal static extern void libusb_exit(nint context);
        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr libusb_error_name(int errorCode);
    }
}
