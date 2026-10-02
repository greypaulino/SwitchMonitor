# SwitchMonitor WPF preview

This is the native C# / WPF migration of SwitchMonitor. It is an experimental preview alongside the released AutoHotkey edition. It stores a separate profile in `%LOCALAPPDATA%\SwitchMonitor-Wpf\settings.json` and does not replace the released edition or its settings.

The preview provides per-monitor input shortcuts, a Next input shortcut, a Settings window, a Shortcuts window, a brightness panel with independent or linked sliders, editable global shortcuts, Startup shortcut control for installed builds, profile backup and restore, and optional import of the AutoHotkey profile. It maps each monitor's reported maximum brightness to 100% in the interface. The update checker accepts only WPF release assets and does not install updates without a user click.

## Monitor transports

- Standard DDC/CI uses Windows physical-monitor handles and reads advertised VCP 0x60 input codes. A monitor without a working handle cannot use this path.
- The LG 29WK600 on the validated Intel HD Graphics 3000 driver uses its Intel COM/DDC transport. A physical HDMI 1 to DisplayPort switch and return to HDMI 1 showed the correct picture and firmware menu on both ends.
- On a Radeon 7 computer, Windows exposes the same LG as `GSM7715` without a standard DDC handle. AMD ADL2 identifies it by Windows display source and EDID. A read-only probe validated VCP 0x10 brightness at 52/100 and VCP 0x60 input at 15/18 (DisplayPort). AMD read requests must use separate send and receive transactions; the driver's combined form returned inconsistent bytes. AMD writes are experimental and are enabled only when `amd-experimental.flag` exists beside the executable. Physical brightness and input writes on AMD still need validation.
- A second monitor on that computer, identified as `SAM7488`, exposes a standard DDC handle and reports brightness 7/50. Its slider uses that maximum independently.

Monitor-dependent actions vary by display, GPU driver, cable and active connection. The preview does not guess an unadvertised input code on a generic monitor. The LG 29WK600's three known inputs are HDMI 1 (`17`), HDMI 2 (`18`) and DisplayPort (`15`).

## Build and checks

Use the .NET 10 SDK on Windows:

```powershell
dotnet build .\wpf\SwitchMonitor.Wpf\SwitchMonitor.Wpf.csproj
dotnet run --project .\wpf\MonitorProbe\MonitorProbe.csproj -- --selftest-return-sync
dotnet run --project .\wpf\ProfileProbe\ProfileProbe.csproj -- --selftest-shortcuts
dotnet run --project .\wpf\UpdateProbe\UpdateProbe.csproj
dotnet run --project .\wpf\AmdProbe\AmdProbe.csproj -- --packet-test
```

These checks do not change monitor settings. `MonitorProbe` without a self-test argument enumerates local monitors and reads DDC capabilities. `AmdProbe` without `--packet-test` writes a read-only `amd-probe.txt` report next to its executable.

The [GitHub Actions workflow](../.github/workflows/wpf-preview.yml) builds the source and uploads an **unsigned** portable artifact. It is a source-verifiable preview, not a signed release. To publish locally:

```powershell
dotnet publish .\wpf\SwitchMonitor.Wpf\SwitchMonitor.Wpf.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\wpf\dist\portable
```

Copy `monitor-switch.ico` and `brightness-sun.ico` into the output folder before running it. A portable copy has no `installed-wpf.flag`, so **Start with Windows** and automatic installation remain unavailable. Do not distribute a build containing `amd-experimental.flag` as a general release while AMD writes are unvalidated.

The installer script is in `packaging/`. The existing local packaging script expects .NET and Inno Setup under `.build-tools/` and emits a WPF-specific installer plus `SHA256-WPF.json`. No WPF installer has been publicly released. The WPF updater requires both `SwitchMonitor-WPF-Setup-X.Y.Z.exe` and a matching `SHA256-WPF.json` in the same GitHub release. It ignores AutoHotkey installer assets.

The application connects to GitHub for release checks as described in the [privacy notice](../PRIVACY.md). The source is under the repository's [MIT License](../LICENSE).
