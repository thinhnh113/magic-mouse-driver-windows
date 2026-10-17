using System.Diagnostics;
using MagicMouse.Core;
using MagicMouse.Core.Driver;
using MagicMouse.Core.Protocol;
using MagicMouse.Core.Settings;

namespace MagicMouse.App;

/// <summary>Settings window. Every change is applied and saved immediately.</summary>
internal sealed class SettingsForm : Form
{
    private readonly TrayContext _owner;
    private AppSettings _s;
    private readonly List<Action> _loaders = new();
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly Label _battery = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f) };
    private readonly LiveTouchesView _live = new() { Size = new Size(260, 400), Visible = false };
    private bool _loading;

    public SettingsForm(TrayContext owner)
    {
        _owner = owner;
        _s = owner.Settings.Clone();
        Text = "MagicMouse";
        ClientSize = new Size(600, 640);
        MinimumSize = new Size(560, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont;
        Icon = AppIcon.Get(32);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(MouseTab());
        tabs.TabPages.Add(ScrollTab());
        tabs.TabPages.Add(GesturesTab());
        tabs.TabPages.Add(ClicksTab());
        tabs.TabPages.Add(DeviceTab());
        tabs.TabPages.Add(AppTab());
        Controls.Add(tabs);

        owner.Service.FrameReceived += OnFrame;
        owner.Service.StateChanged += OnState;
        FormClosed += (_, _) =>
        {
            owner.Service.FrameReceived -= OnFrame;
            owner.Service.StateChanged -= OnState;
        };
        ReloadFromSettings();
        UpdateStatus();
    }

    // ------------------------------------------------------------------ tabs

    private TabPage MouseTab()
    {
        var page = Page("Mouse", out var flow);
        var g = Group(flow, "Mouse options");
        Slider(g, "Pointer speed (0 = keep Windows setting)", 0, 20, s => s.Pointer.PointerSpeed, (s, v) => s.Pointer.PointerSpeed = v);
        Check(g, "Swap primary/secondary clicks", s => s.Buttons.SwapButtons, (s, v) => s.Buttons.SwapButtons = v);
        Check(g, "Left handed mode", s => s.Buttons.LeftHanded, (s, v) => s.Buttons.LeftHanded = v);
        Check(g, "Ignore input if mouse is lifted (gripped by both sides)", s => s.Pointer.IgnoreInputIfLifted, (s, v) => s.Pointer.IgnoreInputIfLifted = v);

        var split = Group(flow, "Left / right click area");
        Check(split, "Use custom left/right split", s => s.Buttons.CustomSplit, (s, v) => s.Buttons.CustomSplit = v);
        Slider(split, "Split position (% from left)", 20, 80, s => s.Buttons.LeftRightSplitPercent, (s, v) => s.Buttons.LeftRightSplitPercent = v);
        return page;
    }

    private TabPage ScrollTab()
    {
        var page = Page("Scrolling", out var flow);
        var g = Group(flow, "Scrolling");
        Check(g, "Scrolling", s => s.Scrolling.Enabled, (s, v) => s.Scrolling.Enabled = v);
        Check(g, "Smooth scrolling", s => s.Scrolling.Smooth, (s, v) => s.Scrolling.Smooth = v);
        Check(g, "Momentum (keep scrolling after a flick)", s => s.Scrolling.Momentum, (s, v) => s.Scrolling.Momentum = v);
        Slider(g, "Scroll speed", 0, 100, s => s.Scrolling.Speed, (s, v) => s.Scrolling.Speed = v);
        Slider(g, "Scroll dead zone (higher = a resting finger won't scroll)", 0, 200,
            s => s.Engine.ScrollStartOffset, (s, v) => s.Engine.ScrollStartOffset = v);
        Check(g, "Accelerated scrolling", s => s.Scrolling.Accelerated, (s, v) => s.Scrolling.Accelerated = v);
        Slider(g, "Acceleration", 0, 100, s => s.Scrolling.Acceleration, (s, v) => s.Scrolling.Acceleration = v);
        Combo(g, "Scroll with", new[] { "One finger", "Two fingers", "One or two fingers" },
            s => (int)s.Scrolling.Fingers, (s, v) => s.Scrolling.Fingers = (ScrollFingers)v);

        var d = Group(flow, "Direction & filters");
        Check(d, "No scrolling on mouse movement", s => s.Scrolling.StopOnMouseMove, (s, v) => s.Scrolling.StopOnMouseMove = v);
        Check(d, "Scroll only vertical or horizontal", s => s.Scrolling.OrthogonalOnly, (s, v) => s.Scrolling.OrthogonalOnly = v);
        Check(d, "Inverse vertical scroll direction", s => s.Scrolling.InverseVertical, (s, v) => s.Scrolling.InverseVertical = v);
        Check(d, "Inverse horizontal scroll direction", s => s.Scrolling.InverseHorizontal, (s, v) => s.Scrolling.InverseHorizontal = v);
        Check(d, "No horizontal scrolling", s => s.Scrolling.NoHorizontal, (s, v) => s.Scrolling.NoHorizontal = v);
        return page;
    }

    private TabPage GesturesTab()
    {
        var page = Page("Gestures", out var flow);
        var one = Group(flow, "1 finger gestures");
        Check(one, "Fast swipe left/right to navigate back/forward", s => s.OneFinger.NavigateBackForward, (s, v) => s.OneFinger.NavigateBackForward = v);
        Check(one, "Swap swipe direction", s => s.OneFinger.SwapDirection, (s, v) => s.OneFinger.SwapDirection = v);
        Slider(one, "Swipe sensitivity", 0, 100, s => s.OneFinger.Sensitivity, (s, v) => s.OneFinger.Sensitivity = v);

        var two = Group(flow, "2 finger gestures");
        Check(two, "Fast swipe left/right to change desktops", s => s.TwoFinger.SwitchDesktops, (s, v) => s.TwoFinger.SwitchDesktops = v);
        Check(two, "Swap desktop swipe direction", s => s.TwoFinger.SwapDesktopDirection, (s, v) => s.TwoFinger.SwapDesktopDirection = v);
        Check(two, "Fast swipe up to cycle Normal | Task View", s => s.TwoFinger.SwipeUpTaskView, (s, v) => s.TwoFinger.SwipeUpTaskView = v);
        Check(two, "Fast swipe down to cycle Normal | Desktop View", s => s.TwoFinger.SwipeDownShowDesktop, (s, v) => s.TwoFinger.SwipeDownShowDesktop = v);
        Slider(two, "Swipe sensitivity", 0, 100, s => s.TwoFinger.Sensitivity, (s, v) => s.TwoFinger.Sensitivity = v);
        return page;
    }

    private TabPage ClicksTab()
    {
        var page = Page("Clicks & Taps", out var flow);
        var actions = Enum.GetNames<ClickAction>();

        var c = Group(flow, "Clicks");
        Check(c, "Simultaneous primary + secondary clicks", s => s.Buttons.SimultaneousPrimarySecondary, (s, v) => s.Buttons.SimultaneousPrimarySecondary = v);
        Check(c, "Middle click", s => s.Buttons.MiddleClick, (s, v) => s.Buttons.MiddleClick = v);
        Combo(c, "Middle click by", new[] { "Clicking in the center", "Clicking with two fingers apart" },
            s => (int)s.Buttons.MiddleClickMode, (s, v) => s.Buttons.MiddleClickMode = (MiddleClickMode)v);

        var t1 = Group(flow, "Tap to click: 1 finger");
        Check(t1, "On 1 finger tap", s => s.Taps.OneFingerTap, (s, v) => s.Taps.OneFingerTap = v);
        Check(t1, "Left area", s => s.Taps.LeftZone, (s, v) => s.Taps.LeftZone = v);
        Combo(t1, "  action", actions, s => (int)s.Taps.LeftZoneAction, (s, v) => s.Taps.LeftZoneAction = (ClickAction)v);
        Check(t1, "Middle area", s => s.Taps.MiddleZone, (s, v) => s.Taps.MiddleZone = v);
        Combo(t1, "  action", actions, s => (int)s.Taps.MiddleZoneAction, (s, v) => s.Taps.MiddleZoneAction = (ClickAction)v);
        Check(t1, "Right area", s => s.Taps.RightZone, (s, v) => s.Taps.RightZone = v);
        Combo(t1, "  action", actions, s => (int)s.Taps.RightZoneAction, (s, v) => s.Taps.RightZoneAction = (ClickAction)v);
        Check(t1, "Double tap and drag", s => s.Taps.DoubleTapDrag, (s, v) => s.Taps.DoubleTapDrag = v);

        var tm = Group(flow, "Tap to click: 2 and 3 fingers");
        Check(tm, "On 2 finger tap", s => s.Taps.TwoFingerTap, (s, v) => s.Taps.TwoFingerTap = v);
        Combo(tm, "  action", actions, s => (int)s.Taps.TwoFingerAction, (s, v) => s.Taps.TwoFingerAction = (ClickAction)v);
        Check(tm, "On 3 finger tap", s => s.Taps.ThreeFingerTap, (s, v) => s.Taps.ThreeFingerTap = v);
        Combo(tm, "  action", actions, s => (int)s.Taps.ThreeFingerAction, (s, v) => s.Taps.ThreeFingerAction = (ClickAction)v);

        var f = Group(flow, "Tap filters");
        Check(f, "Only taps on the front area of the mouse", s => s.Taps.UseTapArea, (s, v) => s.Taps.UseTapArea = v);
        Check(f, "Ignore taps on the side and rear edges", s => s.Taps.FilterIgnoreAreas, (s, v) => s.Taps.FilterIgnoreAreas = v);
        Check(f, "Ignore taps right after a real click", s => s.Taps.IgnoreIfRealClick, (s, v) => s.Taps.IgnoreIfRealClick = v);
        return page;
    }

    private TabPage DeviceTab()
    {
        var page = Page("Device", out var flow);
        var info = Group(flow, "Device info");
        info.Controls.Add(_battery);
        info.Controls.Add(_status);
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        row.Controls.Add(Button("Re-enable multitouch", () => _owner.Service.Recover()));
        row.Controls.Add(Button("Refresh battery", () => _owner.RefreshBattery()));
        info.Controls.Add(row);

        var live = Group(flow, "Live touches");
        var cb = new CheckBox { Text = "Show live touches", AutoSize = true };
        cb.CheckedChanged += (_, _) => _live.Visible = cb.Checked;
        live.Controls.Add(cb);
        live.Controls.Add(_live);
        return page;
    }

    private TabPage AppTab()
    {
        var page = Page("App", out var flow);
        var a = Group(flow, "Application");
        Check(a, "Enabled", s => s.Enabled, (s, v) => s.Enabled = v);
        Check(a, "Start with Windows", s => s.AutoStart, (s, v) => s.AutoStart = v);
        Check(a, "Start minimized to the tray", s => s.StartMinimized, (s, v) => s.StartMinimized = v);
        Check(a, "Show tray icon", s => s.ShowTrayIcon, (s, v) => s.ShowTrayIcon = v);

        var b = Group(flow, "Battery alerts");
        Check(b, "Show battery alerts", s => s.Battery.ShowAlerts, (s, v) => s.Battery.ShowAlerts = v);
        Check(b, "Warning alert", s => s.Battery.WarningAlert, (s, v) => s.Battery.WarningAlert = v);
        Slider(b, "Warning level (%)", 5, 50, s => s.Battery.WarningPercent, (s, v) => s.Battery.WarningPercent = v);
        Check(b, "Critical alert", s => s.Battery.CriticalAlert, (s, v) => s.Battery.CriticalAlert = v);
        Slider(b, "Critical level (%)", 1, 25, s => s.Battery.CriticalPercent, (s, v) => s.Battery.CriticalPercent = v);
        Check(b, "Recharged alert", s => s.Battery.RechargedAlert, (s, v) => s.Battery.RechargedAlert = v);
        Slider(b, "Recharged level (%)", 50, 100, s => s.Battery.ChargedPercent, (s, v) => s.Battery.ChargedPercent = v);

        var p = Group(flow, "Power & connection");
        Check(p, "Pause multitouch while the display is off", s => s.Power.PauseOnDisplayOff, (s, v) => s.Power.PauseOnDisplayOff = v);
        Check(p, "Pause multitouch on battery power", s => s.Power.PauseOnBatteryPower, (s, v) => s.Power.PauseOnBatteryPower = v);
        Check(p, "Pause multitouch while locked / logged off", s => s.Power.PauseOnLogOff, (s, v) => s.Power.PauseOnLogOff = v);
        Check(p, "Recover multitouch on wakeup", s => s.Power.RecoverMultitouchOnWakeup, (s, v) => s.Power.RecoverMultitouchOnWakeup = v);
        Check(p, "Bluetooth keep-alive (re-send multitouch mode periodically)", s => s.Power.BluetoothKeepAlive, (s, v) => s.Power.BluetoothKeepAlive = v);
        Slider(p, "Keep-alive period (s)", 1, 30, s => s.Power.BluetoothKeepAlivePeriodMs / 1000, (s, v) => s.Power.BluetoothKeepAlivePeriodMs = v * 1000);

        var adv = Group(flow, "Advanced");
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        row.Controls.Add(Button("Open settings file", () =>
            Process.Start(new ProcessStartInfo(AppSettings.DefaultPath) { UseShellExecute = true })));
        row.Controls.Add(Button("Reset to defaults", () =>
        {
            if (MessageBox.Show(this, "Reset all settings to their defaults?", Text, MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            _s = new AppSettings();
            _owner.ApplySettings(_s.Clone());
            ReloadFromSettings();
        }));
        adv.Controls.Add(row);
        adv.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(520, 0),
            Text = "Gesture thresholds (tap delay, swipe distances, ignore areas...) live in the \"Engine\" section of the settings file.",
        });
        return page;
    }

    // ------------------------------------------------------------------ status / live view

    public void ReloadFromSettings()
    {
        _s = _owner.Settings.Clone();
        _loading = true;
        foreach (var l in _loaders) l();
        _loading = false;
    }

    public void UpdateStatus()
    {
        if (IsDisposed) return;
        var svc = _owner.Service;
        var lines = new List<string>
        {
            svc.State switch
            {
                ServiceState.DriverMissing => "Filter driver not found. Install driver\\MagicTouchFilter (see README).",
                ServiceState.NoDevice => "Driver loaded, no Magic Mouse connected.",
                ServiceState.Paused => "Paused (power settings).",
                _ => "Running.",
            },
        };
        foreach (var d in svc.Devices)
        {
            string model = d.Model switch
            {
                DeviceModel.MagicMouse1 => "Magic Mouse (2009)",
                DeviceModel.MagicMouse2 => "Magic Mouse 2 (2015)",
                DeviceModel.MagicMouse3 => "Magic Mouse (2024, USB-C)",
                _ => "Unknown Apple mouse",
            };
            lines.Add($"{model} via {d.Transport}{(d.BluetoothAddress is { } a ? $" [{a}]" : "")}: " +
                      $"{(d.MultitouchActive ? "multitouch on" : "waiting for multitouch")}, {d.ReportsSeen} reports");
        }
        _status.Text = string.Join(Environment.NewLine, lines);
        var b = _owner.Battery;
        _battery.Text = b == null ? "Battery: unknown" :
            $"Battery: {b.Percent}%{(b.Charging == true ? " (charging)" : "")}  -  {b.Source}";
    }

    private void OnState(ServiceState _)
    {
        if (IsHandleCreated) BeginInvoke(UpdateStatus);
    }

    private long _lastLive;
    private void OnFrame(FilterDevice d, TouchFrame f)
    {
        if (!_live.Visible || !IsHandleCreated) return;
        long now = Environment.TickCount64;
        if (now - _lastLive < 16) return;   // ~60 fps
        _lastLive = now;
        BeginInvoke(() => _live.SetFrame(f));
    }

    // ------------------------------------------------------------------ builders

    private static TabPage Page(string title, out FlowLayoutPanel flow)
    {
        var page = new TabPage(title) { Padding = new Padding(8) };
        flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
        };
        page.Controls.Add(flow);
        return page;
    }

    private static FlowLayoutPanel Group(FlowLayoutPanel parent, string title)
    {
        var box = new GroupBox { Text = title, AutoSize = true, Padding = new Padding(10, 6, 10, 8), MinimumSize = new Size(540, 0) };
        var inner = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        box.Controls.Add(inner);
        parent.Controls.Add(box);
        return inner;
    }

    private void Check(FlowLayoutPanel g, string text, Func<AppSettings, bool> get, Action<AppSettings, bool> set)
    {
        var cb = new CheckBox { Text = text, AutoSize = true };
        cb.CheckedChanged += (_, _) => Change(s => set(s, cb.Checked));
        _loaders.Add(() => cb.Checked = get(_s));
        g.Controls.Add(cb);
    }

    private void Slider(FlowLayoutPanel g, string text, int min, int max, Func<AppSettings, int> get, Action<AppSettings, int> set)
    {
        var label = new Label { AutoSize = true };
        var tb = new TrackBar { Minimum = min, Maximum = max, Width = 380, TickFrequency = Math.Max(1, (max - min) / 10), AutoSize = true };
        void Caption() => label.Text = $"{text}: {tb.Value}";
        tb.ValueChanged += (_, _) => { Caption(); Change(s => set(s, tb.Value)); };
        _loaders.Add(() => { tb.Value = Math.Clamp(get(_s), min, max); Caption(); });
        g.Controls.Add(label);
        g.Controls.Add(tb);
    }

    private void Combo(FlowLayoutPanel g, string text, string[] items, Func<AppSettings, int> get, Action<AppSettings, int> set)
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        cb.Items.AddRange(items);
        cb.SelectedIndexChanged += (_, _) => { if (cb.SelectedIndex >= 0) Change(s => set(s, cb.SelectedIndex)); };
        _loaders.Add(() => cb.SelectedIndex = Math.Clamp(get(_s), 0, items.Length - 1));
        row.Controls.Add(new Label { Text = text, AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        row.Controls.Add(cb);
        g.Controls.Add(row);
    }

    private static Button Button(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void Change(Action<AppSettings> mutate)
    {
        if (_loading) return;
        mutate(_s);
        _owner.ApplySettings(_s.Clone());
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.CloseReason == CloseReason.UserClosing)
        {
            // Keep running in the tray.
            e.Cancel = true;
            Hide();
        }
    }
}
