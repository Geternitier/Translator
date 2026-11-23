# Translator

Windows 托盘里的本地翻译工具，支持英语、日语到简体中文。

- **选区翻译**。从托盘启用。选中文字后，译文在选区旁出现。可以复制、关闭或拖动当前浮层。
- **持续 OCR**。按 `Ctrl + Alt + O` 画框。边缘拖动，角落缩放，悬停可暂停或关闭。译文窗口可以拖动并记住位置。
- **音频字幕**。从托盘选择要翻译的程序，或按 `Ctrl + Alt + S` 开关上次的来源。字幕流式显示，空档时按段落修订。标题栏可以拖动并记住位置。

程序跳过检测为中文的内容。三个入口使用设置里选定的同一个 GGUF 模型。

## 运行

需要 Windows 11 x64 和 .NET 10 SDK。CUDA 路径需要与随附运行库兼容的 NVIDIA 驱动。

```powershell
./scripts/setup-runtime.ps1
dotnet build -c Release
./scripts/start-desktop.ps1 -Configuration Release
```

初始化脚本下载固定版本的 llama.cpp、PaddleOCR 和 Whisper 运行资产，校验 SHA-256 后保存在 `runtime`。Vulkan 设备可传入 `-GgufBackend Vulkan`。初始化完成后，识别与翻译均可离线运行。

在托盘左击打开设置，右击查看功能菜单。Windows 的通知区域可能把图标收到 `↑` 内。启动脚本可加 `-Settings` 直接打开设置。程序保持单实例，后续启动会请求已有实例打开设置。

## 模型和配置

1. 准备一个本地 GGUF 模型。在设置中选择模型目录和要使用的文件。推荐 Qwen3.5-9B Q4_K_M，显存较小时可选 Qwen3.5-4B Q4_K_M。
2. 启用 GPU 时优先使用 CUDA，然后尝试已安装的 Vulkan。启用 CPU 回退时允许使用 CPU。设置页显示实际加载的后端。
3. 可以从托盘手动释放模型。应用会在无活动功能且达到空闲时限时释放模型。

推理预算、采集等待和音频分段参数有内置默认值。高级调优可使用本机数据目录里的 `translator.runtime.json`，也可用 `TRANSLATOR_CONFIG_PATH` 指定完整配置。

设置页将模型目录、所选文件和其他个人偏好保存到本机的 `%LOCALAPPDATA%\Translater\settings.json`。用 `TRANSLATER_RUNTIME_DIR` 可指定运行资产目录。OCR 和音频位置保存在同一设置目录里的 `overlay-positions.json`。

## 检查

```powershell
dotnet exec src/Translator.Desktop/bin/Release/net10.0/Translator.Desktop.dll --check-models
```

检查输出包含模型文件状态、运行时候选顺序和可选音频程序数。Release 构建和运行前可参阅 [技术方案](docs/技术方案.html) 中的验证用例。

## 文档与许可

- [技术方案、类图与时序图](docs/技术方案.html)
- [流式音频字幕设计](docs/流式音频字幕设计.html)
- [MIT 许可证](LICENSE)

第三方运行时和模型适用各自的许可证。
