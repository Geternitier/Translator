# Translator for Windows

[返回项目首页](../README.md)

## 安装与启动

从源码构建需要 Windows 11 x64 和 .NET 10 SDK。使用 CUDA 时，需要与 llama.cpp 运行库兼容的 NVIDIA 驱动。

在仓库根目录运行以下 PowerShell 命令。

```powershell
./scripts/setup-runtime.ps1
dotnet build -c Release
./scripts/start-desktop.ps1 -Configuration Release
```

初始化脚本下载固定版本的 llama.cpp、PaddleOCR 和 Whisper 运行资产，校验 SHA-256 后保存到 `runtime`。默认安装 CUDA 运行包。使用 Vulkan 时，执行 `./scripts/setup-runtime.ps1 -GgufBackend Vulkan`。初始化完成后，识别与翻译均可离线运行。

## 托盘与模型设置

启动后，左击或右击托盘图标都会打开功能菜单，点击菜单里的「设置」打开设置页。图标也可能位于 Windows 通知区域的 `↑` 菜单中。启动脚本可加 `-Settings` 直接打开设置。再次启动程序会打开已有实例的设置页。

首次使用，在设置中选择本地模型目录和 GGUF 文件，然后保存。启用 GPU 后，应用优先尝试 CUDA，再尝试已安装的 Vulkan。开启 CPU 回退后，GPU 不可用时可以使用 CPU。设置页显示实际加载的后端。

可以从托盘手动释放模型。没有活动功能时，模型会在达到空闲时限后自动释放。

| 功能 | 操作 |
| --- | --- |
| 选区翻译 | 从托盘启用。选中文字后可复制、拖动或关闭译文浮层。 |
| 持续 OCR | 按 `Ctrl + Alt + O` 画框。译文浮层的停止图标暂停识别并保留译文，关闭图标结束该 OCR 会话。 |
| 音频字幕 | 从托盘选择程序，或按 `Ctrl + Alt + S` 开关上次的来源。停止图标保留字幕历史，关闭图标关闭字幕。拖动浮层顶部可保存位置。 |

选区翻译、OCR 译文和音频字幕都使用半透明浮层，并在生成时逐步显示译文。鼠标移入译文区域时，复制、停止和关闭图标出现在浮层右上方。复制会取当前译文，音频字幕会复制窗口保留的译文历史。选区翻译的停止图标会取消当前请求并保留已显示的部分。

设置里的「译文字号」同时适用于三种译文。保存后，已经打开的浮层也会立即调整。

## 音频转写模型

在设置的「音频转写模型」中选择后端。旧设置默认沿用 Whisper Base。正在使用音频字幕时，保存新的后端会重启当前音频会话。

| 后端 | 可下载模型 | 行为 |
| --- | --- | --- |
| Nemotron 3.5 ASR Streaming 0.6B | [160 ms 多语种 INT8](https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-160ms-int8-2026-06-11.tar.bz2) · [560 ms 多语种 INT8](https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11.tar.bz2) | 在 CPU 上增量解码，输出原文预览、稳定前缀和定稿。两档都安装时优先使用 160 ms。 |
| Qwen3-ASR 0.6B | [sherpa-onnx INT8](https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25.tar.bz2) | 在 CPU 上按停顿和时长切块，逐块输出定稿。Qwen 官方 vLLM 流式服务使用另一套运行时。 |

手动下载时，把压缩包解压到 `runtime/asr`。若已设置 `TRANSLATER_RUNTIME_DIR`，默认位置为其下的 `asr`。设置里的「转写模型目录」可填写模型本身的目录或多个模型的共同父目录。应用按必要文件识别模型，子目录可以重命名。使用原始目录名可以保留 Nemotron 两种时延版本的优先顺序。也可用初始化脚本下载并校验所选模型。

```powershell
./scripts/setup-runtime.ps1 -AsrModels Nemotron,Qwen3Asr -DownloadTimeoutSeconds 600
```

若只使用其中一个模型，传入 `Nemotron` 或 `Qwen3Asr` 即可。两个 ONNX 后端都支持固定英语、固定日语和自动识别。自动模式下，当前 sherpa-onnx .NET 接口只向字幕会话返回文本，中文过滤沿用原有文本判断。

## 区分说话者

安装可选的 NeMo-Speech.cpp Windows CPU 运行库和 Nemotron 3 Diarization 模型，然后在设置中勾选「音频字幕区分说话者」。脚本固定运行库版本并校验下载。

```powershell
./scripts/setup-runtime.ps1 -SpeakerDiarization
```

说话者分段与转写并行执行，标签可以在译文显示后补到原字幕行。相邻字幕属于同一位说话者时，只在第一条显示编号。编号在当前音频会话内有效，检测到同时发言时显示「多人重叠」。 [NVIDIA 模型](https://huggingface.co/nvidia/Nemotron-3-Diarization)

## 本机配置

设置保存在 `%LOCALAPPDATA%\Translater\settings.json`。OCR 译文和音频字幕窗口的位置保存在同一目录的 `overlay-positions.json`。

推理预算、采集等待和音频分段参数有内置默认值。高级调优可在同一目录放置完整的 `translator.runtime.json`，或用 `TRANSLATOR_CONFIG_PATH` 指定配置文件。用 `TRANSLATER_RUNTIME_DIR` 可指定运行资产目录。

## 检查模型与运行时

```powershell
dotnet exec src/Translator.Desktop/bin/Release/net10.0/Translator.Desktop.dll --check-models
```

检查输出包含所选 GGUF 模型及文件状态、推理运行时候选顺序、音频转写后端及解析到的模型路径、说话者标注可用状态和可选音频程序数。其他验证用例见[技术方案](技术方案.html#ports)。
