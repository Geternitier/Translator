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

启动后，左击托盘图标打开设置，右击查看功能菜单。图标也可能位于 Windows 通知区域的 `↑` 菜单中。启动脚本可加 `-Settings` 直接打开设置。再次启动程序会打开已有实例的设置页。

首次使用，在设置中选择本地模型目录和 GGUF 文件，然后保存。启用 GPU 后，应用优先尝试 CUDA，再尝试已安装的 Vulkan。开启 CPU 回退后，GPU 不可用时可以使用 CPU。设置页显示实际加载的后端。

可以从托盘手动释放模型。没有活动功能时，模型会在达到空闲时限后自动释放。

| 功能 | 操作 |
| --- | --- |
| 选区翻译 | 从托盘启用。选中文字后可复制、拖动或关闭译文浮层。 |
| 持续 OCR | 按 `Ctrl + Alt + O` 画框。拖边移动，拖角缩放，悬停时可暂停或关闭。译文窗口可拖动并记住位置。 |
| 音频字幕 | 从托盘选择程序，或按 `Ctrl + Alt + S` 开关上次的来源。拖动标题栏可调整并保存字幕位置。 |

## 本机配置

设置保存在 `%LOCALAPPDATA%\Translater\settings.json`。OCR 译文和音频字幕窗口的位置保存在同一目录的 `overlay-positions.json`。

推理预算、采集等待和音频分段参数有内置默认值。高级调优可在同一目录放置完整的 `translator.runtime.json`，或用 `TRANSLATOR_CONFIG_PATH` 指定配置文件。用 `TRANSLATER_RUNTIME_DIR` 可指定运行资产目录。

## 检查模型与运行时

```powershell
dotnet exec src/Translator.Desktop/bin/Release/net10.0/Translator.Desktop.dll --check-models
```

检查输出包含所选模型、目录内模型的文件状态、运行时候选顺序和可选音频程序数。其他验证用例见[技术方案](技术方案.html#ports)。
