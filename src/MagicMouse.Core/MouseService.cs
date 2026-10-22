using System.Diagnostics;
using MagicMouse.Core.Driver;
using MagicMouse.Core.Gestures;
using MagicMouse.Core.Protocol;
using MagicMouse.Core.Settings;

namespace MagicMouse.Core;

public enum ServiceState { DriverMissing, NoDevice, Running, Paused }

/// <summary>
/// Runs the device loop: opens the filter driver, configures every Magic Mouse it filters, reads touch
/// reports on a background thread, feeds one <see cref="GestureEngine"/> per device and drives momentum.
/// </summary>
public sealed class MouseService : IDisposable
{
    private readonly object _gate = new();
    private readonly IInputSink _sink;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<int, (FilterDevice Device, GestureEngine Engine)> _devices = new();
    private AppSettings _settings;
    private FilterClient? _client;
    private Thread? _reader, _ticker;
    private volatile bool _stop;
    private volatile bool _paused;
    private readonly Timer _wake;

    public event Action<ServiceState>? StateChanged;
    /// <summary>Raised on the reader thread for each frame (for the live touches view).</summary>
    public event Action<FilterDevice, TouchFrame>? FrameReceived;

    public ServiceState State { get; private set; } = ServiceState.DriverMissing;
    public IReadOnlyList<FilterDevice> Devices { get { lock (_gate) return _devices.Values.Select(v => v.Device).ToList(); } }

    /// <summary>Fresh device info straight from the driver (counters, cached battery). Falls back to the
    /// last known list when the driver is not available.</summary>
    public IReadOnlyList<FilterDevice> RefreshedDevices()
    {
        try
        {
            if (_client?.GetDevices() is { } fresh) return fresh;
        }
        catch (System.ComponentModel.Win32Exception) { /* fall through */ }
        return Devices;
    }

    public MouseService(AppSettings settings, IInputSink? sink = null)
    {
        _settings = settings;
        _sink = sink ?? new SendInputSink();
        _wake = new Timer(_ => _client?.CancelPendingReads());
    }

