# SwitchMonitor v2 (in progress)

A rewrite of SwitchMonitor in C# / .NET 10 with **no third-party runtime dependencies**: only Windows APIs and the GPU drivers already installed on the computer. The released 1.x (AutoHotkey + NirSoft ControlMyMonitor) stays on `main` until v2 replaces it.

## Layout

| Path | Purpose |
|---|---|
| `src/SwitchMonitor.Core` | Monitor discovery, transports, capabilities parser, quirks, retries, logging |
| `src/SwitchMonitor.Probe` | Read-only diagnostic console. Never writes to a monitor |
| `tests/SwitchMonitor.Core.Tests` | Hardware-free tests with a built-in runner (no test packages) |

## Transports

`IMonitorTransport` is the only way commands reach a display. `MonitorEnumerator` picks one per display:

| Id | Used for | API |
|---|---|---|
| `wmi` | Built-in laptop panels (brightness only) | `root\WMI` through the in-box WbemScripting COM object |
| `intel-cui` | Quirk-listed monitors on the validated Intel HD Graphics 3000 driver | Intel CUI COM interface in the driver |
| `amd-adl` | Quirk-listed monitors on AMD without a standard handle | `atiadlxx.dll` (ADL2) in the driver. Writes need `amd-experimental.flag` |
| `ddc-ci` | Every other monitor | `dxva2.dll` Monitor Configuration API |

## Quirks

Model-specific behavior lives in [`Quirks/quirks.json`](SwitchMonitor.Core/Quirks/quirks.json) instead of code. An entry matches by EDID PnP ID (for example `GSM7714`) and can fix the input list, map input writes to a vendor VCP code, and prefer GPU transports. Codes are hex strings in MCCS terms (`11` = HDMI 1, `0F` = DisplayPort 1).

## Reliability

Commands go through `RetryPolicy` (exponential backoff) rather than fixed sleeps. Writes are read back: `SetInput` and `SetPercent` return `Verified`, `Unverified` (accepted but unreadable, normal after switching to another PC) or `Mismatch`. Failures are logged through `ICoreLog`; `FileLog` rotates `%LOCALAPPDATA%\SwitchMonitor-v2\logs\switchmonitor.log`.

## Build and check (Windows, .NET 10 SDK)

```powershell
dotnet run --project .\tests\SwitchMonitor.Core.Tests\SwitchMonitor.Core.Tests.csproj
dotnet run --project .\src\SwitchMonitor.Probe\SwitchMonitor.Probe.csproj -- --log
```

The probe output is the most useful thing to attach to an issue about an unsupported monitor.
