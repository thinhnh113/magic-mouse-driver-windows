using MagicMouse.Core.Settings;

namespace MagicMouse.Core.Gestures;

public enum MouseButton { Left, Right, Middle }

/// <summary>Virtual-key codes used by gestures.</summary>
public enum VKey : ushort
{
    Tab = 0x09, Escape = 0x1B, Left = 0x25, Right = 0x27, D = 0x44,
    LWin = 0x5B, Control = 0x11, BrowserBack = 0xA6, BrowserForward = 0xA7,
}

/// <summary>Where the gesture engine sends its output (SendInput in the app, a recorder in tests).</summary>
public interface IInputSink
{
    /// <summary>Wheel units (120 = one notch). Positive vertical scrolls up; positive horizontal scrolls right.</summary>
    void Scroll(int vertical, int horizontal);
    void Button(MouseButton button, bool down);
    void Click(ClickAction action);
    /// <summary>Press keys in order, release in reverse.</summary>
    void Chord(params VKey[] keys);
    void Move(int dx, int dy);
}
