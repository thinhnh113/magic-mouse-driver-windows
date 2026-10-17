using MagicMouse.Core.Native;

namespace MagicMouse.Core.Hid;

public sealed record BatteryStatus(int Percent, bool? Charging, string Source);

/// <summary>Reads the Magic Mouse battery level from report 0x90 = [0x90, flags (bit1 charging), level].
/// 1. The value cached by MagicTouchFilter. In multitouch mode the mouse answers GET_REPORT(0x90) with
///    zeros, so the driver keeps the last real report it saw (unsolicited updates or earlier replies).
/// 2. GET_REPORT on the non-exclusive vendor collection (UsagePage 0xFF00, Usage 0x14).
/// 3. The battery property Windows' Bluetooth stack publishes on the BTHENUM devnode.</summary>
public static class BatteryReader
{
    public const byte BatteryReportId = 0x90;

    public static BatteryStatus? Read(string? bluetoothAddress = null, Driver.FilterBattery? cached = null)
    {
        if (cached?.Good is { } good && Decode(good) is { } fromDriver)
            return fromDriver with { Source = "MagicMouse driver (last report)" };

        foreach (var c in HidDevices.Enumerate(HidDevices.IsMagicMouse))
        {
            if (c.UsagePage != 0xFF00 || c.Usage != 0x14 || c.InputLength < 3) continue;
            if (bluetoothAddress != null && c.InstanceId != null &&
                !c.InstanceId.Contains(bluetoothAddress, StringComparison.OrdinalIgnoreCase) &&
                c.InstanceId.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)) continue;
            var s = ReadHid(c.Path, c.InputLength);
            if (s != null) return s;
        }
        return ReadDevProperty(bluetoothAddress);
    }

    public static BatteryStatus? ReadHid(string path, int inputLength)
    {
        using var h = HidDevices.Open(path, readWrite: true);
        if (h.IsInvalid) return null;
        var buf = new byte[Math.Max(inputLength, 3)];
        buf[0] = BatteryReportId;
        if (!NativeMethods.HidD_GetInputReport(h, buf, buf.Length)) return null;
        return Decode(buf);
    }

    public static BatteryStatus? Decode(ReadOnlySpan<byte> report)
    {
        if (report.Length < 3 || report[0] != BatteryReportId) return null;
        // All-zero = the mouse didn't really answer (multitouch mode), not an empty battery.
        if (report[1] == 0 && report[2] == 0) return null;
        int level = report[2];
        int percent = level <= 100 ? level : level * 100 / 255;
        return new BatteryStatus(percent, (report[1] & 0x02) != 0, "HID report 0x90");
    }

    private static BatteryStatus? ReadDevProperty(string? bluetoothAddress)
    {
        foreach (var c in HidDevices.Enumerate(HidDevices.IsMagicMouse))
        {
            if (c.InstanceId == null) continue;
            if (bluetoothAddress != null && !c.InstanceId.Contains(bluetoothAddress, StringComparison.OrdinalIgnoreCase))
                continue;
            if (NativeMethods.CM_Locate_DevNodeW(out uint inst, c.InstanceId, 0) != NativeMethods.CR_SUCCESS) continue;
            // Walk up from the HID collection to the BTHENUM devnode.
            for (int depth = 0; depth < 3; depth++)
            {
                var key = NativeMethods.DEVPKEY_Bluetooth_Battery;
                int size = 1;
                var b = new byte[1];
                if (NativeMethods.CM_Get_DevNode_PropertyW(inst, ref key, out _, b, ref size, 0) == NativeMethods.CR_SUCCESS)
                    return new BatteryStatus(b[0], null, "Windows Bluetooth");
                if (NativeMethods.CM_Get_Parent(out inst, inst, 0) != NativeMethods.CR_SUCCESS) break;
            }
        }
        return null;
    }
}
