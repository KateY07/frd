# FRD v5.1

Windows x64，Native AOT 单文件 `FRD.exe`，无需安装 .NET 运行时。与 v5.0、v4.pre1 及以后的版本可互连（协议未变），与 v3 不互通。

**FFmpeg 扩展改装到用户目录的版本文件夹**
- FFmpeg 扩展安装器 `frd-ffmpeg-8.1.2-setup.exe` 现在安装到 `%LOCALAPPDATA%\FRD\ffmpeg\<版本>\`（例如 `%LOCALAPPDATA%\FRD\ffmpeg\8.1.2\`），只写当前用户的目录：不再去找 FRD 在哪里、不需要选目录，所有 FRD 版本共用一份，单文件版 `frd-v5.1.exe` 也能用。已装同版本时询问是否替换，并逐个文件校验 SHA-256。
- FRD 自动查找 FFmpeg，顺序是：环境变量 `FRD_FFMPEG_DIR` 指定的目录；`FRD.exe` 旁的 `ffmpeg\` 文件夹（扩展 zip 解压后的位置，与以前相同）；`%LOCALAPPDATA%\FRD\ffmpeg\<版本>\`（取最新版本）。没找到时依赖 FFmpeg 的预设显示为不可用，提示“运行 FFmpeg 扩展安装器，或把其中的 ffmpeg 文件夹放到 FRD.exe 旁”。
- 无人值守：`frd-ffmpeg-8.1.2-setup.exe -y`；环境变量 `FFMPEG_TARGET` 可改安装目录。扩展 zip 仍可用于脚本。
- **注意**：新安装器装到用户目录，只有 v5.1 及以后的 FRD 会去那里找；v5.0 及更早的版本只认 `FRD.exe` 旁的 `ffmpeg\` 文件夹，请继续用 zip。

**发布内容**：`frd-v5.1-win-x64.zip`、`frd-v5.1.exe`（单文件版）、`frd-v5.1-ffmpeg-win-x64.zip` 与 `frd-ffmpeg-8.1.2-setup.exe`、各自的 SHA-256。FFmpeg 扩展也单独发布在 `ffmpeg-8.1.2` 这个 Release。没有代码签名，SmartScreen 可能提示。

**验证**：在 `FRD.exe` 旁没有 `ffmpeg\` 的情况下，FRD 从用户目录加载 FFmpeg 8.1.2 并通过 CPU 预设回归；安装器的默认路径、`FFMPEG_TARGET` 覆盖、重复安装均已实测。未覆盖：安装器窗口里的询问（替换/安装）只检查了协议输出，没有在窗口里点击。
