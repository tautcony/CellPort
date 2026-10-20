using CellPort.Core.Transport;

namespace CellPort.Platform.MacOS;

/// <summary>Claimed libusb AT endpoint pair. AT framing and URC accumulation remain in Core.</summary>
internal sealed class LibUsbAtChannel : IAtChannel
{
    private const uint TransferSliceMs = 100;
    private readonly ILibUsbApi _api;
    private readonly nint _context;
    private readonly nint _handle;
    private readonly int _interfaceNumber;
    private readonly byte _inputEndpoint;
    private readonly byte _outputEndpoint;
    private readonly SemaphoreSlim _io = new(1, 1);
    private bool _claimed = true;
    private bool _disposed;

    private LibUsbAtChannel(ILibUsbApi api, nint context, nint handle, int interfaceNumber, byte inputEndpoint, byte outputEndpoint)
    {
        _api = api; _context = context; _handle = handle; _interfaceNumber = interfaceNumber;
        _inputEndpoint = inputEndpoint; _outputEndpoint = outputEndpoint;
    }

    public string Description => $"USB（接口 {_interfaceNumber}）";
    public AtChannelKind Kind => AtChannelKind.UsbDirect;
    public bool IsOpen => !_disposed;

    internal static LibUsbAtChannel? TryOpen(ILibUsbApi api, IReadOnlyList<MacUsbDeviceIdentity> registry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var context = api.Init();
        if (context == 0) return null;
        nint handle = 0;
        var claimed = false;
        var claimedInterface = -1;
        var transferred = false;
        try
        {
            using var list = api.GetDeviceList(context);
            var knownDevices = list.Devices.Where(d => ModuleIdentity.KnownIds.Contains((d.VendorId, d.ProductId))).ToArray();
            var selected = SelectDevice(knownDevices, registry);
            if (selected is null) return null;
            ct.ThrowIfCancellationRequested();
            var openResult = api.Open(selected.Value, out handle);
            if (openResult != 0 || handle == 0) return null;

            var settings = api.GetActiveConfiguration(handle);
            foreach (var setting in settings
                         .Where(s => s.InterfaceNumber is ModuleIdentity.AtInterfaceNumber or ModuleIdentity.AtInterfaceNumberAlt)
                         .OrderBy(s => s.InterfaceNumber == ModuleIdentity.AtInterfaceNumber ? 0 : 1)
                         .ThenBy(s => s.AlternateSetting))
            {
                var endpoints = FindBulkPair(setting.Endpoints);
                if (endpoints is null) continue;
                ct.ThrowIfCancellationRequested();
                var claimResult = api.ClaimInterface(handle, setting.InterfaceNumber);
                if (claimResult != 0) continue;
                claimed = true;
                claimedInterface = setting.InterfaceNumber;
                if (setting.AlternateSetting != 0 && api.SetAlternateSetting(handle, setting.InterfaceNumber, setting.AlternateSetting) != 0)
                {
                    api.ReleaseInterface(handle, claimedInterface); claimed = false; claimedInterface = -1;
                    continue;
                }
                transferred = true;
                return new LibUsbAtChannel(api, context, handle, setting.InterfaceNumber, endpoints.Value.Input, endpoints.Value.Output);
            }
            return null;
        }
        finally
        {
            if (!transferred)
            {
                if (handle != 0)
                {
                    if (claimed && claimedInterface >= 0) api.ReleaseInterface(handle, claimedInterface);
                    api.Close(handle);
                }
                api.Exit(context);
            }
        }
    }

    internal static (byte Input, byte Output)? FindBulkPair(IReadOnlyList<LibUsbEndpoint> endpoints)
    {
        var bulk = endpoints.Where(e => (e.Attributes & 0x03) == 0x02).ToArray();
        var input = bulk.FirstOrDefault(e => (e.Address & 0x80) != 0);
        var output = bulk.FirstOrDefault(e => (e.Address & 0x80) == 0);
        return input.Address != 0 && output.Address != 0 ? (input.Address, output.Address) : null;
    }

    internal static LibUsbDevice? SelectDevice(IReadOnlyList<LibUsbDevice> candidates, IReadOnlyList<MacUsbDeviceIdentity> registry)
    {
        if (candidates.Count == 0) return null;
        var mapped = candidates.Select(device => (device, match: registry.Where(r => r.VendorId == device.VendorId && r.ProductId == device.ProductId && r.LocationId == device.LocationId && r.LocationId.HasValue).ToArray()))
            .Where(pair => pair.match.Length > 0).Select(pair => pair.device).DistinctBy(d => (d.NativeDevice, d.LocationId)).ToArray();
        if (mapped.Length == 1) return mapped[0];
        if (mapped.Length > 1) return null;
        return candidates.Count == 1 ? candidates[0] : null;
    }

    // The provider returns an already claimed channel; ModemManager's OpenAsync call is intentionally idempotent.
    public Task OpenAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); ThrowIfDisposed(); return Task.CompletedTask; }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var offset = 0;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (offset < data.Length)
            {
                ct.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Timed out writing to the USB AT interface.");
                var bytes = data.Slice(offset).ToArray();
                var transfer = await Task.Run(() =>
                {
                    var result = _api.BulkTransfer(_handle, _outputEndpoint, bytes, bytes.Length, out var count, TransferSliceMs);
                    return (result, count);
                }).ConfigureAwait(false);
                var result = transfer.result;
                var transferred = transfer.count;
                if (result == _api.TimeoutErrorCode) continue;
                Check(result, "write");
                if (transferred <= 0) throw new IOException("libusb reported a successful write with no bytes transferred.");
                offset += transferred;
            }
        }
        finally { _io.Release(); }
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (buffer.Length == 0 || timeout <= TimeSpan.Zero) return 0;
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var remaining = deadline - DateTime.UtcNow;
                var waitMs = (uint)Math.Clamp((int)Math.Ceiling(remaining.TotalMilliseconds), 1, (int)TransferSliceMs);
                var bytes = new byte[buffer.Length];
                var transfer = await Task.Run(() =>
                {
                    var result = _api.BulkTransfer(_handle, _inputEndpoint, bytes, bytes.Length, out var count, waitMs);
                    return (result, count);
                }).ConfigureAwait(false);
                var result = transfer.result;
                var transferred = transfer.count;
                if (result == _api.TimeoutErrorCode) continue;
                Check(result, "read");
                if (transferred > 0) { bytes.AsMemory(0, transferred).CopyTo(buffer); return transferred; }
            }
            return 0;
        }
        finally { _io.Release(); }
    }

    public async Task CloseAsync()
    {
        if (_disposed) return;
        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_claimed) { _api.ReleaseInterface(_handle, _interfaceNumber); _claimed = false; }
            _api.Close(_handle);
            _api.Exit(_context);
        }
        finally { _io.Release(); }
    }

    private void Check(int result, string operation)
    {
        if (result != 0) throw new IOException($"libusb {operation} failed: {_api.ErrorName(result)} ({result}).");
    }
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(LibUsbAtChannel)); }
}
