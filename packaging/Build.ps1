param([string]$AhkBase = 'C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolsDir = Join-Path $root '.build-tools'
$package = Join-Path $root 'dist\SwitchMonitor'
$compiler = Join-Path $toolsDir 'Ahk2Exe\Ahk2Exe.exe'
$iscc = Join-Path $toolsDir 'InnoSetup\ISCC.exe'
foreach ($required in @($AhkBase, $compiler, $iscc, "$toolsDir\ControlMyMonitor\ControlMyMonitor.exe")) {
    if (!(Test-Path -LiteralPath $required)) { throw "Falta herramienta: $required. Ver packaging\BUILD-TOOLS.txt" }
}
New-Item -ItemType Directory -Path $package, "$package\licenses", "$package\source\packaging" -Force | Out-Null
& "$PSScriptRoot\Make-Icon.ps1"
$arguments = '/in "{0}\switchMonitor.ahk" /out "{1}\SwitchMonitor.exe" /icon "{0}\monitor-switch.ico" /base "{2}" /compress 0 /silent verbose' -f $root, $package, $AhkBase
$compile = Start-Process -FilePath $compiler -ArgumentList $arguments -WorkingDirectory $root -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$toolsDir\compile-output.txt" -RedirectStandardError "$toolsDir\compile-errors.txt"
if ($compile.ExitCode -ne 0 -or !(Test-Path -LiteralPath "$package\SwitchMonitor.exe")) {
    Get-Content -LiteralPath "$toolsDir\compile-output.txt", "$toolsDir\compile-errors.txt"
    throw "Ahk2Exe fallo: $($compile.ExitCode)"
}
Copy-Item -LiteralPath "$toolsDir\ControlMyMonitor" -Destination $package -Recurse -Force
foreach ($file in @('monitor-switch.ico','monitor-switch.png')) { Copy-Item -LiteralPath "$root\$file" -Destination $package -Force }
Copy-Item -LiteralPath "$PSScriptRoot\LEEME.txt" -Destination $package -Force
Copy-Item -LiteralPath 'C:\Program Files\AutoHotkey\license.txt' -Destination "$package\licenses\AutoHotkey.txt" -Force
foreach ($file in @('switchMonitor.ahk','settingsUi.ahk','intelLegacyDdc.ahk','amdLgDdc.ahk','appPaths.ahk','returnSyncState.ahk','intel-ddc-selftest.ahk','amd-ddc-selftest.ahk','monitor-switch.png')) {
    Copy-Item -LiteralPath "$root\$file" -Destination "$package\source" -Force
}
Get-ChildItem -LiteralPath $PSScriptRoot -File | Copy-Item -Destination "$package\source\packaging" -Force
[IO.File]::WriteAllText("$package\portable.flag", 'Keep settings in .\data')
& $iscc '/Qp' "$PSScriptRoot\SwitchMonitor.iss"
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup fallo.' }
$zip = "$root\dist\SwitchMonitor-Portable-1.2.0.zip"
$releaseFiles = Get-ChildItem -LiteralPath $package | Where-Object { $_.Name -ne 'data' }
Compress-Archive -LiteralPath $releaseFiles.FullName -DestinationPath $zip -Force
Get-FileHash -LiteralPath "$root\dist\SwitchMonitor-Setup-1.2.0.exe", $zip -Algorithm SHA256 |
    Select-Object @{n='File';e={[IO.Path]::GetFileName($_.Path)}}, Hash |
    ConvertTo-Json | Set-Content -LiteralPath "$root\dist\SHA256.json" -Encoding UTF8
Write-Output 'Compilacion terminada: instalador y ZIP portable en dist.'
