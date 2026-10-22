using System.Drawing.Drawing2D;
using MagicMouse.Core;
using MagicMouse.Core.Hid;
using MagicMouse.Core.Settings;
using Microsoft.Win32;

namespace MagicMouse.App;

/// <summary>Tray icon, battery monitor, power/session handling. Owns the <see cref="MouseService"/>.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly MouseService _service;
    private readonly System.Windows.Forms.Timer _batteryTimer;
    private readonly MessageWindow _messages;
    private readonly ToolStripMenuItem _enabledItem;
    private AppSettings _settings;
    private SettingsForm? _form;
    private BatteryStatus? _battery;
    private int _lastAlertLevel = int.MaxValue;
    private bool _displayOff, _onBattery, _sessionLocked;
    private Icon? _currentIcon;
    private readonly SynchronizationContext _ui;

    public TrayContext(bool startMinimized)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_ui);
        _settings = AppSettings.Load();
        AutoStart.Apply(_settings.AutoStart);
        if (_settings.Pointer.PointerSpeed > 0) PointerSpeed.Set(_settings.Pointer.PointerSpeed);

        _service = new MouseService(_settings);
        _service.StateChanged += state => UiInvoke(() =>
        {
            UpdateTray();
            // The driver's cached battery value is only reachable once the service is connected; the read
            // at startup usually happens before that, so read again as soon as a mouse is found.
            if (state == ServiceState.Running) RefreshBattery();
        });

        _enabledItem = new ToolStripMenuItem("Enabled", null, (_, _) => ToggleEnabled()) { Checked = _settings.Enabled };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        menu.Items.Add(_enabledItem);
        menu.Items.Add("Re-enable multitouch", null, (_, _) => _service.Recover());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = _settings.ShowTrayIcon, Text = "MagicMouse" };
        _tray.DoubleClick += (_, _) => ShowSettings();

        _messages = new MessageWindow(this);

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        _batteryTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _batteryTimer.Tick += (_, _) => RefreshBattery();
        _batteryTimer.Start();

        _service.Start();
        RefreshBattery();
        UpdateTray();
        if (!startMinimized && !_settings.StartMinimized) ShowSettings();
    }

    public AppSettings Settings => _settings;
    public MouseService Service => _service;
    public BatteryStatus? Battery => _battery;

    public void ApplySettings(AppSettings s)
    {
        bool autoStartChanged = s.AutoStart != _settings.AutoStart;
        bool speedChanged = s.Pointer.PointerSpeed != _settings.Pointer.PointerSpeed;
        _settings = s;
        _settings.Save();
        if (autoStartChanged) AutoStart.Apply(s.AutoStart);
        if (speedChanged && s.Pointer.PointerSpeed > 0) PointerSpeed.Set(s.Pointer.PointerSpeed);
        _enabledItem.Checked = s.Enabled;
        _tray.Visible = s.ShowTrayIcon;
        _service.ApplySettings(s.Clone());
        UpdatePause();
        UpdateTray();
    }

    private void ToggleEnabled()
    {
        var s = _settings.Clone();
        s.Enabled = !s.Enabled;
        ApplySettings(s);
        _form?.ReloadFromSettings();
    }

    public void ShowSettings()
    {
        if (_form == null || _form.IsDisposed)
        {
            _form = new SettingsForm(this);
            _form.FormClosed += (_, _) => _form = null;
        }
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    // ------------------------------------------------------------------ battery

    public void RefreshBattery()
    {
        Task.Run(() =>
        {
            var dev = _service.RefreshedDevices().FirstOrDefault();
            return BatteryReader.Read(dev?.BluetoothAddress, dev?.Battery);
        }).ContinueWith(t =>
        {
            // Keep the last known value rather than flipping to "unknown" on a failed read.
            if (t.IsCompletedSuccessfully && t.Result != null) _battery = t.Result;
            CheckBatteryAlerts();
            UpdateTray();
            _form?.UpdateStatus();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void CheckBatteryAlerts()
    {
        var b = _settings.Battery;
        if (_battery is not { } s || !b.ShowAlerts) return;
        int p = s.Percent;
        if (p <= b.CriticalPercent && b.CriticalAlert && _lastAlertLevel > b.CriticalPercent)
            Balloon("Magic Mouse battery critical", $"{p}% left. Charge the mouse now.", ToolTipIcon.Error);
        else if (p <= b.WarningPercent && b.WarningAlert && _lastAlertLevel > b.WarningPercent)
            Balloon("Magic Mouse battery low", $"{p}% left.", ToolTipIcon.Warning);
        else if (p >= b.ChargedPercent && b.RechargedAlert && _lastAlertLevel < b.ChargedPercent && _lastAlertLevel != int.MaxValue)
            Balloon("Magic Mouse charged", $"Battery at {p}%.", ToolTipIcon.Info);
        _lastAlertLevel = p;
    }

    private void Balloon(string title, string text, ToolTipIcon icon) => _tray.ShowBalloonTip(8000, title, text, icon);

    // ------------------------------------------------------------------ tray icon

    private void UpdateTray()
    {
        string state = _service.State switch
        {
            ServiceState.DriverMissing => "driver not installed",
            ServiceState.NoDevice => "no Magic Mouse",
            ServiceState.Paused => "paused",
            _ => _settings.Enabled ? "active" : "disabled",
        };
        string battery = _battery is { } b ? $" - {b.Percent}%{(b.Charging == true ? " (charging)" : "")}" : "";
        var text = $"MagicMouse: {state}{battery}";
        _tray.Text = text.Length > 63 ? text[..63] : text;

        var old = _currentIcon;
        _currentIcon = DrawIcon(_battery?.Percent, _service.State == ServiceState.Running && _settings.Enabled);
        _tray.Icon = _currentIcon;
        if (old != null) { NativeUi.DestroyIcon(old.Handle); old.Dispose(); }
    }

    private static Icon DrawIcon(int? percent, bool active)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var app = AppIcon.Get(32);
            if (active) g.DrawIcon(app, new Rectangle(0, 0, 32, 32));
            else ControlPaint.DrawImageDisabled(g, app.ToBitmap(), 0, 0, Color.Transparent);
            if (percent is int p)
            {
                // Battery bar along the right edge, colored by level.
                var c = p <= 10 ? Color.Red : p <= 25 ? Color.Orange : Color.LimeGreen;
                using var back = new SolidBrush(Color.FromArgb(200, 30, 30, 30));
                using var bar = new SolidBrush(c);
                g.FillRectangle(back, 25, 3, 6, 26);
                int h = Math.Max(2, 24 * p / 100);
                g.FillRectangle(bar, 26, 28 - h, 4, h);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    // ------------------------------------------------------------------ power / session

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume && _settings.Power.RecoverMultitouchOnWakeup)
        {
            // Bluetooth needs a moment to reconnect; the driver also re-sends on the first plain report.
            Task.Delay(1500).ContinueWith(_ => _service.Recover());
        }
        if (e.Mode == PowerModes.StatusChange)
        {
            _onBattery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
            UpdatePause();
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        _sessionLocked = e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff
            or SessionSwitchReason.ConsoleDisconnect;
        UpdatePause();
        if (e.Reason == SessionSwitchReason.SessionLock && _settings.Power.DisconnectOnLock)
            DisconnectSoon(() => _sessionLocked);
    }

    internal void OnDisplayState(bool on)
    {
        bool turnedOff = !on && !_displayOff;
        _displayOff = !on;
        UpdatePause();
        if (turnedOff && _settings.Power.DisconnectOnDisplayOff) DisconnectSoon(() => _displayOff);
    }

    /// <summary>Drops the mouse's Bluetooth link after a short grace period, if the condition still holds,
    /// so the mouse can deep-sleep. Moving or clicking it reconnects it.</summary>
    private void DisconnectSoon(Func<bool> stillWanted)
    {
        Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
        {
            if (stillWanted()) _service.DisconnectBluetooth();
        });
    }

    private void UpdatePause()
    {
        var p = _settings.Power;
        bool pause = (p.PauseOnDisplayOff && _displayOff) || (p.PauseOnBatteryPower && _onBattery) ||
                     (p.PauseOnLogOff && _sessionLocked);
        if (pause != (_service.State == ServiceState.Paused)) _service.SetPaused(pause);
    }

    private void UiInvoke(Action a) => _ui.Post(_ => a(), null);

    protected override void ExitThreadCore()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _batteryTimer.Dispose();
        _service.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _messages.Dispose();
        _form?.Dispose();   // Close() would only hide it
        base.ExitThreadCore();
    }

    /// <summary>Hidden window: display-state notifications and "show" requests from a second instance.</summary>
    private sealed class MessageWindow : NativeWindow, IDisposable
    {
        private readonly TrayContext _owner;
        private IntPtr _powerNotify;

        public MessageWindow(TrayContext owner)
        {
            _owner = owner;
            // Top-level (not message-only) so HWND_BROADCAST from a second instance reaches it.
            CreateHandle(new CreateParams { Caption = "MagicMouse.Messages" });
            var g = NativeUi.GUID_CONSOLE_DISPLAY_STATE;
            _powerNotify = NativeUi.RegisterPowerSettingNotification(Handle, ref g, 0);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeUi.ShowMessage) _owner.ShowSettings();
            else if (m.Msg == NativeUi.WM_POWERBROADCAST && (int)m.WParam == NativeUi.PBT_POWERSETTINGCHANGE)
            {
                var s = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeUi.POWERBROADCAST_SETTING>(m.LParam);
                if (s.PowerSetting == NativeUi.GUID_CONSOLE_DISPLAY_STATE) _owner.OnDisplayState(s.Data != 0);
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (_powerNotify != IntPtr.Zero) { NativeUi.UnregisterPowerSettingNotification(_powerNotify); _powerNotify = IntPtr.Zero; }
            DestroyHandle();
        }
    }
}
