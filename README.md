# GlowSync — ambient light for Skydimo and Adalight LED strips on Windows

**English** · [Русский](README.ru.md)

The LED strip behind your monitor mirrors the colors at the edges of the screen, dances to music or runs effects.
GlowSync is a lightweight, open-source replacement for the official Skydimo app (and an Ambilight-style alternative
to Prismatik / HyperHDR for DIY Adalight strips). It lives in the tray, never pops up on its own and turns the strip off
together with the monitor.

**[⬇ Download GlowSync.exe](https://github.com/skybots-tg/glowsync/releases/latest/download/GlowSync.exe)** · Windows 10 (1903+) / 11 · free, MIT

> The interface is in Russian for now.

![Main window](docs/screenshot.png)

## Features

- **Screen sync** via DXGI Desktop Duplication. The frame is downscaled on the GPU, CPU load is about 0.1 %.
  Settings: 10–60 fps, smoothing, saturation, gamma, capture depth, blending of neighbouring LEDs, black threshold,
  automatic letterbox (black bar) detection for widescreen movies (subtitles in the bar do not confuse it).
- **Music mode**: audio is taken from the playback device (WASAPI loopback, no microphone needed):
  spectrum, pulse, VU meter. Switching between headphones and speakers is picked up automatically.
- **Effects**: rainbow, color cycle, breathing, solid color, comet.
- **Turns off with the monitor**, detected two independent ways, because one is not enough:
  1. Windows display-state notifications (console display state, session display status, monitor power);
  2. polling the monitor itself over DDC/CI (VCP 0xD6) every 2 seconds. This catches the monitor being switched off
     with its own power button, which Windows does not report at all.

  A monitor that does not answer DDC/CI never turns the strip off by itself, and neither does a single missed answer
  (three in a row are needed). Turning off applies after the screen has been dark for more than 2 seconds, turning on
  is instant: this handles monitors that blink on and off a dozen times while going to sleep. The strip also goes dark
  on sleep and shutdown and, optionally, on lock (Win + L).
- **Night mode**: brightness limit on a schedule. **White balance** R/G/B if the strip looks too blue.
- **Global hotkeys** with an unobtrusive on-screen hint that does not steal focus from games.
- **Auto-detect and auto-reconnect**: unplugged USB, sleep, port busy with another program: it reconnects without
  a restart. Bluetooth COM ports are skipped (probing them freezes the system for seconds).
- **Layout editor** (LEDs per side, start corner, direction, offset) and a side test.
- **One-click import of Skydimo settings**: brightness, mode, layout, frame rate, hotkeys.
- Log is capped at 1 MB (Skydimo's logs were known to grow to 15 GB).

## Requirements

- Windows 10 (1903+) or Windows 11.
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0). If it is missing, Windows offers to
  download it on first launch.
- A Skydimo LED strip (tested on **SK0127**, 65 LEDs, CH340 USB chip), or any DIY strip running **Adalight**
  firmware (Arduino, ESP8266/ESP32 and similar).

## Install

**Prebuilt exe:** download [`GlowSync.exe`](https://github.com/skybots-tg/glowsync/releases/latest/download/GlowSync.exe),
put it in any folder and run it. It adds itself to autostart (silent, tray only) and finds the strip on a USB port.

**From source:**

```powershell
git clone https://github.com/skybots-tg/glowsync.git
cd glowsync
.\install.ps1
```

`install.ps1` builds the project, installs it to `%LOCALAPPDATA%\Programs\GlowSync`, creates a Start menu shortcut
and launches it. The same command updates an existing install.

Command-line options: `--autostart` starts silently in the tray (this is how autostart is registered),
`--exit` closes a running instance.

Files: settings in `%LOCALAPPDATA%\GlowSync\config.json`, logs in `%LOCALAPPDATA%\GlowSync\logs`.

## Default hotkeys

| Action | Keys |
|---|---|
| Toggle the light | Ctrl + Alt + Shift + F10 |
| Brighter / dimmer | Ctrl + Alt + Shift + ↑ / ↓ |
| Next / previous mode | Ctrl + Alt + Shift + → / ← |

They can be changed in the settings, on the «Поведение» (Behaviour) tab.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| "COM port is busy with another program" | Skydimo or other LED software is running. Settings → «Поведение» has a button that closes Skydimo and removes it from autostart (reversible). |
| The strip does not turn off with the monitor | On the «Поведение» tab, check that the monitor answers DDC/CI. If it says it has not answered yet, enable DDC/CI in the monitor's own menu. |
| Device not found | Install the USB-serial driver (CH340 for Skydimo). The strip must show up as a COM port in Device Manager. |
| Colors are in the wrong places | «Лента и устройство» (Strip and device) tab → side test: left is red, top green, right blue, bottom yellow, the first LED white. Adjust start and direction. |
| Dark movie scenes flicker | Increase the black threshold and smoothing on the «Экран» (Screen) tab. |
| Black screen on browser video | DRM-protected content (Netflix and similar) is excluded from screen capture by Windows itself. |

## Skydimo protocol

Useful if you are writing your own software: Skydimo controllers are Adalight with a different frame header.

- Port: 115200 baud, 8N1.
- Handshake: send `Moni-A`, the reply is `SK0127,` + 7 bytes of serial number + `\r\n`
  (those bytes may contain 0x0D and 0x0A, so do not parse it line by line).
- Frame: `'A' 'd' 'a' 0x00 <count_hi> <count_lo>` followed by `count × RGB`, 3 bytes per LED.
  Classic Adalight differs: `'A' 'd' 'a'` + `(count-1)` + checksum byte `hi ^ lo ^ 0x55`.
- The controller expects exactly as many LEDs as its model has (65 for SK0127).
- Capture zones: the screen is split into a grid (31×17 for SK0127), each LED takes the average of its cell,
  then gamma 2.2 is applied. The result is indistinguishable from the official app
  (checked against real Skydimo output: difference ≤ 2 of 255).

## Development

```powershell
dotnet build GlowSync.sln
dotnet build tests -o $env:TEMP\gs-tests; & $env:TEMP\gs-tests\GlowSync.Tests.exe
```

Tests cover the layout generator (compared with Skydimo's maps if it is installed), controller reply parsing,
settings import, the zone sampler (against reference Skydimo output on a gradient), letterbox detection,
the night schedule, monitor polling logic and real GPU screen capture.

Pitfalls worth knowing if you build something similar:

- **Vortice 3.8**: the `IDXGIOutput5.DuplicateOutput1` wrapper crashes the process with an AccessViolation, so the method
  is called directly through the vtable (see `DesktopCapture.DuplicateOutput1`).
- **`POWERBROADCAST_SETTING`** carries exactly one data byte after the GUID and length. Reading it as a 32-bit value
  pulls garbage from the buffer into the high bytes, and "display off" turns into "on"
  (see `SystemEvents.OnPowerSettingChange`).
- **`System.IO.Ports`** is deliberately not used: its background thread crashes the process if the USB adapter is
  unplugged while the port is open. A thin wrapper over `CreateFile`/`WriteFile` is used instead.

## License

MIT. Do whatever you want, no warranty.
