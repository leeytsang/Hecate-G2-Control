using PeripheralBattery.Core.Protocols;
using Windows.Devices.Bluetooth.Advertisement;

namespace PeripheralBattery.Windows.Bluetooth;

/// <summary>
/// Connection state for the headset's BLE control channel.
/// The persistent GATT session is the primary source; advertisements are used
/// for discovery and as a safe startup fallback.
/// </summary>
public sealed class EdifierPresenceMonitor : IDisposable
{
    private static readonly TimeSpan LostAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InitialTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan GattDisconnectGrace = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan AdReconnectGrace = TimeSpan.FromSeconds(3);
    private readonly object _sync = new();
    private readonly BluetoothLEAdvertisementWatcher _watcher = new()
    {
        ScanningMode = BluetoothLEScanningMode.Active,
    };
    private Timer? _timer;
    private long _startedAt;
    private long? _lastSeen;
    private long _lastGattDisconnectedAt;
    private bool _gattConnected;
    private bool _gattEverConnected;
    private bool? _present;
    private bool _started;
    private bool _disposed;

    public event Action<bool>? PresenceChanged;

    public bool IsPresent
    {
        get { lock (_sync) return _present == true; }
    }

    public void Start()
    {
        if (_disposed || _started) return;
        _started = true;
        _startedAt = Environment.TickCount64;
        _watcher.Received += OnReceived;
        try
        {
            _watcher.Start();
            _timer = new Timer(Tick, null, 0, 500);
        }
        catch
        {
            _watcher.Received -= OnReceived;
            _started = false;
            throw;
        }
    }

    public void NoteSeen()
    {
        if (_disposed) return;
        bool changed = false;
        lock (_sync)
        {
            _lastSeen = Environment.TickCount64;
            if (_present != true)
            {
                _present = true;
                changed = true;
            }
        }
        if (changed) PresenceChanged?.Invoke(true);
    }

    public void NoteGattConnected()
    {
        if (_disposed) return;
        bool changed = false;
        lock (_sync)
        {
            _gattConnected = true;
            _gattEverConnected = true;
            _lastSeen = Environment.TickCount64;
            if (_present != true)
            {
                _present = true;
                changed = true;
            }
        }
        if (changed) PresenceChanged?.Invoke(true);
    }

    public void NoteGattDisconnected()
    {
        if (_disposed) return;
        lock (_sync)
        {
            _gattConnected = false;
            _gattEverConnected = true;
            _lastGattDisconnectedAt = Environment.TickCount64;
        }
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var ad = args.Advertisement;
        if (string.Equals(ad.LocalName, EdifierBleProtocol.AdvertisingName, StringComparison.OrdinalIgnoreCase)
            || ad.ServiceUuids.Contains(EdifierBleProtocol.AdvertisingServiceUuid))
            NoteSeen();
    }

    private void Tick(object? state)
    {
        if (_disposed) return;

        bool? changedTo = null;
        lock (_sync)
        {
            long now = Environment.TickCount64;
            if (_gattConnected)
            {
                if (_present != true)
                {
                    _present = true;
                    changedTo = true;
                }
            }
            else if (_gattEverConnected)
            {
                long disconnectAge = now - _lastGattDisconnectedAt;
                long adAge = _lastSeen is { } last
                    ? now - last
                    : long.MaxValue;
                if (disconnectAge >= GattDisconnectGrace.TotalMilliseconds
                    && adAge >= AdReconnectGrace.TotalMilliseconds
                    && _present != false)
                {
                    _present = false;
                    changedTo = false;
                }
            }
            else if (_lastSeen is { } lastSeen)
            {
                if (now - lastSeen >= LostAfter.TotalMilliseconds && _present != false)
                {
                    _present = false;
                    changedTo = false;
                }
            }
            else if (now - _startedAt >= InitialTimeout.TotalMilliseconds && _present is null)
            {
                _present = false;
                changedTo = false;
            }
        }

        if (changedTo is { } present) PresenceChanged?.Invoke(present);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher.Received -= OnReceived;
        try { _watcher.Stop(); } catch { }
        _timer?.Dispose();
        _timer = null;
    }
}
