# DualSenseConnect

A small Windows tray app that makes a PlayStation 5 **DualSense** controller connected over
**Bluetooth** nicer to use on a PC: the mute button mutes your Windows microphone, sound and bass
vibration go to the controller, and battery, connection quality and power are one click away.

It is built to stay out of the way: it runs alongside **Steam Input** without taking over the
controller, and it uses almost no resources (about 14 MB of memory and ~0.2% of one CPU core when
idle, about 23 MB and ~1.5% while streaming sound).

> Not affiliated with or endorsed by Sony Interactive Entertainment. "DualSense" and "PlayStation" are
> trademarks of their respective owners.

> **Written with AI.** This project was written with
> the help of an AI assistant ([Claude Code](https://claude.com/claude-code) by Anthropic), directed and
> tested on real hardware by the author. Please report
> anything that looks wrong.

## Features

**Microphone**
- The controller's **mute button** mutes and unmutes the default Windows microphone; its orange LED
  follows the microphone state, also when you mute from Windows, Discord or a keyboard shortcut.
- A configurable **keyboard shortcut** (Ctrl+Shift+M by default) does the same.
- A short **confirmation buzz** in the controller: one buzz for muted, two for unmuted.

**Sound to the controller** (Bluetooth)
- Streams Windows sound to the controller's **3.5 mm headphone jack**, or to its built-in speaker
  when no headphones are plugged in. Headphone plug and unplug are detected automatically.
- With the free [VB-CABLE](https://vb-audio.com/Cable/) driver installed, the controller becomes an
  output device you can rename to **"DualSense"**. Windows output switches to it while headphones
  are plugged into the controller and switches back when they are unplugged.
- **Volume on the controller:** hold mute and press D-pad up or down.
- **Long-press mute** turns sound to the controller on or off.
- **Automatic buffer tuning** (optional): adds a little delay only while the Bluetooth link is
  dropping packets, to avoid audible gaps.

**Vibration**
- **Bass vibration:** the low end of what is playing is felt through the controller's actuators,
  with adjustable strength. Works with or without streaming sound, and does not touch Steam's rumble.

**Battery and power**
- Battery level and charging state in the tray tooltip.
- Notifications at low battery (15% and 5%) and when charging is complete.
- **Turn off the controller** from the tray menu, and optionally whenever the PC locks or sleeps.

**Other**
- **Connection quality** window: messages per second, dropouts, longest gap and a rating, to find a
  good spot for the controller or the Bluetooth antenna.
- Starts with Windows (optional, through Task Scheduler, no administrator rights needed).
- 12 languages: English, Spanish, French, German, Russian, Portuguese, Polish, Ukrainian,
  Belarusian, Japanese, Chinese (Simplified) and Korean.

## Limitations

- **Windows 10/11, 64-bit only.** Developed and tested on Windows 11.
- **Sound and vibration work over Bluetooth only.** Over USB, Windows already treats the controller
  as a sound card, so the app only handles the mute button and battery there.
- **The controller's built-in microphone is not supported.** Over Bluetooth its audio arrives in the
  same report as buttons and sticks, and Steam reads it as stick movement. Use a USB cable or another
  microphone.
- **One controller at a time.**
- **The Bluetooth audio and haptics protocol is not documented by Sony.** It is based on community
  reverse engineering and could change with a controller firmware update.
- **Range depends on your Bluetooth adapter.** Streaming sound needs a much steadier link than button
  input. If sound drops out a metre or two from the PC, check the adapter's antenna (on motherboards
  with built-in Wi-Fi/Bluetooth it is usually a separate antenna screwed onto the back panel), or use
  the connection quality window and automatic buffer tuning.
- **Without VB-CABLE** the sound is copied from an existing output device, so it plays on your
  speakers and on the controller at the same time.
- DualSense Edge is recognised but untested.
- Translations other than English and Russian have not been reviewed by native speakers. Corrections
  are welcome.

## Requirements

| What | Required | Notes |
|---|---|---|
| Windows 10 or 11, 64-bit | Yes | |
| Bluetooth adapter | Yes | A working Windows Bluetooth driver from the adapter or motherboard vendor |
| DualSense controller | Yes | Paired with Windows over Bluetooth |
| [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) | Only for the small download | Not needed for the `selfcontained` download |
| [VB-CABLE](https://vb-audio.com/Cable/) | No | Needed only for the "DualSense" output device. Donationware by VB-Audio, installed separately |

## Installation

1. Download the latest archive from [Releases](../../releases):
   - `DualSenseConnect-<version>-win-x64.zip` (recommended, about 1 MB): needs the .NET 10 Desktop
     Runtime. If it is missing, Windows offers to download it on first start.
   - `DualSenseConnect-<version>-win-x64-selfcontained.zip` (about 45 MB): runs without installing
     .NET, but uses more memory (about 55 MB instead of 14 MB), since the bundled runtime is
     unpacked into memory.
2. Extract it to a permanent folder, for example `%LOCALAPPDATA%\Programs\DualSenseConnect`.
3. Run `DualSenseConnect.exe`. The app is not code-signed, so Windows SmartScreen may warn you:
   choose **More info → Run anyway**.
4. Pair the controller if you haven't yet: hold **Create** and **PS** until the light bar blinks
   quickly, then add it in **Settings → Bluetooth & devices → Add device → Bluetooth**.
5. Right-click the tray icon to open **Settings**, and enable **Start with Windows** if you like.

### Optional: "DualSense" as an output device

1. Download VB-CABLE from [vb-audio.com/Cable](https://vb-audio.com/Cable/), extract it, run
   `VBCABLE_Setup_x64.exe` **as administrator**, click **Install Driver**, and restart the PC.
2. Optionally rename the new **CABLE Input** device to **DualSense**:
   **Settings → System → Sound → CABLE Input → Rename**.
3. DualSenseConnect finds the cable by itself. Plug headphones into the controller, and Windows sound
   moves to them.

## Controls

| Action | Result |
|---|---|
| Press mute | Mute or unmute the Windows microphone |
| Hold mute (0.8 s) | Turn sound to the controller on or off |
| Hold mute + D-pad ↑/↓ | Controller volume ±10% |
| Ctrl+Shift+M (configurable) | Mute or unmute the Windows microphone |
| Double-click the tray icon | Mute or unmute the Windows microphone |

Steam also sees these buttons. If a game reacts to the D-pad presses used for volume, change the
volume from the settings window instead.

## Data and privacy

The app makes no network connections and collects no data. Settings and a diagnostic log are stored
locally in `%LOCALAPPDATA%\DualSenseConnect` (`settings.json`, `log.txt`). The log contains device
names and the controller's Bluetooth address; check it before sharing it in an issue.

## Uninstalling

1. In the tray menu, untick **Start with Windows**. This removes the scheduled task.
2. Choose **Exit**.
3. Delete the app folder and `%LOCALAPPDATA%\DualSenseConnect`.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet build -c Release
# Release archives (both variants) into .\artifacts:
powershell -ExecutionPolicy Bypass -File .\build-release.ps1
```

Project layout: `Gamepad/` (controller I/O and haptics), `Audio/` (capture, Opus streaming, output
devices), `Microphone/`, `UI/` (tray and windows), `Core/` (settings, autostart, hotkey, log),
`Localization/` (one file per language).

## License

See [LICENSE](LICENSE). Third-party components and protocol references are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
