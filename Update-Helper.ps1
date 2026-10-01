param(
    [Parameter(Mandatory=$true)][string]$Installer,
    [Parameter(Mandatory=$true)][string]$ExpectedHash,
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string]$AppDir,
    [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
$notice = $null
try {
    $expected = $ExpectedHash.ToUpperInvariant()
    if ($expected -notmatch '^[0-9A-F]{64}$') { throw 'Invalid checksum.' }
    $actual = (Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash
    if ($actual -ne $expected) { throw 'The installer checksum does not match the published release.' }
    if ($VerifyOnly) { exit 0 }
    $running = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($running) { $running.WaitForExit(30000) | Out-Null }
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $notice = New-Object System.Windows.Forms.NotifyIcon
        $notice.Icon = [System.Drawing.SystemIcons]::Information
        $notice.Text = 'SwitchMonitor update'
        $notice.Visible = $true
        $notice.ShowBalloonTip(5000, 'SwitchMonitor update',
            'Installing SwitchMonitor...', [System.Windows.Forms.ToolTipIcon]::Info)
    } catch { }
    $setup = Start-Process -FilePath $Installer -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS' -Wait -PassThru
    if ($setup.ExitCode -ne 0) { throw "Installer exited with code $($setup.ExitCode)." }
    $exe = Join-Path $AppDir 'SwitchMonitor.exe'
    $script = Join-Path $AppDir 'switchMonitor.ahk'
    if (Test-Path -LiteralPath $script) {
        Start-Process -FilePath $exe -ArgumentList ('"' + $script + '" --activate') -WorkingDirectory $AppDir
    } else {
        Start-Process -FilePath $exe -ArgumentList '--activate' -WorkingDirectory $AppDir
    }
} catch {
    if ($VerifyOnly) { Write-Error $_.Exception.Message; exit 1 }
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show($_.Exception.Message, 'SwitchMonitor update') | Out-Null
    exit 1
} finally {
    if ($notice) { $notice.Dispose() }
}
