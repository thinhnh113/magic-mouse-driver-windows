using System.Runtime.InteropServices;

namespace MagicMouse.App;

internal static class NativeUi
{
    public static readonly int ShowMessage = RegisterWindowMessage("MagicMouse.Show");
    public static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6fe69556-704a-47a0-8f24-c28d936fda47");
    public const int WM_POWERBROADCAST = 0x0218;
    public const int PBT_POWERSETTINGCHANGE = 0x8013;
    public const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int HWND_BROADCAST = 0xFFFF;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);

    [DllImport("user32.dll")]
    public static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    public static void PostShowMessage() => PostMessage((IntPtr)HWND_BROADCAST, ShowMessage, IntPtr.Zero, IntPtr.Zero);
}
