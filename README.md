# SwitchMonitor

SwitchMonitor is a Windows utility for changing monitor inputs with keyboard shortcuts. It supports separate settings for multiple monitors, a shortcut for cycling through connected inputs, and an optional shortcut in the Windows Startup folder. The interface is in English.

The current source is an AutoHotkey v2 application. It uses [ControlMyMonitor](https://www.nirsoft.net/utils/control_my_monitor.html) for standard DDC/CI monitors and includes experimental LG 29WK600 transports for the Intel and AMD systems used during development. A monitor and its active connection must support the required control command for switching to work.

## Run from source

Install AutoHotkey v2, place ControlMyMonitor in `Documents\ControlMyMonitor`, then run `switchMonitor.ahk`. Settings and diagnostics are saved in `data/` beside the script. The repository ignores this folder, the downloaded build tools, and generated packages.

The detailed project notes are in [SWITCHMONITOR-LEEME.md](SWITCHMONITOR-LEEME.md). Build instructions and third-party tool information are in [packaging/BUILD-TOOLS.txt](packaging/BUILD-TOOLS.txt).

## Releases and updates

The app checks the latest published GitHub release at startup and through **Check for updates** in the tray menu. When a newer version exists, it shows a desktop notice. **Download update** saves the installer to the user's Downloads folder; it does not run the installer. If the installer asset is missing, the notice links to the release page instead.

For each release, use a version tag such as `v1.3.0` and attach the installer as `SwitchMonitor-Setup-1.3.0.exe`. The version in `switchMonitor.ahk` (`APP_VERSION` and the Ahk2Exe directive), `packaging/SwitchMonitor.iss`, and `packaging/Build.ps1` must match. Publish a non-draft, non-prerelease GitHub release so the latest-release API can find it.

The current 1.2.0 installers were built before the update notice was added. The notice will first be available in the next compiled release.
