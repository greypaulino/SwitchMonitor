# SwitchMonitor v2 (in progress)

A rewrite of SwitchMonitor in C# / .NET 10 with **no third-party runtime dependencies**: only Windows APIs and the GPU drivers already installed on the computer. The released 1.x (AutoHotkey + NirSoft ControlMyMonitor) stays on `main` until v2 replaces it.

## Layout

| Path | Purpose |
|---|---|
| `src/SwitchMonitor.Core` | Monitor discovery, transports, capabilities parser, quirks, retries, logging |
| `src/SwitchMonitor.App` | The WPF app (`SwitchMonitor.exe`): Displays and Shortcuts tabs, profiles, tray icon, global shortcuts |
| `src/SwitchMonitor.Probe` | Read-only diagnostic console. Never writes to a monitor |
| `tests/SwitchMonitor.Core.Tests` | Hardware-free tests with a built-in runner (no test packages) |

## Transports

`IMonitorTransport` is the only way commands reach a display. `MonitorEnumerator` picks one per display:

| Id | Used for | API |
|---|---|---|
| `wmi` | Built-in laptop panels (brightness only) | `root\WMI` through the in-box WbemScripting COM object; falls back to the `\\.\LCD` brightness IOCTLs where WMI fails (Dell N5050, Intel HD 3000) |
| `intel-cui` | Quirk-listed monitors on the validated Intel HD Graphics 3000 driver | Intel CUI COM interface in the driver |
| `amd-adl` | Quirk-listed monitors on AMD without a standard handle | `atiadlxx.dll` (ADL2) in the driver. Writes need `amd-experimental.flag` |
| `ddc-ci` | Every other monitor | `dxva2.dll` Monitor Configuration API |

## Quirks

Model-specific behavior lives in [`Quirks/quirks.json`](SwitchMonitor.Core/Quirks/quirks.json) instead of code. An entry matches by EDID PnP ID (for example `GSM7714`) and can fix the input list, map input writes to a vendor VCP code, and prefer GPU transports. Codes are hex strings in MCCS terms (`11` = HDMI 1, `0F` = DisplayPort 1).

## Vendor controls and learn mode

Beyond the MCCS standard, monitors advertise their own codes (for example LG uses `0x15` for picture mode). `MonitorDevice.Features` lists them after the standard controls. A code nobody has mapped is shown read-only, because some vendors use their codes for resets. A quirk `controls` entry names a code and its values, can say the advertised value list is decimal or wrong (`capabilityValues`), give the real maximum of a slider, or mark a control `readOnly` when the monitor ignores writes. Mappings learned on one computer go in `%LOCALAPPDATA%\SwitchMonitor-v2\quirks.user.json` and overlay the embedded file.

Learn mode finds those codes for any DDC/CI monitor: take a snapshot of every advertised value (`--snapshot`), change one setting with the monitor's own buttons, take another snapshot and compare (`--diff`). Snapshots only read; MCCS reset and command codes are never touched.

Writes use the MCCS host address `0x51`. The LG 29WK600 ignored every write except brightness when it was sent with `0x50` (the address the 1.x app used); its input switch (`0xF4`) still needs `0x50`.

## The app

- **Displays tab**: inputs (with their shortcut and a "Next input" mark), then every control the monitor offers. Read-only and unmapped codes stay out of the panel. Two-option controls (FreeSync) are switches; mute is the speaker icon of the volume slider. Changing a mode or preset re-reads the panel (0.4 s and 2 s later). ↺ restores one slider's factory value for the current mode (MCCS `0x05` / `0x08`, the others are put back); "Reset to factory" restores brightness, contrast and colors. Quirk `enabledWhen` rules lock controls (LG red/green/blue only in picture mode Custom).
- **Profiles** (`scenes.json`): named sets of settings applied together. A value list is tried in order (`"picture mode": ["FPS Game 1", "Games"]`), modes are applied before levels, and the panel is re-read afterwards.
- **Shortcuts tab**: press keys to record; several shortcuts per action; duplicates block saving, shortcuts owned by another program are flagged. Global shortcuts pause while the tab is open.
- **1.x behavior kept**: Ctrl+Alt+1..9 per input, Ctrl+Alt+M Next input (again within 10 s goes back; the previous input survives restarts), keypad and main-keyboard brightness keys with tap and hold acceleration, Ctrl+Alt+* next display, LG menu correction after returning from another computer on Intel. Settings live in `%LOCALAPPDATA%\SwitchMonitor-v2` (`settings.json`, `scenes.json`, `state.json`).

## Reliability

Commands go through `RetryPolicy` (exponential backoff) rather than fixed sleeps. Writes are read back: `SetInput` and `SetPercent` return `Verified`, `Unverified` (accepted but unreadable, normal after switching to another PC) or `Mismatch`. Failures are logged through `ICoreLog`; `FileLog` rotates `%LOCALAPPDATA%\SwitchMonitor-v2\logs\switchmonitor.log`.

## Build and check (Windows, .NET 10 SDK)

```powershell
dotnet run --project .\tests\SwitchMonitor.Core.Tests\SwitchMonitor.Core.Tests.csproj
dotnet run --project .\src\SwitchMonitor.Probe\SwitchMonitor.Probe.csproj -- --log
```

The probe output is the most useful thing to attach to an issue about an unsupported monitor.

Probe options: `--snapshot FILE`, `--diff A B` (read-only); `--roundtrip --yes [--codes 10,12]` and `--try-choice 15=30,F7=02 --yes [--in-mode 15=1C]` write to the monitor and restore every value afterwards.
