param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Settings
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$assembly = Join-Path $root "src/Translator.Desktop/bin/$Configuration/net10.0/Translator.Desktop.dll"
if (!(Test-Path -LiteralPath $assembly -PathType Leaf)) {
    throw "请先在仓库根目录运行 dotnet build -c $Configuration。"
}

$start = [System.Diagnostics.ProcessStartInfo]::new()
$start.FileName = (Get-Command dotnet.exe -CommandType Application).Source
$start.WorkingDirectory = $root
$start.Arguments = '"' + $assembly + '"'
if ($Settings) { $start.Arguments += ' --settings' }
# Use the installed .NET host so a low-integrity workspace apphost does not block Explorer tray registration.
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
[System.Diagnostics.Process]::Start($start).Dispose()
