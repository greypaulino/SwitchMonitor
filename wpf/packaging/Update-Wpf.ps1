param(
    [Parameter(Mandatory=$true)][string]$Installer,
    [Parameter(Mandatory=$true)][string]$ExpectedHash,
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string]$AppDir,
    [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
try {
    if ($ExpectedHash -notmatch '^[0-9a-fA-F]{64}$') { throw 'Invalid update checksum.' }
    if ((Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash -ne $ExpectedHash.ToUpperInvariant()) {
        throw 'The downloaded installer does not match its published SHA-256 hash.'
    }
    if ($VerifyOnly) { exit 0 }
    $running = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($running -and !$running.WaitForExit(30000)) { throw 'SwitchMonitor did not exit before installation.' }
    $setup = Start-Process -FilePath $Installer -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS' -Wait -PassThru -WindowStyle Hidden
    if ($setup.ExitCode -ne 0) { throw "Installer exited with code $($setup.ExitCode)." }
    $exe = Join-Path $AppDir 'SwitchMonitor.Wpf.exe'
    if (!(Test-Path -LiteralPath $exe)) { throw 'Updated application was not found.' }
    $markerDir = Join-Path $env:LOCALAPPDATA 'SwitchMonitor-Wpf'
    New-Item -ItemType Directory -Path $markerDir -Force | Out-Null
    $version = [regex]::Match([IO.Path]::GetFileName($Installer), '^SwitchMonitor-WPF-Setup-(\d+\.\d+\.\d+)\.exe$').Groups[1].Value
    if ($version) { Set-Content -LiteralPath (Join-Path $markerDir 'update-complete.txt') -Value $version -Encoding UTF8 }
    Start-Process -FilePath $exe -ArgumentList '--background' -WorkingDirectory $AppDir -WindowStyle Hidden
} catch {
    if ($VerifyOnly) { Write-Error $_.Exception.Message; exit 1 }
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show($_.Exception.Message, 'SwitchMonitor update') | Out-Null
    exit 1
}
