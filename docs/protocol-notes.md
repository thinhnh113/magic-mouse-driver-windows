# Protocol & feature notes

Sources: the installed Magic Mouse Utilities 3.1.5.6 (registry settings, INF, driver/service strings, UI Automation tree), the Bluetooth SDP cache on this machine, and public protocol facts about Apple Magic Mouse reports. No vendor code was decompiled or copied. Their app and service are Themida-packed, and their driver is obfuscated.

## Devices (from MagicMouse.inf)
| Model | BT hardware ID | USB |
|---|---|---|
| Magic Mouse 2009 (MM1) | `BTHENUM\{00001124-...}_VID&000205ac_PID&030d` / `PID&0310` | – |
| Magic Mouse 2015 (MM2) | `..._VID&0001004c_PID&0269` | `USB\Vid_05ac&Pid_0269&MI_01` |
| Magic Mouse 2024 (MM3, USB-C) | `..._VID&0001004c_PID&0323` | `USB\Vid_05ac&Pid_0323&MI_01` |

The vendor driver installs as a KMDF **LowerFilter** below hidbth / hidusb. It creates a raw PDO `{7D55502A-2C87-441F-9993-0761990E0C7A}\MagicMouseRawPdo`. App-to-driver traffic is encrypted with BCrypt, keyed off the MachineGuid. That channel is proprietary, so we use our own.

## Stock MM3 HID descriptor (from SDP cache, attribute 0x0206)
- **Report 0x12, input:** mouse with 2 buttons, X/Y as 16-bit relative values in the range -2047..2047, plus 2 padding bytes. Total size is 8 bytes including the ID.
- **Report 0x55, feature:** 64 bytes, vendor page 0xFF02, usage 0x55.
- **Report 0x90, input:** in its own TLC (UsagePage 0xFF00, Usage 0x14). It carries 3 flag bits (Power page: 0x61, 0x44 Charging, 0x46) and a 1-byte AbsoluteStateOfCharge (0x65, 0..255). **This TLC isn't exclusive, so user mode can read battery directly.**
- Feature report 0xF1 (multitouch enable) is **not declared**, so `HidD_SetFeature` rejects it.
- Touch frames arrive as a longer report 0x12 on the exclusive mouse TLC.
- **Conclusion: a filter driver is required.** It sends the mode switch and diverts touch frames to user mode.

## Mode switch (public facts)
- MM1: feature report `D7 01`.
- MM2/MM3: feature report `F1 02 01`.
- Over BT, send it as a HID SET_REPORT (feature) transaction, header `0x53`, on the control channel.
- Must be re-sent after reconnect or resume.

## Touch report layout (public facts)
**MM2/MM3, report 0x12:**
- Header: `[0]=0x12`, `[1]=buttons` (bit0 left, bit1 right), `[2..3]=dx`, `[4..5]=dy` (int16 LE).
- Touches start at offset 14, 8 bytes each.

**MM1, report 0x29:**
- Header: `[1]` = dx low, `[2]` = dy low, `[3]` = buttons plus the high bits of dx/dy. dx = sign-extended 10 bits from `((d3&0x0C)<<6)|d1`.
- Touches start at offset 6, 8 bytes each.

**Per-touch block `t[0..7]`:**
- `id = ((t6<<2)|(t5>>6)) & 0xF`
- `x`: 12-bit signed `(int)(t1<<28 | t0<<20) >> 20` for all models. This was **verified on a live MM3 capture**, where decoding as 13-bit made X wrap by 4096.
- `y`: 12-bit signed `-((int)(t2<<24 | t1<<16) >> 20)`.
- **Live capture (MM3, BT, 2026-10-17):**
  - Frames arrive at about 90 Hz.
  - States go 0x10/0x20 (approach), 0x30 (start), 0x40 (drag), 0x50/0x60 (lifting), 0x70 (lifted), then 0x00.
  - The mode switch `F1 02 01` works on the control channel.
  - hidbth's input read buffer is too small for touch frames, so the driver substitutes a 256-byte buffer.
- `major=t3`, `minor=t4`, `size=t5&0x3F`, `orientation=(t6>>2)-32`.
- `state=t7&0xF0`: 0x30 = start, 0x40 = drag, 0x50/0x60/0x70 = lifting. Anything else counts as not touching.
- Surface range is about X -1100..1258 and Y -1589..2047.

## Settings schema of the original app (registry, HKCU\Software\MagicUtilities\MagicMouse\Devices\<dev>)
- **UserSettings:**
  - buttons: swap buttons;
  - pointer: base pointer speed;
  - scrolling: on/off, smooth, inverse V/H, orthogonal-only, no horizontal, accelerated, speed (0..100), stop on mouse move, scroll fingers;
  - swipes: 1-finger swipe navigate left/right with swap; 2-finger swipe left/right/up/down;
  - clicks: simultaneous primary+secondary, middle click and its kind;
  - tap to click: 1-finger tap left/middle/right zones with kinds, ignore real click, filter ignore areas, use tap area, double-tap drag; 2- and 3-finger tap with kinds.
- **EngineSettings (thresholds):**
  - `SwipeHoriOffset=100`, `SwipeVertiOffset=45`, `ScrollStepFactor=120`, `ScrollHori/VertiOffset=136`;
  - `TapDelay=200ms`, `TapOffsetAllowed=80`, `DoubleTapDrag 100..200ms`;
  - `BlockTimeAfterReset=80`, `SwipeNaviBlockTime=300`, `SwipeOtherBlockTime=300`;
  - `IgnoreSideTouches L/R=150` (W23=80), `IgnoreBottomTouches=900`, `TapToClickFromTop=1600`;
  - `MiddleClick1Start=819`, `MiddleClick1End=1638`, `MiddleClick2MinDistance=1228`;
  - `Swipe2Hori/VertiOffset=35/40`, `StopScrollMoveOffset=2`.
- **DeviceSettings:** live touches, ignore input if lifted, left handed, left/right button split=55%.
- **DeviceAppSettings:** suspend device on display off / battery / logoff / sleep / shutdown; recover multitouch on wakeup; task-view toggle mode; swap desktop navigation direction; virtual-desktop swipe animation.
- **App:** autostart, start minimized, battery thresholds (charged 80%, warning 10%, critical 5%), alert toggles.
- **Driver (HKLM):** `BthSmoothScrolling`, `BthKeepAlive` with `BthKeepAlivePeriod=2000ms`, `BthDirectSdp`.

## UI (UI Automation dump of the main window)
- **Mouse Options:** pointer speed slider, swap primary/secondary, left handed, ignore input if lifted.
- **Scrolling:** scrolling, smooth scrolling, speed slider, acceleration slider, no scrolling on mouse movement, scroll only vertical or horizontal, inverse both directions, no horizontal scrolling.
- **1 Finger Gestures:** fast swipe left/right to navigate back/forward, swap direction, sensitivity slider.
- **2 Finger Gestures:**
  - fast swipe left/right to change desktops, with keyboard or swipe animation and swap direction;
  - fast swipe up to cycle Normal | Task View;
  - fast swipe down to cycle Normal | Desktop View.
- **Clicks:** simultaneous primary + secondary clicks, middle click.
- **Tap To Click:** on 1 / 2 / 3 finger tap, with filters.
- **Device Info:** battery, live touches visualiser.
