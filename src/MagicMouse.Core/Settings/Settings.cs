using System.Text.Json;
using System.Text.Json.Serialization;

namespace MagicMouse.Core.Settings;

public enum ClickAction { None, LeftClick, RightClick, MiddleClick, DoubleClick, Back, Forward }
public enum ScrollFingers { One, Two, OneOrTwo }
public enum MiddleClickMode
{
    /// <summary>Physical click with the finger in the middle third of the surface.</summary>
    CenterZone,
    /// <summary>Physical click with two fingers spread wide apart.</summary>
    TwoFingersApart,
}
public enum DesktopSwitchAnimation { Keyboard }

public sealed class ButtonSettings
{
    public bool SwapButtons { get; set; }
    public bool LeftHanded { get; set; }
    /// <summary>Use <see cref="LeftRightSplitPercent"/> instead of the mouse's own left/right decision.</summary>
    public bool CustomSplit { get; set; }
    /// <summary>Position of the left/right split as % of surface width (from the left).</summary>
    public int LeftRightSplitPercent { get; set; } = 55;
    public bool SimultaneousPrimarySecondary { get; set; }
    public bool MiddleClick { get; set; }
    public MiddleClickMode MiddleClickMode { get; set; } = MiddleClickMode.CenterZone;
}

public sealed class PointerSettings
{
    /// <summary>Windows pointer speed 1..20 (0 = leave unchanged).</summary>
    public int PointerSpeed { get; set; }
    /// <summary>Ignore touch gestures while the mouse is gripped by both sides (lifting / repositioning it).</summary>
    public bool IgnoreInputIfLifted { get; set; }
}

public sealed class ScrollSettings
{
    public bool Enabled { get; set; } = true;
    public bool Smooth { get; set; } = true;
    /// <summary>0..100.</summary>
    public int Speed { get; set; } = 61;
    public bool Accelerated { get; set; } = true;
    /// <summary>0..100, strength of the acceleration curve.</summary>
    public int Acceleration { get; set; } = 50;
    public bool Momentum { get; set; } = true;
    public bool InverseVertical { get; set; }
    public bool InverseHorizontal { get; set; }
    public bool OrthogonalOnly { get; set; } = true;
    public bool NoHorizontal { get; set; }
    public bool StopOnMouseMove { get; set; }
    public ScrollFingers Fingers { get; set; } = ScrollFingers.One;
}

public sealed class OneFingerSwipeSettings
{
    public bool NavigateBackForward { get; set; } = true;
    /// <summary>Default: finger moving right = Back (like macOS). Swapped: finger right = Forward.</summary>
    public bool SwapDirection { get; set; }
    /// <summary>0..100, higher = shorter/slower swipe triggers.</summary>
    public int Sensitivity { get; set; } = 50;
}

public sealed class TwoFingerSwipeSettings
{
    public bool SwitchDesktops { get; set; }
    public bool SwapDesktopDirection { get; set; }
    public DesktopSwitchAnimation Animation { get; set; } = DesktopSwitchAnimation.Keyboard;
    public bool SwipeUpTaskView { get; set; }
    public bool SwipeDownShowDesktop { get; set; }
    public int Sensitivity { get; set; } = 50;
}

public sealed class TapSettings
{
    public bool OneFingerTap { get; set; }
    public bool LeftZone { get; set; } = true;
    public ClickAction LeftZoneAction { get; set; } = ClickAction.LeftClick;
    public bool MiddleZone { get; set; }
    public ClickAction MiddleZoneAction { get; set; } = ClickAction.MiddleClick;
    public bool RightZone { get; set; } = true;
    public ClickAction RightZoneAction { get; set; } = ClickAction.RightClick;
    /// <summary>Only taps on the front part of the surface count.</summary>
    public bool UseTapArea { get; set; } = true;
    public bool FilterIgnoreAreas { get; set; } = true;
    public bool IgnoreIfRealClick { get; set; }
    public bool DoubleTapDrag { get; set; }

    public bool TwoFingerTap { get; set; }
    public ClickAction TwoFingerAction { get; set; } = ClickAction.RightClick;
    public bool ThreeFingerTap { get; set; }
    public ClickAction ThreeFingerAction { get; set; } = ClickAction.MiddleClick;
}

