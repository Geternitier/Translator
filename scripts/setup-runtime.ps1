param(
    [string]$RuntimeDirectory = $env:TRANSLATER_RUNTIME_DIR,
    [ValidateSet('Cuda', 'Vulkan')]
    [string]$GgufBackend
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\runtime.assets.json') -Raw | ConvertFrom-Json
if (!$GgufBackend) { $GgufBackend = $manifest.defaultBackend }
$engine = $manifest.engines.PSObject.Properties[$GgufBackend].Value
if (!$engine) { throw "Unknown llama.cpp backend $GgufBackend." }

if ([string]::IsNullOrWhiteSpace($RuntimeDirectory)) { $RuntimeDirectory = Join-Path $PSScriptRoot '..\runtime' }
$runtimePath = [IO.Path]::GetFullPath($RuntimeDirectory)
$runtimePrefix = $runtimePath.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
New-Item -ItemType Directory -Force -Path $runtimePath | Out-Null

function Runtime-Child([string] $RelativePath) {
    if ([IO.Path]::IsPathRooted($RelativePath)) { throw "Expected a relative asset path $RelativePath." }
    $path = [IO.Path]::GetFullPath((Join-Path $runtimePath $RelativePath))
    if (!$path.StartsWith($runtimePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid asset path $RelativePath." }
    return $path
}

function Receive-Asset($Asset) {
    $destination = Runtime-Child $Asset.path
    if ($Asset.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw "Missing SHA-256 for $destination." }
    if ((Test-Path -LiteralPath $destination -PathType Leaf) -and
        (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $Asset.sha256) {
        return $destination
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    $partial = "$destination.partial-$([guid]::NewGuid().ToString('N'))"
    try {
        Write-Host "Downloading $([IO.Path]::GetFileName($destination))"
        Invoke-WebRequest -Uri $Asset.url -OutFile $partial -TimeoutSec $manifest.downloadTimeoutSeconds
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $Asset.sha256) {
            throw "SHA-256 mismatch for $destination."
        }
        Move-Item -LiteralPath $partial -Destination $destination -Force
        return $destination
    }
    finally {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial }
    }
}

foreach ($asset in $manifest.files) { Receive-Asset $asset | Out-Null }

$target = Runtime-Child $engine.directory
$missing = @($engine.requiredFiles | Where-Object { !(Test-Path -LiteralPath (Join-Path $target $_) -PathType Leaf) })
if ($missing.Count -ne 0) {
    $stage = Runtime-Child ".llama-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $stage | Out-Null
    try {
        foreach ($archive in $engine.archives) {
            $zip = Receive-Asset $archive
            Expand-Archive -LiteralPath $zip -DestinationPath $stage -Force
        }
        foreach ($file in $engine.requiredFiles) {
            if (!(Test-Path -LiteralPath (Join-Path $stage $file) -PathType Leaf)) { throw "Missing llama.cpp file $file." }
        }

        if (!(Test-Path -LiteralPath $target)) { [IO.Directory]::Move($stage, $target) }
        else {
            Get-ChildItem -LiteralPath $stage -File | Sort-Object { $_.Name -eq 'llama-server.exe' } |
                ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $target -Force }
        }
    }
    finally {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    }
}
Write-Host "Runtime ready at $runtimePath"
