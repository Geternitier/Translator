param(
    [string]$RuntimeDirectory = $env:TRANSLATER_RUNTIME_DIR,
    [ValidateSet('Cuda', 'Vulkan')]
    [string]$GgufBackend = 'Cuda',
    [ValidateRange(1, 3600)]
    [int]$DownloadTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$assets = @(
    @{
        path = 'ocr/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx'
        url = 'https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv5/cls/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx'
        sha256 = '54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7'
    }
    @{
        path = 'ocr/ch_PP-OCRv5_det_mobile.onnx'
        url = 'https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv5/det/ch_PP-OCRv5_det_mobile.onnx'
        sha256 = '4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae'
    }
    @{
        path = 'ocr/ch_PP-OCRv5_rec_mobile.onnx'
        url = 'https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile.onnx'
        sha256 = '5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5'
    }
    @{
        path = 'ocr/ppocrv5_dict.txt'
        url = 'https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/paddle/PP-OCRv5/rec/ppocrv5_dict.txt'
        sha256 = 'd1979e9f794c464c0d2e0b70a7fe14dd978e9dc644c0e71f14158cdf8342af1b'
    }
    @{
        path = 'ggml-base-q5_1.bin'
        url = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base-q5_1.bin'
        sha256 = '422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898'
    }
)

$engines = @{
    Cuda = @{
        directory = 'llama-cuda'
        archives = @(
            @{
                path = 'llama-b12631-bin-win-cuda-13.5-x64.zip'
                url = 'https://github.com/ggml-org/llama.cpp/releases/download/b12631/llama-b12631-bin-win-cuda-13.5-x64.zip'
                sha256 = '3880f2092689cb0085f85b1ac569caad693aabd825556ab953f238673e6e71c0'
            }
            @{
                path = 'cudart-llama-bin-win-cuda-13.5-x64.zip'
                url = 'https://github.com/ggml-org/llama.cpp/releases/download/b12631/cudart-llama-bin-win-cuda-13.5-x64.zip'
                sha256 = 'c2bf4cc202bfbaaa7a0b75f5082457c3811326947bb216059b72839d844e9cba'
            }
        )
        requiredFiles = @(
            'llama-server.exe'
            'llama-server-impl.dll'
            'ggml-cuda.dll'
            'cudart64_13.dll'
            'cublas64_13.dll'
            'cublasLt64_13.dll'
        )
    }
    Vulkan = @{
        directory = 'llama-vulkan'
        archives = @(
            @{
                path = 'llama-b12631-bin-win-vulkan-x64.zip'
                url = 'https://github.com/ggml-org/llama.cpp/releases/download/b12631/llama-b12631-bin-win-vulkan-x64.zip'
                sha256 = 'f54121061f29cfcd59a3e3ca17870c41957f4a226146185ab6c5297258203fc4'
            }
        )
        requiredFiles = @(
            'llama-server.exe'
            'llama-server-impl.dll'
            'ggml-vulkan.dll'
        )
    }
}
$engine = $engines[$GgufBackend]

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
        Invoke-WebRequest -Uri $Asset.url -OutFile $partial -TimeoutSec $DownloadTimeoutSeconds
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

foreach ($asset in $assets) { Receive-Asset $asset | Out-Null }

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
