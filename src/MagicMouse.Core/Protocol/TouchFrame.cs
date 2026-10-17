namespace MagicMouse.Core.Protocol;

public enum TouchState : byte
{
    None = 0x00,
    Start = 0x30,
    Drag = 0x40,
    // 0x50..0x70: finger lifting / leaving. Anything not Start/Drag counts as not touching.
}

/// <summary>One finger on the touch surface, in device units.
/// X grows to the right; Y grows toward the user (back of the mouse).</summary>
public readonly record struct Touch(int Id, int X, int Y, int Major, int Minor, int Size, int Orientation, byte RawState)
{
    public bool IsDown => RawState is (byte)TouchState.Start or (byte)TouchState.Drag;
}

[Flags]
public enum PhysicalButtons : byte { None = 0, Left = 1, Right = 2 }

public sealed record TouchFrame(long Timestamp100ns, PhysicalButtons Buttons, int Dx, int Dy, IReadOnlyList<Touch> Touches)
{
    public double TimeMs => Timestamp100ns / 10_000.0;
}

/// <summary>Touch surface extents in device units (observed range shared by MM1/MM2/MM3).</summary>
public static class Surface
{
    public const int MinX = -1100, MaxX = 1258, MinY = -1589, MaxY = 2047;
    public const int Width = MaxX - MinX, Height = MaxY - MinY;
}
