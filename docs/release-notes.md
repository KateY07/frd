# FRD v3.pre1

Windows x64，**Native AOT 单文件 `FRD.exe`（约 10.8 MB）**，无需安装 .NET 运行时；FFmpeg 改为可选扩展包。

- 默认编解码改用 Windows 内置 Media Foundation（H.264）：优先使用显卡硬件编码（如 Intel Quick Sync，异步 MFT），不可用时回退内置软件编码；解码用系统 H.264 解码器。基础包不再携带任何 FFmpeg 文件。
- FFmpeg 为可选扩展包 `frd-v3.pre1-ffmpeg-win-x64.zip`：解压到 `FRD.exe` 同目录（生成 `ffmpeg\`）后，`libx264rgb`、H.265、AV1 等 FFmpeg 预设自动可用；未安装时这些预设标记为不可用，默认预设为 `mf_h264`。没有任何可用编解码器时以退出码 2 结束。
- 界面由 Avalonia 改为原生 Win32 + Direct3D11 + Direct2D/DirectWrite（视频背后的高斯模糊、悬浮面板、悬浮球、被控端卡片、`»` 折叠与 10 秒自动收起动画均保留）；被控端毛玻璃改为自绘模糊，不再依赖 DWM 亚克力。
- Windows 辅助服务（安全桌面/UAC/锁屏的抓屏与输入）拆为独立的 `frd-wintools.exe`（Native AOT，约 3 MB，随基础包与 `FRD.exe` 同目录）：`FRD.exe` 只负责查询服务状态，点击“安装辅助服务”时以管理员身份启动 `frd-wintools.exe --install-secure-helper`；`FRD.exe` 不再接受 `--install-secure-helper`/`--secure-service`/`--secure-worker`。服务名不变，旧版安装的服务指向 `FRD.exe`，升级后需再点一次安装。
- 体积：发布包解压后约 138 MiB → 约 11 MiB，zip 约 52 MiB → 约 6 MiB（FFmpeg 扩展包另计，约 50 MiB）。
- 协议、端口、预设 JSON 结构不变（预设参数新增 `-c:v mf-h264`、`-rc`、`-hw` 等 Media Foundation 选项）；主被控端建议同时升级。

验证：Media Foundation 编解码回归（含强制关键帧、SPS/PPS、PSNR）、本机控制浮层回归、CPU 预设回归（带 FFmpeg 扩展）、本机回环远程会话（221 帧、219 帧 GPU 确认）均在 Native AOT 构建上通过。
