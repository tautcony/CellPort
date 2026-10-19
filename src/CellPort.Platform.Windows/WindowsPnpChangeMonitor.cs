using System.Management;

namespace CellPort.Platform.Windows;

/// <summary>Signals PnP graph changes and invalidates cached interface discovery.</summary>
public sealed class WindowsPnpChangeMonitor : IDisposable
{
    private readonly ManagementEventWatcher _watcher;
    private bool _disposed;

    public WindowsPnpChangeMonitor()
    {
        _watcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent"));
        _watcher.EventArrived += OnEventArrived;
    }

    public event EventHandler? DeviceGraphChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _watcher.Start();
    }

    public void Stop()
    {
        if (!_disposed)
        {
            _watcher.Stop();
        }
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs args)
    {
        ModuleScanner.InvalidateCache();
        DeviceGraphChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _watcher.EventArrived -= OnEventArrived;
        _watcher.Dispose();
    }
}
