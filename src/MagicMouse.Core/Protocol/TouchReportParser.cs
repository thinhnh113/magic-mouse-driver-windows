using MagicMouse.Core.Driver;

namespace MagicMouse.Core.Protocol;

/// <summary>Decodes Magic Mouse multitouch input reports (Data[0] = report id).</summary>
public static class TouchReportParser
{
    public const byte ReportMM1 = 0x29;
    public const byte ReportMM2 = 0x12;
    private const int TouchBlock = 8;
    private const int HeaderMM1 = 6;
    private const int HeaderMM2 = 14;

    public static TouchFrame? Parse(DeviceModel model, ReadOnlySpan<byte> data, long timestamp100ns)
    {
        if (data.Length < 2) return null;
        return data[0] switch
        {
            ReportMM1 when model is DeviceModel.MagicMouse1 or DeviceModel.Unknown => ParseMM1(data, timestamp100ns),
            ReportMM2 when model is not DeviceModel.MagicMouse1 => ParseMM2(data, timestamp100ns),
            _ => null,
        };
    }

    private static TouchFrame? ParseMM2(ReadOnlySpan<byte> d, long ts)
    {
        if (d.Length < HeaderMM2) return null;
        int dx = (short)(d[2] | d[3] << 8);
        int dy = (short)(d[4] | d[5] << 8);
        var buttons = (PhysicalButtons)(d[1] & 0x03);
        int count = (d.Length - HeaderMM2) / TouchBlock;
        var touches = new List<Touch>(count);
        // Same 12-bit touch block as MM1 (verified against a live MM3 capture: decoding as 13-bit
        // made X wrap by 4096 and doubled Y).
        for (int i = 0; i < count; i++) touches.Add(DecodeBlock(d.Slice(HeaderMM2 + i * TouchBlock, TouchBlock)));
        return new TouchFrame(ts, buttons, dx, dy, touches);
    }

    private static TouchFrame? ParseMM1(ReadOnlySpan<byte> d, long ts)
    {
        if (d.Length < HeaderMM1) return null;
        int dx = ((d[3] & 0x0C) << 28 | d[1] << 22) >> 22;
        int dy = ((d[3] & 0x30) << 26 | d[2] << 22) >> 22;
        var buttons = (PhysicalButtons)(d[3] & 0x03);
        int count = (d.Length - HeaderMM1) / TouchBlock;
        var touches = new List<Touch>(count);
        for (int i = 0; i < count; i++) touches.Add(DecodeBlock(d.Slice(HeaderMM1 + i * TouchBlock, TouchBlock)));
        return new TouchFrame(ts, buttons, dx, dy, touches);
    }

    // x = t0 + low nibble of t1 (12-bit signed); y = high nibble of t1 + t2 (12-bit signed, negated).
    private static Touch DecodeBlock(ReadOnlySpan<byte> t) => new(
        Id: (t[6] << 2 | t[5] >> 6) & 0x0F,
        X: (t[1] << 28 | t[0] << 20) >> 20,
        Y: -((t[2] << 24 | t[1] << 16) >> 20),
        Major: t[3],
        Minor: t[4],
        Size: t[5] & 0x3F,
        Orientation: (t[6] >> 2) - 32,
        RawState: (byte)(t[7] & 0xF0));

    /// <summary>Inverse of the MM2 decoder, used by tests and the probe's self-check.</summary>
    public static byte[] EncodeMM2(PhysicalButtons buttons, short dx, short dy, IEnumerable<Touch> touches)
    {
        var list = touches.ToList();
        var d = new byte[HeaderMM2 + list.Count * TouchBlock];
        d[0] = ReportMM2;
        d[1] = (byte)buttons;
        d[2] = (byte)dx; d[3] = (byte)(dx >> 8);
        d[4] = (byte)dy; d[5] = (byte)(dy >> 8);
        for (int i = 0; i < list.Count; i++)
        {
            var t = list[i];
            int o = HeaderMM2 + i * TouchBlock;
            int x = t.X & 0xFFF;
            int y = -t.Y & 0xFFF;
            d[o + 0] = (byte)x;
            d[o + 1] = (byte)((x >> 8) & 0x0F | (y << 4) & 0xF0);
            d[o + 2] = (byte)(y >> 4);
            d[o + 3] = (byte)t.Major;
            d[o + 4] = (byte)t.Minor;
            d[o + 5] = (byte)((t.Size & 0x3F) | (t.Id & 0x03) << 6);
            d[o + 6] = (byte)((t.Id >> 2) & 0x03 | ((t.Orientation + 32) << 2) & 0xFC);
            d[o + 7] = t.RawState;
        }
        return d;
    }
}
