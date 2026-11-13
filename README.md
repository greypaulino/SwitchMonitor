# SwitchMonitor

SwitchMonitor 2 controls your monitors from Windows and from your phone: inputs, brightness, picture modes, color, volume and every other setting the monitor offers over DDC/CI, with profiles and keyboard shortcuts. It is open source under the [MIT License](LICENSE) and has no third-party runtime dependencies: only Windows and the display drivers already on the computer.

## Download

From the [latest release](https://github.com/greypaulino/SwitchMonitor/releases/latest):

- **Windows 10/11 (x64):** `SwitchMonitor-<version>-win-x64.zip`. Unzip it anywhere and run `SwitchMonitor.exe`; nothing else needs to be installed. It stays in the tray (brightness panel under the sun icon). Settings live in `%LOCALAPPDATA%\SwitchMonitor-v2`.
- **Android 6.0+:** `SwitchMonitor-<version>.apk`, installed directly (allow installing from this source). In SwitchMonitor on the PC open *Settings → Devices*, turn on *Allow control from the network* and join the phone to *My devices* with the 6-digit code (or pair it alone with *Pair a phone*). On Xiaomi / HyperOS set the app's battery use to *No restrictions* so the Quick Settings panel and widgets keep their connection.

Each file has its SHA-256 in the release notes. The builds are not code-signed yet, so Windows SmartScreen may warn the first time.

## What it does

- **Displays:** every input with its shortcut, *Next input*, and every control the monitor offers (brightness, contrast, picture mode, color temperature, sharpness, response time, FreeSync, volume…). Unknown monitors can be taught with *Learn settings* and shared.
- **Profiles:** named sets of settings (Work, Night, Game, Theater…) applied in one click, by shortcut, or automatically while a linked program is in front. A profile changed by hand shows *· custom* with a *Save?* button.
- **Brightness panel** in the tray, linked or per display, and the main display chosen from it.
- **My devices:** your computers and phones form one group. Add a device once with the 6-digit code another computer shows (it changes every 30 seconds, like an authenticator app's) and one click of approval; after that they find and trust each other by themselves, even when an address changes. Every device has its own key; a computer can remove any device and the removal spreads.
- **Other computers:** the displays of the computers in the group appear in the panel with the same controls, and a profile can drive them all.
- **Shared mouse and keyboard:** push the pointer past the edge of the screen and keep going on another computer of the group. A phone can also be the mouse and keyboard of any computer in the group.
- **Apps:** drag a program to a display and it opens there (normal, maximized, half the screen or minimized).
- **Phone:** the Android app (*My devices* controls, plus a *Mouse* tab), widgets and a Quick Settings tile control brightness, profiles, picture modes, inputs, the main display and the volume of the computer that owns the selected display, over the home Wi-Fi. A phone that is not in the group can still pair with one computer by code; afterwards the phone and the computer prove to each other who they are without sending the secret again.
- **English and Spanish**, a welcome tour, and *Settings → About* with every feature and its switch in one place.

The code lives in [`src/`](src/README.md) (Windows) and [`android/`](android/). Build with the .NET 10 SDK (`dotnet build src\SwitchMonitor.App`) and Android Studio.

## Privacy and security

Everything stays on your computer and phone; the only internet access is the optional download of the shared monitor database from this repository. Phone access is off until you turn it on, answers only devices on private (home) networks, and accepts only paired phones. See the [privacy notice](PRIVACY.md).

## Code signing policy

The builds are unsigned. SignPath Foundation declined the project because it does not yet have enough public adoption; a Microsoft Store release is being considered instead. Verify the SHA-256 of what you download and, if in doubt, build from source.

Project author: [@greypaulino](https://github.com/greypaulino).

Version 1.x was an AutoHotkey application that bundled third-party tools; it is no longer distributed.
