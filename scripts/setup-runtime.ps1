param(
    [string]$RuntimeDirectory = $env:TRANSLATER_RUNTIME_DIR,
    [ValidateSet('Cuda', 'Vulkan')]
    [string]$GgufBackend = 'Cuda',
    [ValidateSet('Nemotron', 'Qwen3Asr')]
    [string[]]$AsrModels = @(),
    [switch]$SpeakerDiarization,
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

$asrPackages = @{
    Nemotron = @{
        name = 'sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-160ms-int8-2026-06-11'
        sha256 = 'a81909a1780d84cff16d73c15e13e67d9d81d8839faf14870d507d8499f7a61a'
        requiredFiles = @('encoder.int8.onnx', 'decoder.int8.onnx', 'joiner.int8.onnx', 'tokens.txt')
    }
    Qwen3Asr = @{
        name = 'sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25'
        sha256 = '393f8a14e2f5fb96746aaab342997a40641001fbd5bf9592a080a8329178ee96'
        requiredFiles = @('conv_frontend.onnx', 'encoder.int8.onnx', 'decoder.int8.onnx', 'tokenizer/merges.txt',
            'tokenizer/vocab.json', 'tokenizer/tokenizer_config.json')
    }
}

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

function Install-AsrModel($Package) {
    $name = $Package.name
    $modelRoot = Runtime-Child 'asr'
    $modelTarget = Runtime-Child "asr/$name"
    $missing = @($Package.requiredFiles | Where-Object { !(Test-Path -LiteralPath (Join-Path $modelTarget $_) -PathType Leaf) })
    if ($missing.Count -eq 0) { return }

    $archive = Receive-Asset @{
        path = "asr/$name.tar.bz2"
        url = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/$name.tar.bz2"
        sha256 = $Package.sha256
    }
    $stage = Runtime-Child "asr/.model-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $stage | Out-Null
    try {
        & tar.exe -xf $archive -C $stage
        if ($LASTEXITCODE -ne 0) { throw "Failed to extract $archive." }
        $extracted = Join-Path $stage $name
        foreach ($file in $Package.requiredFiles) {
            if (!(Test-Path -LiteralPath (Join-Path $extracted $file) -PathType Leaf)) { throw "Missing ASR file $file." }
        }
        if (!(Test-Path -LiteralPath $modelTarget)) { [IO.Directory]::Move($extracted, $modelTarget) }
        else {
            foreach ($file in $missing) {
                $destination = Join-Path $modelTarget $file
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
                Copy-Item -LiteralPath (Join-Path $extracted $file) -Destination $destination
            }
        }
    }
    finally {
        $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
        if ([IO.Path]::GetDirectoryName($resolvedStage) -eq $modelRoot -and $resolvedStage -eq $stage) {
            Remove-Item -LiteralPath $resolvedStage -Recurse -Force
        }
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
foreach ($model in $AsrModels) { Install-AsrModel $asrPackages[$model] }
if ($SpeakerDiarization) {
    $diarizationPackage = 'nemo-speech-0.3.0-windows-x86_64-cpu'
    $diarizationTarget = Runtime-Child "diarization/$diarizationPackage"
    $diarizationFiles = @('nemo_speech_asr_c.dll', 'nemo_speech_asr.dll', 'ggml.dll', 'ggml-base.dll', 'ggml-cpu.dll')
    $diarizationMissing = @($diarizationFiles | Where-Object {
        !(Test-Path -LiteralPath (Join-Path $diarizationTarget "bin/$_") -PathType Leaf)
    })
    if ($diarizationMissing.Count -gt 0) {
        $archive = Receive-Asset @{
            path = "diarization/$diarizationPackage.zip"
            url = "https://github.com/NVIDIA/NeMo-Speech.cpp/releases/download/v0.3.0/$diarizationPackage.zip"
            sha256 = '4ed9740e361e4653f56c3dce7d1e81883a1c8b831c1bd7d01567fff1130b5b05'
        }
        Expand-Archive -LiteralPath $archive -DestinationPath (Runtime-Child 'diarization') -Force
    }
    Receive-Asset @{
        path = 'diarization/Nemotron-3-Diarization.q8_0.gguf'
        url = 'https://huggingface.co/nvidia/Nemotron-3-Diarization/resolve/6a002fd9cad5e8dfa624d97a7ee0388a7153243d/Nemotron-3-Diarization.q8_0.gguf'
        sha256 = '08456d9e22cd9a323c0364d98375f3746d6e68507ebb705cd46438c534c7a3a1'
    } | Out-Null
}
Write-Host "Runtime ready at $runtimePath"
