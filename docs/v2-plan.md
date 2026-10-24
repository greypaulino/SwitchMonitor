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

## Phase 2 — more controls ✅

- [x] Control catalog: brightness, contrast, volume, mute, color preset, picture mode, RGB gains, power (MCCS 2.2a value names)
- [x] Controls and choices shown only when the monitor advertises them; choice values are never guessed
- [x] Capabilities read through the Intel CUI and AMD ADL2 drivers (DDC/CI 0xF3), so GPU-transport monitors show every control
- [x] Scene profiles (`scenes.json`: Work, Night, Game) that skip what a monitor lacks; power and input can never be in a scene
- [x] Inactive displays (e.g. closed laptop lid) listed as OFF and never commanded, matching 1.10.1
- [x] Read-only probe lists every control; round-trip write test (+/-1 and restore) for brightness, contrast, volume

Validated on DESKTOP-BPMFO0B (Intel HD 3000 + LG 29WK600): capabilities read, all controls read, brightness write round-trip.
Writes use the MCCS host address 0x51 (0x50 only for the LG 0xF4 input switch); with 0x50 the LG ignored every write except brightness.

## Phase 2b — vendor controls ✅

- [x] Advertised vendor codes shown read-only until mapped; MCCS reset/command codes never read or written
- [x] Learn mode: snapshot, change one setting on the monitor's own menu, snapshot again, diff (works for any DDC/CI monitor)
- [x] `quirks.json` controls with value names, decimal capability lists and real maximums; `quirks.user.json` overlay for mappings learned locally
- [x] LG 29WK600 mapped: picture mode (0x15, 10 modes), response time (0xF7), FreeSync (0xF8), black stabilizer (0xF9), gamma (0xFE)
- [x] Write-verified on the LG: picture mode, response time, FreeSync. Gamma and black stabilizer ignore DDC writes, so they are marked read-only

## Phase 3 — one app, two faces

One process and one Core own every monitor command (DDC/CI is a shared bus: two programs talking to it at once collide) and every global shortcut. The 1.x "SwitchMonitor" and "brightness" programs merge, separated by the question each answers:

| | Connection — "which computer, where?" | Picture — "how does it look?" |
|---|---|---|
| Tray icon | Monitor | Sun |
| Holds | Inputs, Next input, return to previous, LG menu correction, main display and its hand-off | Brightness (linked or not), contrast, picture mode, response time, FreeSync, color, scenes |

One settings window (tabs: Displays, Shortcuts, Scenes, General) serves both. Showing the sun icon is optional.

- [x] MVVM app with services (`HotkeyService`, `TrayIcon`), controls generated from each monitor's capabilities
- [x] Block 1: 1.x keyboard behavior (keypad and main-keyboard shortcuts, one-point taps, third quick tap and hold acceleration), slider arrow keys, persistent shortcut warnings
- [x] Block 2: Ctrl+Alt+1..9 input shortcuts, Next input with marked inputs and the ten-second return, Return to previous input (saved), LG menu correction on Intel
- [x] Fixes from the block 1-2 hardware tests (laptop brightness through `\\.\LCD`, Next input pressed during a switch, unreadable controls reported)
- [x] Shortcut editor as a tab: press the keys to record, several shortcuts per action, live conflict and "used by another program" checks, restore defaults
- [x] Scenes shown as Profiles; value alternatives; modes applied before levels
- [x] Panel: switches for on/off controls, speaker icon for mute, read-only codes hidden, picture mode in the monitor's menu order, auto re-read after mode changes, factory-value reset per slider and for all, quirk `enabledWhen` locks
- [ ] Window reorganized into Connection / Picture / Settings
- [ ] "Add setting" wizard: pick a general function (or name a new one), Learn = guided snapshot diff repeated per option, value-type detection, write test with automatic revert, saved to `quirks.user.json`
- [ ] Sun panel (Picture): one row per display, OFF rows, wheel, linked brightness, keypad +/-/*, typed 0-100, auto-hide; main-display "M" moves to Connection
- [ ] Profile export/import and a shared, checksum-verified model database fed by user contributions
- [ ] Import 1.x settings, Start with Windows, backup/restore, diagnostics button
- [ ] All settings and flags under `%LOCALAPPDATA%` (required for MSIX)

## Phase 4 — quality and distribution

- [ ] Release builds only from GitHub Actions, single `VERSION` file
- [ ] MSIX for the Microsoft Store (free registration; the Store signs the package)
- [ ] Portable build on GitHub Releases with GitHub artifact attestations
- [ ] One `CHANGELOG.md`, short README with screenshots, usage docs in `docs/`

## Hardware validation still needed

- AMD ADL2 writes (brightness and input) on the Radeon 7 + LG 29WK600 setup
- WMI brightness on a laptop through the new transport (the test laptop panel was inactive)
- Run `SwitchMonitor.Probe -- --log` on each test computer and compare with the 1.x behavior
