using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using CellPort.Core.Transport;

namespace CellPort.Platform.Windows;

/// <summary>Reads present PnP devnodes and their physical USB association properties.</summary>
public static class WindowsUsbDeviceEnumerator
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;
    private const uint DevpropTypeGuid = 0x0000000D;
    private const uint DevpropTypeString = 0x00000012;
    private const uint DevpropTypeStringList = 0x00002012;

    private static readonly Guid ParentFormatId = new("4340A6C5-93FA-4706-972C-7B648008A5A7");
    private static readonly Guid DeviceFormatId = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    private static readonly Guid ContainerFormatId = new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C");

    public static IReadOnlyList<WindowsPnpDeviceNode> EnumeratePresent()
    {
        var deviceInfoSet = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero,
            DigcfPresent | DigcfAllClasses);
        if (deviceInfoSet == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate PnP devices.");
        }

        try
        {
            var nodes = new List<WindowsPnpDeviceNode>();
            for (uint index = 0; ; index++)
            {
                var deviceInfo = new SpDevinfoData { Size = (uint)Marshal.SizeOf<SpDevinfoData>() };
                if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfo))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                    {
                        break;
                    }
                    throw new Win32Exception(error, "Could not read a PnP device node.");
                }

                var instanceId = ReadInstanceId(deviceInfoSet, ref deviceInfo);
                if (instanceId is null)
                {
                    continue;
                }

                nodes.Add(new WindowsPnpDeviceNode(
                    instanceId,
                    ReadStringProperty(deviceInfoSet, ref deviceInfo, ParentFormatId, 8),
                    ReadGuidProperty(deviceInfoSet, ref deviceInfo, ContainerFormatId, 2),
                    ReadStringListProperty(deviceInfoSet, ref deviceInfo, DeviceFormatId, 37),
                    ReadStringListProperty(deviceInfoSet, ref deviceInfo, DeviceFormatId, 3),
                    ReadStringProperty(deviceInfoSet, ref deviceInfo, DeviceFormatId, 14)));
            }

            return nodes;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static string? ReadInstanceId(IntPtr set, ref SpDevinfoData deviceInfo)
    {
        _ = SetupDiGetDeviceInstanceIdW(set, ref deviceInfo, null, 0, out var required);
        var error = Marshal.GetLastWin32Error();
        if (required == 0 || error != ErrorInsufficientBuffer)
        {
            return null;
        }

        var buffer = new StringBuilder((int)required);
        return SetupDiGetDeviceInstanceIdW(set, ref deviceInfo, buffer, required, out _)
            ? buffer.ToString()
            : null;
    }

    private static string? ReadStringProperty(IntPtr set, ref SpDevinfoData deviceInfo, Guid formatId, uint id)
    {
        var value = ReadProperty(set, ref deviceInfo, formatId, id, out var type);
        return value is not null && type == DevpropTypeString
            ? Encoding.Unicode.GetString(value).TrimEnd('\0')
            : null;
    }

    private static Guid? ReadGuidProperty(IntPtr set, ref SpDevinfoData deviceInfo, Guid formatId, uint id)
    {
        var value = ReadProperty(set, ref deviceInfo, formatId, id, out var type);
        return value is not null && type == DevpropTypeGuid && value.Length >= 16
            ? new Guid(value.AsSpan(0, 16))
            : null;
    }

    private static System.Collections.Immutable.ImmutableArray<string> ReadStringListProperty(
        IntPtr set,
        ref SpDevinfoData deviceInfo,
        Guid formatId,
        uint id)
    {
        var value = ReadProperty(set, ref deviceInfo, formatId, id, out var type);
        if (value is null || type != DevpropTypeStringList)
        {
            return [];
        }

        return [.. Encoding.Unicode.GetString(value)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private static byte[]? ReadProperty(
        IntPtr set,
        ref SpDevinfoData deviceInfo,
        Guid formatId,
        uint id,
        out uint type)
    {
        var key = new DevpropKey { FormatId = formatId, PropertyId = id };
        _ = SetupDiGetDevicePropertyW(set, ref deviceInfo, ref key, out type,
            IntPtr.Zero, 0, out var required, 0);
        if (required == 0 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
        {
            type = 0;
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            if (!SetupDiGetDevicePropertyW(set, ref deviceInfo, ref key, out type,
                    buffer, required, out _, 0))
            {
                return null;
            }

            var value = new byte[required];
            Marshal.Copy(buffer, value, 0, value.Length);
            return value;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    private static extern IntPtr SetupDiGetClassDevsW(
        IntPtr classGuid,
        string? enumerator,
        IntPtr parentWindow,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiEnumDeviceInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevinfoData deviceInfo);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiDestroyDeviceInfoList")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInstanceIdW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfo,
        StringBuilder? deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDevicePropertyW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfo,
        ref DevpropKey propertyKey,
        out uint propertyType,
        IntPtr propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevpropKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }
}
