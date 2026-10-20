# FRD v2.pre15

Windows x64，依赖 .NET 10 x64 Runtime，不自包含，不启用 Native AOT。

- 发布包大幅瘦身：解压后 323 MiB → 约 138 MiB，zip 123 MiB → 约 52 MiB。
  - 只随包提供 FRD 实际调用的 FFmpeg 库（`avcodec`、`avutil`、`swscale`，以及 avcodec 自身依赖的 `swresample`）和 LICENSE，不再带 `avfilter`、`avformat`、`avdevice`、`ffplay`、`ffmpeg.exe`、`ffprobe`（约 160 MiB，代码从未调用）。
  - 目标框架由 `net10.0-windows10.0.26100.0` 改为 `net10.0-windows`，不再打入未使用的 Windows SDK 投影（`Microsoft.Windows.SDK.NET.dll`，约 25 MiB），`FRD.exe` 54.7 → 29.0 MiB。
- 功能、协议、端口、界面均无变化，主被控端无需同时升级。

验证：本机控制浮层回归、CPU 预设回归、远程会话（450 帧全部 GPU 确认）和 `--version` 均通过；编解码回归（`--codec-regression`）有 17 项 `keyFrame` 判定为假，改动前的构建上完全相同，不是本版引入。若自行替换 `ffmpeg\` 目录，需保留上述四个库。
