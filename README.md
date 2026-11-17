# NashmiKeys

Turn controller button chords into keyboard input on Windows. Any combination of controller buttons can trigger any set of keyboard keys, and you can create as many hotkeys as you want — each one sending a single key or a multi-key shortcut. No drivers, no virtual gamepad: it listens to your controller and sends real keyboard input.

Works with Xbox and PlayStation controllers on Windows 10/11.

## Features

- Any number of buttons as a trigger, any set of keyboard keys as output.
- **Hold / Tap / Toggle** mode per hotkey.
- Unlimited hotkeys, active globally.
- **Pass-through** — the game still receives the controller input.
- Tray app with optional elevated autostart, so there is no UAC prompt at login.

## Install

1. Get `NashmiKeys-1.0-Setup.exe` from the [Releases](../../releases) page.
2. Run it. It needs **.NET 8 Desktop Runtime** or newer; the setup checks and tells you if it is missing.
3. Optional tasks: start with Windows, desktop shortcut, launch after install.

The build is unsigned, so SmartScreen may warn — choose "More info → Run anyway".

## Use

1. Connect your controller.
2. **Add hotkey**, then press the buttons you want as the trigger.
3. Click the output box and press the keys you want sent.
4. Pick a mode: **Hold** (while the chord is held), **Tap** (once), **Toggle** (press to turn on, press to turn off).

Release every button of a chord before triggering another.

## Notes

- Controller input is **not blocked**; the game sees the chord as well.
- Runs elevated so keys reach elevated windows.
- Settings live in `%APPDATA%\NashmiKeys\config.json`.

## Build

Requires the .NET 8 SDK and Inno Setup 6.

```powershell
dotnet build -c Release
dotnet run --project tests\NashmiKeys.Tests -c Release
dotnet publish src\NashmiKeys.App -c Release -r win-x64 --self-contained false -o dist
installer\build.ps1
```

`SDL3.dll` ships under `third_party\sdl` (zlib license).

## License

MIT — see [LICENSE](LICENSE).
