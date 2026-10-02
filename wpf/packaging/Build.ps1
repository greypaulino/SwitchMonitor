param([string]$Version = '0.1.1')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$wpf = Join-Path $root 'wpf'
$dotnet = Join-Path $root '.build-tools\dotnet\dotnet.exe'
$iscc = Join-Path $root '.build-tools\InnoSetup\ISCC.exe'
if (!(Test-Path -LiteralPath $dotnet) -or !(Test-Path -LiteralPath $iscc)) {
    throw 'The local .NET SDK and Inno Setup compiler are required.'
}
$env:DOTNET_CLI_HOME = Join-Path $root '.build-tools'
$env:NUGET_PACKAGES = Join-Path $root '.build-tools\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$package = Join-Path $wpf 'dist\package-build'
& $dotnet publish (Join-Path $wpf 'SwitchMonitor.Wpf\SwitchMonitor.Wpf.csproj') `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:PublishTrimmed=false -p:EnableCompressionInSingleFile=true `
    "-p:Version=$Version" --no-restore -o $package
if ($LASTEXITCODE -ne 0) { throw 'WPF publish failed.' }
& $iscc '/Qp' "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'SwitchMonitor-Wpf.iss')
if ($LASTEXITCODE -ne 0) { throw 'WPF installer compilation failed.' }
$installer = Join-Path $wpf "dist\SwitchMonitor-WPF-Setup-$Version.exe"
$manifest = @(@{ File = [IO.Path]::GetFileName($installer); Hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash })
ConvertTo-Json -InputObject $manifest -Depth 3 |
    Set-Content -LiteralPath (Join-Path $wpf 'dist\SHA256-WPF.json') -Encoding UTF8
Write-Output "Built $installer and SHA256-WPF.json"
