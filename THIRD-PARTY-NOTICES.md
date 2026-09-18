# 第三方依赖

程序通过 FFmpeg.AutoGen 使用 FFmpeg 动态库，并使用 Avalonia、Vortice 及 .NET。各依赖保留其原有许可证；本文件不替代这些许可证，也不为本项目另行指定许可证。

开发采用 Gyan.dev 的 FFmpeg 8.1.2 full shared 构建，来源和 SHA-256 固定在 `third_party/ffmpeg/provenance.json`。该构建包含 GPL 组件，许可证随运行包放在 `ffmpeg/LICENSE.txt`。仓库不包含 FFmpeg 二进制；获取脚本保持提供者分发包不变。第三方构建、源码及许可证信息：[Gyan.dev](https://www.gyan.dev/ffmpeg/builds/)、[FFmpeg](https://ffmpeg.org/legal.html)。

向他人分发含 FFmpeg 的完整运行包时，应遵守该构建的许可证及对应源码提供义务。这里提供的构建命令不替代分发者履行这些义务。