    public void Start()
    {
        _stop = false;
        _ticker = new Thread(TickLoop) { IsBackground = true, Name = "MagicMouse momentum", Priority = ThreadPriority.AboveNormal };
        _ticker.Start();
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "MagicMouse reader", Priority = ThreadPriority.Highest };
        _reader.Start();
    }

    public void ApplySettings(AppSettings settings)
    {
        lock (_gate)
        {
            _settings = settings;
            foreach (var (_, engine) in _devices.Values) engine.UpdateSettings(settings);
        }
        PushConfig();
    }

    /// <summary>Pause multitouch (display off, battery saver...). The mouse keeps working as a plain mouse.</summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;
        lock (_gate) foreach (var (_, e) in _devices.Values) e.Reset();
        PushConfig();
        SetState(paused ? ServiceState.Paused : _devices.Count > 0 ? ServiceState.Running : State);
    }

    /// <summary>Drops the Bluetooth link of every Bluetooth mouse so it can deep-sleep. The mouse stays paired
    /// and reconnects by itself when moved or clicked; the driver re-enables multitouch then.</summary>
    public void DisconnectBluetooth()
    {
        foreach (var d in RefreshedDevices().Where(d => d.Transport == Transport.Bluetooth))
        {
            if (Hid.BluetoothLink.ParseAddress(d.BluetoothAddress) is { } addr) Hid.BluetoothLink.Disconnect(addr);
        }
    }

    /// <summary>Re-send the mode switch, e.g. after resume from sleep.</summary>
    public void Recover() => PushConfig();

    private void PushConfig()
    {
        var c = _client;
        if (c == null) return;
        List<(FilterDevice d, GestureEngine e)> list;
        AppSettings s;
        lock (_gate) { list = _devices.Values.ToList(); s = _settings; }
        foreach (var (d, e) in list)
        {
            var flags = FilterFlags.None;
            if (s.Enabled && !_paused)
            {
                flags |= FilterFlags.Multitouch;
                if (e.OwnsButtons) flags |= FilterFlags.SuppressButtons;
                if (s.Power.BluetoothKeepAlive && d.Transport == Transport.Bluetooth) flags |= FilterFlags.KeepAlive;
            }
            try { c.SetConfig(d.Index, flags, (uint)s.Power.BluetoothKeepAlivePeriodMs); }
            catch (System.ComponentModel.Win32Exception) { /* device vanished; rescan handles it */ }
        }
    }

    private void RefreshDevices()
    {
        var c = _client;
        if (c == null) return;
        var found = c.GetDevices();
        List<FilterDevice> added;
        lock (_gate) added = found.Where(f => !_devices.ContainsKey(f.Index)).ToList();
        foreach (var f in added)
        {
            // Read the battery while the mouse may still be in plain mouse mode: once multitouch is on it
            // answers this query with zeros. The driver caches the real reply for later reads.
            try { Hid.BatteryReader.Read(f.BluetoothAddress); }
            catch (Exception) { /* best effort */ }
        }
        lock (_gate)
        {
            foreach (var gone in _devices.Keys.Except(found.Select(f => f.Index)).ToList())
            {
                _devices[gone].Engine.Reset();
                _devices.Remove(gone);
            }
            foreach (var f in found)
            {
                if (_devices.TryGetValue(f.Index, out var existing)) _devices[f.Index] = (f, existing.Engine);
                else _devices[f.Index] = (f, new GestureEngine(_settings, _sink, f.Model));
            }
        }
        // Re-sent on every scan (every few seconds): the driver only talks to the mouse when the mode
        // actually changes, and this restores our config if another client (HidProbe) changed it.
        PushConfig();
        SetState(_paused ? ServiceState.Paused : found.Count > 0 ? ServiceState.Running : ServiceState.NoDevice);
    }

    private void ReadLoop()
    {
        var lastScan = TimeSpan.Zero;
        while (!_stop)
        {
            if (_client == null)
            {
                _client = FilterClient.TryOpen();
                if (_client == null) { SetState(ServiceState.DriverMissing); Thread.Sleep(2000); continue; }
                lastScan = TimeSpan.Zero;
            }
            try
            {
                if (_clock.Elapsed - lastScan > TimeSpan.FromSeconds(3) || _devices.Count == 0)
                {
                    RefreshDevices();
                    lastScan = _clock.Elapsed;
                    if (_devices.Count == 0) { Thread.Sleep(1000); continue; }
                }

                // Wake periodically so device changes are noticed even without touches.
                _wake.Change(3000, Timeout.Infinite);
                var report = _client.ReadReport();
                _wake.Change(Timeout.Infinite, Timeout.Infinite);
                if (report is not { } r) continue;
                Dispatch(r);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Driver unloaded (last mouse removed / driver update): reopen.
                _client?.Dispose();
                _client = null;
                lock (_gate) { foreach (var v in _devices.Values) v.Engine.Reset(); _devices.Clear(); }
                Thread.Sleep(500);
            }
        }
    }

    private void Dispatch(RawReport r)
    {
        FilterDevice device;
        GestureEngine engine;
        lock (_gate)
        {
            if (!_devices.TryGetValue(r.DeviceIndex, out var entry)) return;
            (device, engine) = entry;
        }
        var frame = TouchReportParser.Parse(device.Model, r.Data, r.Timestamp100ns);
        if (frame == null) return;
        if (!_paused)
        {
            lock (_gate) engine.Process(frame, _clock.Elapsed.TotalMilliseconds);
        }
        FrameReceived?.Invoke(device, frame);
    }

    private void TickLoop()
    {
        while (!_stop)
        {
            Thread.Sleep(8);
            double now = _clock.Elapsed.TotalMilliseconds;
            // Tick even without momentum so the engine's clock stays current.
            lock (_gate) foreach (var (_, e) in _devices.Values) e.Tick(now);
        }
    }

    private void SetState(ServiceState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Dispose();
        lock (_gate) foreach (var (_, e) in _devices.Values) e.Reset();
        _client?.Dispose();
        _reader?.Join(1000);
        _ticker?.Join(200);
    }
}