/// <summary>Gesture thresholds in device units / ms. Defaults match the reference app.</summary>
public sealed class EngineSettings
{
    public int SwipeHorizontalOffset { get; set; } = 100;
    public int SwipeVerticalOffset { get; set; } = 45;
    public int Swipe2HorizontalOffset { get; set; } = 35;
    public int Swipe2VerticalOffset { get; set; } = 40;
    public int SwipeMaxDurationMs { get; set; } = 250;
    public int ScrollStepFactor { get; set; } = 120;
    /// <summary>Scroll dead zone: finger travel (device units, ~0.025 mm each) before scrolling starts.</summary>
    public int ScrollStartOffset { get; set; } = 30;
    /// <summary>Movement during the first ms of a touch is ignored (the contact patch settles as the finger lands).</summary>
    public int TouchSettleMs { get; set; } = 40;
    /// <summary>Finger travel (device units, ~0.025 mm each) before the scroll axis is locked.</summary>
    public int AxisLockDistance { get; set; } = 40;
    /// <summary>Horizontal wins only if sideways motion is this many times the vertical motion.</summary>
    public double HorizontalLockRatio { get; set; } = 2.0;
    public int TapDelayMs { get; set; } = 200;
    public int TapOffsetAllowed { get; set; } = 80;
    public int DoubleTapDragMinDelayMs { get; set; } = 100;
    public int DoubleTapDragMaxDelayMs { get; set; } = 200;
    public int BlockTimeAfterResetMs { get; set; } = 80;
    public int SwipeNavigationBlockMs { get; set; } = 300;
    public int SwipeOtherBlockMs { get; set; } = 300;
    public int IgnoreSideTouchesLeft { get; set; } = 150;
    public int IgnoreSideTouchesRight { get; set; } = 150;
    public int IgnoreBottomTouches { get; set; } = 900;
    public int TapAreaFromTop { get; set; } = 1600;
    public int MiddleClickZoneStartPercent { get; set; } = 33;
    public int MiddleClickZoneEndPercent { get; set; } = 67;
    public int MiddleClickTwoFingerMinDistance { get; set; } = 1228;
    public int StopScrollMoveOffset { get; set; } = 2;
    public double MomentumDecayPerSecond { get; set; } = 4.0;
    public int IgnoreRealClickTapWindowMs { get; set; } = 300;
}

public sealed class PowerSettings
{
    public bool PauseOnDisplayOff { get; set; } = true;
    public bool PauseOnBatteryPower { get; set; }
    public bool PauseOnLogOff { get; set; }
    public bool RecoverMultitouchOnWakeup { get; set; } = true;
    public bool BluetoothKeepAlive { get; set; }
    public int BluetoothKeepAlivePeriodMs { get; set; } = 2000;
}

public sealed class BatterySettings
{
    public bool ShowAlerts { get; set; } = true;
    public bool WarningAlert { get; set; } = true;
    public bool CriticalAlert { get; set; } = true;
    public bool RechargedAlert { get; set; }
    public int ChargedPercent { get; set; } = 80;
    public int WarningPercent { get; set; } = 10;
    public int CriticalPercent { get; set; } = 5;
}

public sealed class AppSettings
{
    public bool Enabled { get; set; } = true;
    public bool AutoStart { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;

    public ButtonSettings Buttons { get; set; } = new();
    public PointerSettings Pointer { get; set; } = new();
    public ScrollSettings Scrolling { get; set; } = new();
    public OneFingerSwipeSettings OneFinger { get; set; } = new();
    public TwoFingerSwipeSettings TwoFinger { get; set; } = new();
    public TapSettings Taps { get; set; } = new();
    public EngineSettings Engine { get; set; } = new();
    public PowerSettings Power { get; set; } = new();
    public BatterySettings Battery { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static string DefaultPath => Path.Combine(AppData, "MagicMouse", "settings.json");
    // The app was called MagicTouch before; its settings are migrated on first load.
    private static string LegacyPath => Path.Combine(AppData, "MagicTouch", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        if (path == null && !File.Exists(DefaultPath) && File.Exists(LegacyPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath)!);
            File.Copy(LegacyPath, DefaultPath);
        }
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        }
        catch (JsonException) { /* corrupt file: fall back to defaults, keep the bad file for inspection */ }
        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json), Json)!;

    /// <summary>Whether the app must own button generation (driver masks the physical buttons).</summary>
    [JsonIgnore]
    public bool AppHandlesButtons =>
        Buttons.SwapButtons || Buttons.LeftHanded || Buttons.CustomSplit ||
        Buttons.SimultaneousPrimarySecondary || Buttons.MiddleClick || Taps.DoubleTapDrag;
}
