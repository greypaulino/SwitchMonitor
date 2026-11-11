# SwitchMonitor 2 — source

A rewrite of SwitchMonitor in C# / .NET 10 with **no third-party runtime dependencies**: only Windows APIs and the GPU drivers already installed on the computer. The 1.x edition (AutoHotkey + NirSoft ControlMyMonitor) remains in the repository root.

## Layout

| Path | Purpose |
|---|---|
| `src/SwitchMonitor.Core` | Monitor discovery, transports, capabilities parser, quirks, retries, logging; `Remote/` (phone server, `Ecosystem`, peer client and store), `Sharing/` (mouse and keyboard protocol) |
| `src/SwitchMonitor.App` | The WPF app (`SwitchMonitor.exe`): Displays, Apps, Profiles and Shortcuts tabs, Settings, tray icon, global shortcuts, phone bridge, peers and shared input |
| `src/SwitchMonitor.Probe` | Read-only diagnostic console. Never writes to a monitor |
| `tests/SwitchMonitor.Core.Tests` | Hardware-free tests with a built-in runner (no test packages) |

## Transports

`IMonitorTransport` is the only way commands reach a display. `MonitorEnumerator` picks one per display:

| Id | Used for | API |
|---|---|---|
| `wmi` | Built-in laptop panels (brightness only) | `root\WMI` through the in-box WbemScripting COM object; falls back to the `\\.\LCD` brightness IOCTLs where WMI fails (Dell N5050, Intel HD 3000) |
| `intel-cui` | Quirk-listed monitors on the validated Intel HD Graphics 3000 driver | Intel CUI COM interface in the driver |
| `amd-adl` | Quirk-listed monitors on AMD without a standard handle | `atiadlxx.dll` (ADL2) in the driver. Writes are on; an empty `amd-writes-off.flag` in the data folder turns them off |
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

## My devices (the ecosystem)

`Remote/Ecosystem.cs` groups the computers and phones of one person without any server or account (home network only). Each device has an ECDSA P-256 key pair made at the first run (the private half is sealed with DPAPI on Windows, kept in the Keystore on Android). The first computer founds the group: its public key gives the group id. A computer already in the group signs a `MemberCert` for every device it lets in (certificates chain back to the founder); a removal is a signed `Revocation` that counts for certificates issued before it.

- **Joining:** the joining device sends `enroll` with an HMAC of the 6-digit code (RFC 6238 TOTP, SHA-1, 30 s, so an authenticator app fed the setup key shows the same code) bound to the server's challenge, its id and its key. The code works once and five wrong tries lock it for a minute. The person on the computer then clicks *Allow*, and the answer carries the group (certificates and revocations) plus a proof that the computer knew the code.
- **Signing in:** `member` carries a signature of the challenge, the device id and what the device knows of the group; `memberWelcome` answers the same way. Both sides merge the lists they receive, checking every certificate, so a new member becomes known to the others on their next connection. No token is stored per pair.
- **Finding each other:** members answer the UDP discovery with their device id and group id; `PeerService` connects to each member computer it finds and drops those that were removed.
- **A phone as a member** is a client with `kind: "phone"`. Its mouse and keyboard can aim at any computer of the group: the computer it is connected to passes `input` on (`target`, `phone: true`) to the one chosen.

## Reliability

Commands go through `RetryPolicy` (exponential backoff) rather than fixed sleeps. Writes are read back: `SetInput` and `SetPercent` return `Verified`, `Unverified` (accepted but unreadable, normal after switching to another PC) or `Mismatch`. Failures are logged through `ICoreLog`; `FileLog` rotates `%LOCALAPPDATA%\SwitchMonitor-v2\logs\switchmonitor.log`.

## Build and check (Windows, .NET 10 SDK)

```powershell
dotnet run --project .\tests\SwitchMonitor.Core.Tests\SwitchMonitor.Core.Tests.csproj
dotnet run --project .\src\SwitchMonitor.Probe\SwitchMonitor.Probe.csproj -- --log
```

The probe output is the most useful thing to attach to an issue about an unsupported monitor.

Probe options: `--snapshot FILE`, `--diff A B` (read-only); `--roundtrip --yes [--codes 10,12]` and `--try-choice 15=30,F7=02 --yes [--in-mode 15=1C]` write to the monitor and restore every value afterwards.
