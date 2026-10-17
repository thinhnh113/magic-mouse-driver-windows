# MagicMouse

A clean-room Windows utility (the app was first called MagicTouch; the driver keeps the internal name MagicTouchFilter) for Apple Magic Mouse (2009 / 2015 / 2024 USB-C), built to match the
features of Magic Mouse Utilities 3.1.5. See `docs/protocol-notes.md` for what was learned from the
installed app and the device. No vendor code is used.

## Layout
| Path | What |
|---|---|
| `driver/MagicTouchFilter/` | KMDF lower filter (C). Sends the multitouch mode switch, forwards touch reports to the app, and keeps the normal mouse working |
| `src/MagicMouse.Core/` | Driver client, report parser, gesture engine, SendInput output, settings, battery |
| `src/MagicMouse.App/` | Tray app `MagicMouse.exe` with a settings window |
| `tools/HidProbe/` | Diagnostics: `HidProbe info`, `stream`, `raw`, `sdp` |
| `tests/MagicMouse.Tests/` | xUnit tests for the parser and gesture engine (`dotnet test`) |

## Features (mapped from the original app)
- **Mouse:**
  - pointer speed;
  - swap primary/secondary;
  - left-handed mode;
  - custom left/right split;
  - ignore input while the mouse is gripped/lifted.
- **Scrolling:**
  - on/off and smooth (hi-res wheel deltas);
  - momentum;
  - speed and acceleration;
  - one/two-finger scrolling;
  - stop on mouse move;
  - vertical-or-horizontal lock;
  - inverse V/H;
  - no horizontal.
- **1-finger gestures:** fast swipe left/right for Back/Forward, swappable direction, sensitivity.
- **2-finger gestures:**
  - fast swipe left/right switches virtual desktops (swappable);
  - swipe up toggles Task View;
  - swipe down toggles Show Desktop.
- **Clicks:**
  - simultaneous primary + secondary;
  - middle click by center zone or by two fingers apart.
- **Tap to click:**
  - 1 finger with left/middle/right zones and a configurable action per zone;
  - 2- and 3-finger taps with configurable actions;
  - double-tap-drag;
  - tap area and ignore-area filters;
  - ignore taps right after a real click.
- **Device:** battery % in the tray icon and tooltip, low/critical/recharged alerts, a live touches view, and a "re-enable multitouch" action.
- **Power:**
  - pause multitouch on display off, on battery, or while locked;
  - recover after wakeup (the driver also re-arms on its own when the mouse resets);
  - Bluetooth keep-alive.
- **App:** enable/disable, start with Windows, start minimized, tray icon toggle. Settings are stored in `%APPDATA%\MagicMouse\settings.json`, and gesture thresholds sit in its `Engine` section.

**Differences from the original:**
- Desktop switching uses the Windows keyboard animation. There is no injected-touchpad "swipe animation".
- "Suspend device" is implemented as pausing multitouch, not disconnecting Bluetooth.
- On the 2009 Magic Mouse the app injects pointer motion itself, because its multitouch report replaces the plain mouse report.

## Build
- **App, tests, probe:** need the .NET 8 SDK. Run `dotnet build MagicTouch.sln -c Release (output: `src\MagicMouse.App\bin\x64\Release\net8.0-windows\MagicMouse.exe`)` and `dotnet test`.
- **Driver:** needs VS 2022 Build Tools (C++ toolset, Windows 11 SDK 26100, Spectre libs) plus WDK 10.0.26100. Run `driver\MagicTouchFilter\build.cmd`.
  - It compiles with cl/link directly, so full Visual Studio isn't needed.
  - It test-signs with a self-signed "MagicTouch Test" certificate from `CurrentUser\My`.
  - The output goes to `driver\MagicTouchFilter\out\`: `.sys`, a stamped `.inf`, a signed `.cat`, and `MagicTouchTest.cer`.
  - With full Visual Studio and the WDK extension installed, `MagicTouchFilter.vcxproj` works too.

## Installer
`build-installer.cmd` builds the driver, runs the tests, and publishes the app self-contained, so the target PC doesn't need .NET. It then produces `artifacts\installer\MagicMouse-Setup-<version>.exe` with Inno Setup 6.

**What Setup does:**
- Closes a running MagicMouse.
- Installs to `Program Files\MagicMouse`, with a Start-menu entry and optional autostart / desktop icon.
- Trusts the test certificate (test-signed builds only).
- Installs the driver and restarts connected Magic Mice.
- If test-signing mode is off, offers to turn it on.
- Warns if Magic Utilities' driver is still installed.
- Asks for a restart only if Windows needs one.
- Logs driver steps to `install.log`.

**What uninstall does** (Settings → Apps → MagicMouse):
- Closes the app.
- Removes every copy of the driver, so the mouse goes back to the stock Windows driver.
- Removes the driver service, the test certificate, autostart and the files.
- Asks whether to keep your settings.

Test-signing mode is left as it is; turn it off with `bcdedit /set testsigning off` if nothing else needs it.

**Production signing:** the installer works unchanged with a Microsoft-signed driver.
1. Sign `MagicTouchFilter.sys` and the `.cat` with your EV certificate.
2. Submit the package for attestation signing in the Partner Center hardware dashboard.
3. Put the returned files in `driver\MagicTouchFilter\out` without `MagicTouchTest.cer`.

Setup then skips the certificate and test-mode steps, and the driver loads with Secure Boot on.

## Manual install (development)
A self-built kernel driver can only load in Windows **test-signing mode**:
- Secure Boot must be off in the firmware settings.
- Production signing would need an EV certificate and Microsoft attestation.

1. Uninstall Magic Mouse Utilities, or at least its mouse driver. Both filters bind to the same hardware IDs.
   - `pnputil /enum-drivers` shows its `oemNN.inf` ("Magic Utilities Pty Ltd").
   - Remove it with `pnputil /delete-driver oemNN.inf /uninstall`.
2. Run `bcdedit /set testsigning on` in an admin prompt, then reboot.
3. In an admin prompt, trust the test certificate and install the driver. Use full paths, because an admin prompt starts in System32:
   ```
   cd /d C:\Users\Admin\Downloads\magic_mouse\driver\MagicTouchFilter\out
   certutil -addstore Root MagicTouchTest.cer
   certutil -addstore TrustedPublisher MagicTouchTest.cer
   pnputil /add-driver MagicTouchFilter.inf /install
   ```
4. Remove and re-pair the mouse, or disable and re-enable it in Device Manager.
5. Check the setup with `HidProbe info`, which should list the filtered mouse. Then run `HidProbe stream`, touch the surface, and confirm that touch frames appear.
6. Run `MagicMouse.exe`.

**Updating the driver later:** from an admin prompt, run `driver\MagicTouchFilter\update.cmd` after `build.cmd`.
- It closes MagicMouse first, installs the driver, restarts the mouse, and starts the app again.
- Don't install over a running app. Its open handle keeps the old driver loaded, and Windows then postpones the update until a reboot, leaving the driver half-updated until then.

**Rollback:**
1. Run `pnputil /delete-driver <MagicTouchFilter oem inf> /uninstall` and `bcdedit /set testsigning off`.
2. Reboot.
3. Reinstall Magic Mouse Utilities.

**Safety:**
- If MagicMouse exits or crashes, the driver stops masking buttons, so clicks keep working natively.
- If the driver can't start (no touch data), the mouse behaves like it does on stock Windows.
