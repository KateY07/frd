# FRD v3.pre2

Windows x64，Native AOT 单文件 `FRD.exe`，无需安装 .NET 运行时；FFmpeg 扩展包与 `frd-wintools.exe` 的用法同 v3.pre1。

- 界面层拆出为独立开源库 [Glasspane](https://github.com/KateY07/Glasspane)（MIT），FRD 以子模块 `external/Glasspane` 引用，行为不变。克隆本仓库请用 `--recurse-submodules`。
- 修复高 DPI（如 200% 缩放）下主控端毛玻璃错位：模糊区域与其后的视频内容对不上，面板中间还有一条接缝。
- 功能、协议、端口、预设 JSON 均无变化，可与 v3.pre1 互连。
