# SwitchMonitor 2.0.0

A new SwitchMonitor, rewritten in C# / .NET with no third-party runtime dependencies, plus an Android app.

## Windows

- **Every control your monitor offers** over DDC/CI: inputs, brightness, contrast, picture mode, color temperature, sharpness, response time, FreeSync, volume and more; vendor menus can be taught with *Learn settings* and shared.
- **Profiles** (Work, Night, Game, Theater…) by click, shortcut or linked program. A profile changed by hand stays active as *· custom*, with a *Save?* button under it. The Profiles view opens locked; the padlock unlocks editing.
- **Brightness panel** in the tray (linked or per display, main display, picture modes), keyboard shortcuts with tap and hold acceleration, *Next input* and return to the previous input.
- Loading placeholders: the window no longer jumps while displays are read.

## Phone (Android 6.0+)

- Brightness per display or linked, profiles, picture modes, inputs, main display and the Windows volume over the home Wi-Fi.
- Widgets (4×2, 2×2 with display switch, profiles) and a Quick Settings tile whose panel opens at once.
- Pairing with a 6-digit code. After that the secret is never sent again: each connection is a challenge both sides answer, and the secret is kept encrypted (Windows DPAPI, Android Keystore) and out of backups.
- On Xiaomi / HyperOS set the app's battery use to *No restrictions*.

## Files

- `SwitchMonitor-2.0.0-win-x64.zip` — unzip and run `SwitchMonitor.exe` (no installation, .NET included). Settings: `%LOCALAPPDATA%\SwitchMonitor-v2`.
- `SwitchMonitor-2.0.0.apk` — install directly on the phone.

The builds are not code-signed. SHA-256 below.

SwitchMonitor 1.x is not replaced automatically; if both run, close 1.x so their shortcuts do not collide.
