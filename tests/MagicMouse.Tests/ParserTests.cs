using MagicMouse.Core.Driver;
using MagicMouse.Core.Hid;
using MagicMouse.Core.Protocol;
using Xunit;

namespace MagicMouse.Tests;

public class ParserTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 400)]
    [InlineData(-800, -1200)]
    [InlineData(-128, -1475)]
    [InlineData(1258, 2047)]
    [InlineData(-1100, -1589)]
    [InlineData(1000, 1500)]
    public void Mm2_round_trips_touch_coordinates(int x, int y)
    {
        var t = new Touch(Id: 5, X: x, Y: y, Major: 40, Minor: 30, Size: 12, Orientation: 3, RawState: 0x40);
        var bytes = TouchReportParser.EncodeMM2(PhysicalButtons.Left, -7, 12, new[] { t });

        var f = TouchReportParser.Parse(DeviceModel.MagicMouse3, bytes, 0)!;

        Assert.Equal(PhysicalButtons.Left, f.Buttons);
        Assert.Equal(-7, f.Dx);
        Assert.Equal(12, f.Dy);
        var p = Assert.Single(f.Touches);
        Assert.Equal(5, p.Id);
        Assert.Equal(x, p.X);
        Assert.Equal(y, p.Y);
        Assert.Equal(40, p.Major);
        Assert.Equal(30, p.Minor);
        Assert.Equal(12, p.Size);
        Assert.Equal(3, p.Orientation);
        Assert.True(p.IsDown);
    }

    [Fact]
    public void Mm2_header_only_report_has_no_touches()
    {
        var bytes = TouchReportParser.EncodeMM2(PhysicalButtons.None, 3, -4, Array.Empty<Touch>());
        var f = TouchReportParser.Parse(DeviceModel.MagicMouse2, bytes, 0)!;
        Assert.Empty(f.Touches);
        Assert.Equal(3, f.Dx);
        Assert.Equal(-4, f.Dy);
    }

    [Fact]
    public void Plain_mouse_report_is_not_a_touch_frame()
    {
        // 8-byte report 0x12 with no touch header: too short.
        Assert.Null(TouchReportParser.Parse(DeviceModel.MagicMouse3, new byte[] { 0x12, 1, 2, 0, 3, 0, 0, 0 }, 0));
    }

    [Fact]
    public void Mm1_decodes_header_motion_and_touch()
    {
        // dx = -3 (10-bit), dy = +5; buttons = left.
        int dx = -3 & 0x3FF, dy = 5;
        var d = new byte[6 + 8];
        d[0] = 0x29;
        d[1] = (byte)dx;
        d[2] = (byte)dy;
        d[3] = (byte)(0x01 | ((dx >> 8) & 0x03) << 2 | ((dy >> 8) & 0x03) << 4);
        // touch: x = 200, y = -(raw) where raw = 0x100 -> y = -256
        int x = 200, yraw = 0x100;
        d[6] = (byte)x;
        d[7] = (byte)((x >> 8) & 0x0F | (yraw << 4) & 0xF0);
        d[8] = (byte)(yraw >> 4);
        d[13] = 0x30;

        var f = TouchReportParser.Parse(DeviceModel.MagicMouse1, d, 0)!;

        Assert.Equal(-3, f.Dx);
        Assert.Equal(5, f.Dy);
        Assert.Equal(PhysicalButtons.Left, f.Buttons);
        var t = Assert.Single(f.Touches);
        Assert.Equal(200, t.X);
        Assert.Equal(-256, t.Y);
        Assert.True(t.IsDown);
    }

    [Theory]
    [InlineData(0x00, 85, 85, false)]
    [InlineData(0x02, 40, 40, true)]
    [InlineData(0x00, 255, 100, false)]
    public void Battery_report_decodes(byte flags, byte level, int expected, bool charging)
    {
        var s = BatteryReader.Decode(new byte[] { 0x90, flags, level })!;
        Assert.Equal(expected, s.Percent);
        Assert.Equal(charging, s.Charging);
    }

    [Fact]
    public void All_zero_battery_report_is_unknown_not_empty()
    {
        // In multitouch mode the mouse answers GET_REPORT(0x90) with zeros.
        Assert.Null(BatteryReader.Decode(new byte[] { 0x90, 0x00, 0x00 }));
    }

    [Fact]
    public void Driver_cached_battery_is_preferred()
    {
        var cached = new FilterBattery(3, new byte[] { 0x90, 0, 0, 0 }, new byte[] { 0x90, 0x00, 73, 0 });
        var s = BatteryReader.Read("000000000000", cached)!;
        Assert.Equal(73, s.Percent);
    }

    [Fact]
    public void Bluetooth_address_parsed_from_instance_id()
    {
        var d = new FilterDevice(0, DeviceModel.MagicMouse3, Transport.Bluetooth, 0x05AC, 0x0323, true, 0,
            @"BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&0323\7&4AF2E5&0&3C5002E57BA8_C00000000");
        Assert.Equal("3C5002E57BA8", d.BluetoothAddress);
    }
}


