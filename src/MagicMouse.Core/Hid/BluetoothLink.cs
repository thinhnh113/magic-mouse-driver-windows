using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MagicMouse.Core.Hid;

/// <summary>Drops the Bluetooth connection to a paired device so it can go into deep sleep. A Magic Mouse
/// reconnects by itself when clicked; it stays paired.</summary>
public static class BluetoothLink
{
    // bthioctl.h: BTH_CTL(BTH_IOCTL_BASE + 0x03) = CTL_CODE(FILE_DEVICE_BLUETOOTH, 3, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private const uint IOCTL_BTH_DISCONNECT_DEVICE = 0x0041000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_FIND_RADIO_PARAMS { public int dwSize; }

    [DllImport("bluetoothapis.dll", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS p, out SafeFileHandle radio);

    [DllImport("bluetoothapis.dll", SetLastError = true)]
    private static extern bool BluetoothFindRadioClose(IntPtr find);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref ulong inBuf, int inLen,
        IntPtr outBuf, int outLen, out int returned, IntPtr overlapped);

    /// <summary>Parses a 12-hex-digit address such as "3C5002E57BA8".</summary>
    public static ulong? ParseAddress(string? hex) =>
        hex != null && hex.Length == 12 && ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var a)
            ? a : null;

    /// <summary>Disconnects the device. Returns 0 on success, otherwise the Win32 error code.</summary>
    public static int Disconnect(ulong address)
    {
        var p = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
        IntPtr find = BluetoothFindFirstRadio(ref p, out var radio);
        if (find == IntPtr.Zero) return Marshal.GetLastWin32Error();
        try
        {
            using (radio)
            {
                return DeviceIoControl(radio, IOCTL_BTH_DISCONNECT_DEVICE, ref address, sizeof(ulong),
                    IntPtr.Zero, 0, out _, IntPtr.Zero) ? 0 : Marshal.GetLastWin32Error();
            }
        }
        finally { BluetoothFindRadioClose(find); }
    }
}
