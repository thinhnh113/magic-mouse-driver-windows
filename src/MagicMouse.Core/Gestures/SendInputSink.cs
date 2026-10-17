using System.Runtime.InteropServices;
using MagicMouse.Core.Settings;
using static MagicMouse.Core.Native.NativeMethods;

namespace MagicMouse.Core.Gestures;

/// <summary>Injects gesture output into Windows with SendInput.</summary>
public sealed class SendInputSink : IInputSink
{
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    public void Scroll(int vertical, int horizontal)
    {
        var list = new List<INPUT>(2);
        if (vertical != 0) list.Add(Mouse(MOUSEEVENTF_WHEEL, (uint)vertical));
        if (horizontal != 0) list.Add(Mouse(MOUSEEVENTF_HWHEEL, (uint)horizontal));
        Send(list.ToArray());
    }

    public void Button(MouseButton button, bool down) => Send(Mouse(button switch
    {
        MouseButton.Left => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
        MouseButton.Right => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
        _ => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
    }, 0));

    public void Click(ClickAction action)
    {
        switch (action)
        {
            case ClickAction.LeftClick: Button(MouseButton.Left, true); Button(MouseButton.Left, false); break;
            case ClickAction.RightClick: Button(MouseButton.Right, true); Button(MouseButton.Right, false); break;
            case ClickAction.MiddleClick: Button(MouseButton.Middle, true); Button(MouseButton.Middle, false); break;
            case ClickAction.DoubleClick: Click(ClickAction.LeftClick); Click(ClickAction.LeftClick); break;
            // X buttons work in browsers, Explorer and most apps; better than VK_BROWSER_BACK.
            case ClickAction.Back: Send(Mouse(MOUSEEVENTF_XDOWN, XBUTTON1), Mouse(MOUSEEVENTF_XUP, XBUTTON1)); break;
            case ClickAction.Forward: Send(Mouse(MOUSEEVENTF_XDOWN, XBUTTON2), Mouse(MOUSEEVENTF_XUP, XBUTTON2)); break;
        }
    }

    public void Chord(params VKey[] keys)
    {
        var list = new List<INPUT>(keys.Length * 2);
        foreach (var k in keys) list.Add(Key(k, false));
        for (int i = keys.Length - 1; i >= 0; i--) list.Add(Key(keys[i], true));
        Send(list.ToArray());
    }

    public void Move(int dx, int dy) => Send(Mouse(MOUSEEVENTF_MOVE, 0, dx, dy));

    private static INPUT Mouse(uint flags, uint data, int dx = 0, int dy = 0) => new()
    {
        type = INPUT_MOUSE,
        u = new INPUTUNION { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } },
    };

    private static INPUT Key(VKey k, bool up)
    {
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        if (k is VKey.Left or VKey.Right or VKey.LWin) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = (ushort)k, dwFlags = flags } } };
    }

    private static void Send(params INPUT[] inputs)
    {
        if (inputs.Length > 0) SendInput((uint)inputs.Length, inputs, InputSize);
    }
}
