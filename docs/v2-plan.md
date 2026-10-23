# SwitchMonitor v2 plan

Goal: one C# / .NET 10 code base with no third-party runtime dependencies that controls every monitor feature a display advertises, distributed without cost while staying MIT-licensed. The 1.x AutoHotkey edition is frozen to critical fixes on `main`; v2 work happens on `v2-rewrite`.

## Phase 1 — core without hardware ✅ (this branch)

- [x] `SwitchMonitor.Core` with `IMonitorTransport`: `DdcCiTransport`, `WmiBrightnessTransport`, `IntelCuiTransport`, `AmdAdlTransport`
- [x] `quirks.json` replaces hard-coded `GSM7714`/`GSM7715` and `[17, 18, 15]`
- [x] Tolerant MCCS capabilities parser (nested values, missing parentheses, run-together hex)
- [x] Exponential-backoff retries and read-back verification instead of fixed sleeps
- [x] Rotating diagnostic log; no silent `catch { }`
- [x] Hardware-free tests and CI, plus a CI check that no package references are added
- [x] Read-only probe for testing on real computers

## Phase 2 — more controls

- [ ] Contrast `0x12`, volume `0x62`, mute `0x8D`, power `0xD6`
- [ ] Color presets `0x14`, RGB gains `0x16`/`0x18`/`0x1A`
- [ ] Vendor picture or game modes, shown only when advertised
- [ ] Scene profiles ("Work", "Night", "Game") applying several values with one shortcut

## Phase 3 — interface

- [ ] Split the WPF code-behind into services and MVVM (`HotkeyService`, `TrayService`, `UpdateService`, `MonitorManager`)
- [ ] Controls panel generated from each monitor's capabilities
- [ ] Import 1.x (AutoHotkey) and WPF 0.1.x profiles
- [ ] All settings and flags under `%LOCALAPPDATA%` (required for MSIX)

## Phase 4 — quality and distribution

- [ ] Release builds only from GitHub Actions, single `VERSION` file
- [ ] MSIX for the Microsoft Store (free registration; the Store signs the package)
- [ ] Portable build on GitHub Releases with GitHub artifact attestations
- [ ] One `CHANGELOG.md`, short README with screenshots, usage docs in `docs/`

## Hardware validation still needed

- AMD ADL2 writes (brightness and input) on the Radeon 7 + LG 29WK600 setup
- WMI brightness on a laptop through the new transport
- Run `SwitchMonitor.Probe -- --log` on each test computer and compare with the 1.x behavior
