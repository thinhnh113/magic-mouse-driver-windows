using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MagicMouse.Core.Native;

internal static partial class NativeMethods
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    public const uint OPEN_EXISTING = 3;
    public const int ERROR_OPERATION_ABORTED = 995;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint code, void* inBuf, int inLen,
        void* outBuf, int outLen, out int returned, IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);

    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public const int ERROR_IO_PENDING = 997;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetOverlappedResult(SafeFileHandle handle, NativeOverlapped* ov,
        out int transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    /// <summary>DeviceIoControl on an overlapped handle, waiting for completion.</summary>
    public static unsafe bool IoctlWait(SafeFileHandle h, uint code, void* inBuf, int inLen, void* outBuf, int outLen,
        out int returned)
    {
        using var evt = new ManualResetEvent(false);
        var ov = new NativeOverlapped { EventHandle = evt.SafeWaitHandle.DangerousGetHandle() };
        if (DeviceIoControl(h, code, inBuf, inLen, outBuf, outLen, out returned, (IntPtr)(&ov))) return true;
        if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return false;
        return GetOverlappedResult(h, &ov, out returned, true);
    }

    // ---------------- hid.dll
    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID, ProductID, VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool HidD_GetProductString(SafeFileHandle h, char[] buf, int len);

    // ---------------- cfgmgr32 (device interfaces + properties)
    public const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;
    public const uint CR_SUCCESS = 0;
    public const uint CM_LOCATE_DEVNODE_NORMAL = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_Device_Interface_List_SizeW(out int len, ref Guid cls, string? deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_Device_Interface_ListW(ref Guid cls, string? deviceId, char[] buffer, int len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_Device_Interface_PropertyW(string iface, ref DEVPROPKEY key, out uint type,
        byte[]? buffer, ref int size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type,
        byte[]? buffer, ref int size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_Device_IDW(uint devInst, char[] buffer, int len, uint flags);

    public static readonly DEVPROPKEY DEVPKEY_Device_InstanceId =
        new() { fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), pid = 256 };
    // Battery level that Windows' Bluetooth stack publishes for classic BT HID devices (Win11).
    public static readonly DEVPROPKEY DEVPKEY_Bluetooth_Battery =
        new() { fmtid = new Guid("104ea319-6ee2-4701-bd47-8ddbf425bbe5"), pid = 2 };

    public static List<string> GetInterfaces(Guid cls)
    {
        var result = new List<string>();
        if (CM_Get_Device_Interface_List_SizeW(out int len, ref cls, null, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS || len <= 1)
            return result;
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref cls, null, buf, len, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS)
            return result;
        foreach (var s in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries)) result.Add(s);
        return result;
    }

    public static string? GetInterfaceInstanceId(string iface)
    {
        var key = DEVPKEY_Device_InstanceId;
        int size = 0;
        CM_Get_Device_Interface_PropertyW(iface, ref key, out _, null, ref size, 0);
        if (size <= 0) return null;
        var buf = new byte[size];
        if (CM_Get_Device_Interface_PropertyW(iface, ref key, out _, buf, ref size, 0) != CR_SUCCESS) return null;
        return System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
    }

    // ---------------- user32 input
    public const int INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020,
        MOUSEEVENTF_MIDDLEUP = 0x0040, MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100,
        MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002;
    public const uint XBUTTON1 = 1, XBUTTON2 = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public int type; public INPUTUNION u; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, INPUT[] inputs, int size);

    public const uint SPI_GETMOUSESPEED = 0x0070, SPI_SETMOUSESPEED = 0x0071;
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint action, uint param, IntPtr pv, uint winIni);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint action, uint param, out int pv, uint winIni);
}
