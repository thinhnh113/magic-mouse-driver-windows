using MagicMouse.Core.Native;
using Microsoft.Win32;

namespace MagicMouse.App;

internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "MagicMouse";
    private const string LegacyName = "MagicTouch";   // the app's earlier name

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key == null) return;
        key.DeleteValue(LegacyName, throwOnMissingValue: false);
        if (enabled) key.SetValue(Name, $"\"{Environment.ProcessPath}\" --minimized");
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }
}

internal static class PointerSpeed
{
    private const uint SPIF_UPDATEINIFILE = 1, SPIF_SENDCHANGE = 2;

    public static int Get() => NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETMOUSESPEED, 0, out int v, 0) ? v : 10;

    /// <summary>Windows pointer speed 1..20 (system-wide).</summary>
    public static void Set(int speed) =>
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSESPEED, 0, (IntPtr)Math.Clamp(speed, 1, 20),
            SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
}
