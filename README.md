# SwitchMonitor

The AutoHotkey application is the active, supported version of SwitchMonitor. An experimental [C# / WPF migration preview](wpf/README.md) remains in `wpf/`, but its development is paused.

SwitchMonitor source code is open source under the [MIT License](LICENSE). Third-party tools and runtimes retain their own licenses. The WPF preview uses the Windows monitor APIs and AMD/Intel display drivers available on the user's computer; the released AutoHotkey edition separately uses NirSoft ControlMyMonitor.

## Code signing policy

The WPF preview is currently unsigned. Its [Windows build workflow](.github/workflows/wpf-preview.yml) compiles the public source and publishes an unsigned test artifact; a successful workflow does not mean it is signed. SignPath Foundation declined the project's application because it does not yet have enough public adoption and visibility. The AutoHotkey installer is also unsigned. Neither compiling AutoHotkey to an EXE nor switching programming languages provides a trusted code signature.

Project author: [@greypaulino](https://github.com/greypaulino). The [privacy notice](PRIVACY.md) covers local settings and connections to GitHub. Until a signed release exists, verify the source and treat downloadable builds as unsigned.

The [privacy notice](PRIVACY.md) describes the app's GitHub release checks and locally stored settings.

SwitchMonitor is a Windows utility for changing monitor inputs with keyboard shortcuts. It supports separate settings for multiple monitors, a shortcut for cycling through connected inputs, and an optional shortcut in the Windows Startup folder. The interface is in English.

The current source is an AutoHotkey v2 application. It uses [ControlMyMonitor](https://www.nirsoft.net/utils/control_my_monitor.html) for standard DDC/CI monitors and includes experimental LG 29WK600 transports for the Intel and AMD systems used during development. A monitor and its active connection must support the required control command for switching to work.

## Run from source

Install AutoHotkey v2, place ControlMyMonitor in `Documents\ControlMyMonitor`, then run `switchMonitor.ahk`. Settings and diagnostics are saved in `data/` beside the script. The repository ignores this folder, the downloaded build tools, and generated packages.

The detailed project notes are in [SWITCHMONITOR-LEEME.md](SWITCHMONITOR-LEEME.md). Build instructions and third-party tool information are in [packaging/BUILD-TOOLS.txt](packaging/BUILD-TOOLS.txt).

## Releases and updates

The app checks GitHub releases at startup, every six hours, and through **Check for updates** in the monitor tray menu. A newer version changes that menu item to **Update available! Click to install** and shows a clickable Windows notification. Only clicking the notification or menu item downloads the installer, verifies the published SHA-256 checksum, upgrades the installed edition, and reopens the app. Settings in `%LOCALAPPDATA%\SwitchMonitor` are preserved. Portable and source copies open the release page instead.

For each release, use a version tag such as `v1.7.0` and attach the installer as `SwitchMonitor-Setup-1.7.0.exe` and its `SHA256.json` checksum manifest. The version in `switchMonitor.ahk` (`APP_VERSION` and the Ahk2Exe directive), `packaging/SwitchMonitor.iss`, and `packaging/Build.ps1` must match. Publish a non-draft, non-prerelease GitHub release so the latest-release API can find it.

Version 1.3.0 is the first release with update notices and one-click installer downloads.
The 1.4.0 installer bundles the official AutoHotkey interpreter and the application scripts, because Windows Defender blocked the Ahk2Exe-generated binary during packaging. The installed app still preserves settings in `%LOCALAPPDATA%\SwitchMonitor`.

## Brightness control

The sun icon in the tray toggles a mouse-controlled brightness panel. In independent mode it shows one slider per detected monitor. Linking monitors combines them into one slider and shows their names on separate lines; moving that slider applies the same displayed percentage to each monitor. The link control is unavailable when only one monitor is detected. Settings includes editable shortcuts and the same link option.

The defaults are **Ctrl+Alt+-** to decrease, **Ctrl+Alt++** to increase, and **Ctrl+Alt+*** to select the next monitor. They accept the numeric keypad; the main keyboard aliases are `-`, `Shift+=`, and `Shift+8` on a US layout. All three shortcuts can be changed in Settings for other layouts. A tap or mouse-wheel step changes brightness by one point. Holding a shortcut for at least 250 ms repeats in five-point steps; after two seconds it repeats in ten-point steps. The panel slides up on activity, stays open while the pointer is over it, and hides after five seconds of inactivity or a click outside. Its gear button opens Settings.

While the brightness panel is open, `+` and `-` adjust the selected display and `*` selects the next display without Ctrl+Alt. Both the numeric keypad and the US main-keyboard combinations work. When multiple displays are present, a subtle rounded background highlights the selected display. Hovering over another display or pressing `*` moves the highlight smoothly. Short and rapid brightness changes move the indicator immediately; larger jumps animate. The last display is selected when the app starts.

Brightness uses VCP code 10 through ControlMyMonitor, except on the validated LG 29WK600 Intel system where it uses the existing Intel DDC transport. The app reads each monitor's reported VCP maximum and maps it to 100% (for example, raw 25/50 appears as 50%). The range is cached per monitor after discovery. A display must expose working brightness control to be adjustable.

Built-in laptop displays use Windows WMI brightness when Windows exposes an active brightness instance. They appear in the monitor selector and brightness panel without input shortcuts. The LG 29WK600 identifiers `GSM7714` and `GSM7715` are limited to its physical HDMI 1, HDMI 2, and DisplayPort 1 inputs, even when generic capabilities list an additional DisplayPort code.

In independent mode, the rounded background identifies the active brightness target. Linked mode shows one slider for all displays without an active-target highlight. The monitor list refreshes after Windows display/device changes and during periodic checks, keeping the selected monitor when it remains connected.

Settings can back up and restore profiles, shortcuts, brightness preferences, and the Start with Windows choice. Save any pending edits before backing up or restoring. The About window includes a **Test notification** button so you can verify notification clicks without starting an update.
