using System.Runtime.InteropServices;

namespace MagicMouse.Core.Driver;

// Mirrors driver/MagicTouchFilter/Public.h.
public static class FilterProtocol
{
    public const string UserPath = @"\\.\MagicTouchFilter";
    public const int ProtocolVersion = 3;
    public const int MaxDevices = 8;
    public const int MaxReport = 128;

    private static uint Ctl(uint fn) => (0x22u << 16) | (0u << 14) | ((0x800u + fn) << 2) | 0u; // FILE_DEVICE_UNKNOWN, ANY_ACCESS, BUFFERED

    public static readonly uint IoctlGetDevices = Ctl(0);
    public static readonly uint IoctlSetConfig = Ctl(1);
    public static readonly uint IoctlReadReport = Ctl(2);
    public static readonly uint IoctlSendFeature = Ctl(3);
}

public enum DeviceModel : uint { Unknown = 0, MagicMouse1 = 1, MagicMouse2 = 2, MagicMouse3 = 3 }
public enum Transport : uint { Unknown = 0, Bluetooth = 1, Usb = 2 }

[Flags]
public enum FilterFlags : uint
{
    None = 0,
    Multitouch = 1,
    SuppressButtons = 2,
    SuppressMotion = 4,
    KeepAlive = 8,
}

[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
internal unsafe struct MmfDeviceInfo
{
    public uint Index, Model, Transport;
    public ushort VendorId, ProductId;
    public uint Connected, ReportsSeen;
    public fixed char InstanceId[128];
    public uint BrbCount, InputReports, ChannelOpens, ControlKnown, InterruptKnown, ModeSwitchSends;
    public int LastSendStatus;
    public fixed byte LastReport[16];
    public uint LastReportLength;
    public fixed byte LastRaw[16];
    public uint LastRawLength, MaxRawLength;
    // v3
    public uint BatteryReports;
    public fixed byte LastBattery[4];
    public fixed byte GoodBattery[4];
    public long GoodBatteryTime;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct MmfDeviceList
{
    public uint ProtocolVersion, Count;
    public MmfDeviceInfo D0, D1, D2, D3, D4, D5, D6, D7;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct MmfConfig
{
    public uint Index, Flags, KeepAlivePeriodMs;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct MmfReport
{
    public uint Index, Length;
    public long Timestamp;
    public fixed byte Data[FilterProtocol.MaxReport];
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct MmfFeature
{
    public uint Index, Length;
    public fixed byte Data[64];
}

/// <summary>Driver-side counters for troubleshooting (protocol v2).</summary>
public sealed record FilterDiagnostics(uint Requests, uint InputReports, uint ChannelOpens, bool ControlChannelKnown,
    bool InterruptChannelKnown, uint ModeSwitchSends, int LastSendStatus, byte[] LastReport,
    byte[] LastRaw, uint LastRawLength, uint MaxRawLength)
{
    public string LastSendStatusText => LastSendStatus switch
    {
        0 => "success",
        0x103 => "pending",
        unchecked((int)0xC00000A3) => "device not ready (control channel unknown)",
        unchecked((int)0xC00002B9) => "no Bluetooth profile interface",
        _ => $"0x{LastSendStatus:X8}",
    };
}

/// <summary>Battery report 0x90 as cached by the driver (protocol v3).</summary>
public sealed record FilterBattery(uint ReportsSeen, byte[] Last, byte[]? Good);

public sealed record FilterDevice(int Index, DeviceModel Model, Transport Transport, ushort VendorId,
    ushort ProductId, bool MultitouchActive, uint ReportsSeen, string InstanceId, FilterDiagnostics? Diagnostics = null,
    FilterBattery? Battery = null)
{
    /// <summary>Bluetooth address (12 hex chars) parsed from the BTHENUM instance id, if present.</summary>
    public string? BluetoothAddress
    {
        get
        {
            var tail = InstanceId.Split('\\').LastOrDefault() ?? "";
            var parts = tail.Split('&', '_');
            return parts.FirstOrDefault(p => p.Length == 12 && p.All(Uri.IsHexDigit));
        }
    }
}

public readonly record struct RawReport(int DeviceIndex, long Timestamp100ns, byte[] Data);
