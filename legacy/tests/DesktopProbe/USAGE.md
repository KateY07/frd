# 本机编码与安全桌面专用测试

本工具独立于正式 FRD 发布入口，依赖 .NET 10 x64，复用正式 FFmpeg 后端、DXGI 捕获、UDP 和 GPU 呈现。不是 Native AOT 或自包含发布。

```powershell
dotnet build tests/DesktopProbe/DesktopProbe.csproj -c Release
.\tests\DesktopProbe\bin\Release\net10.0-windows10.0.26100.0\DesktopProbe.exe
```

启动后自动捕获普通桌面作为统一样本，对样本作固定滚动，不识别页面内容。候选在独立进程实测，原生初始化失败或超时不会冒充通过。固定 1280×720、30 FPS、5 Mbps，8 帧预热、36 帧计时；验证输出数量、PTS、解码尺寸、码率和像素误差，再按提交到对应码流产出的平均时间选择编码器。不是所有硬件、参数、内容下的绝对最低值，也不是画质相同的编码效率排名。

探测覆盖 x264 ultrafast、Intel H.264/HEVC/AV1 QSV、NVIDIA H.264/HEVC/AV1 NVENC P1、AMD H.264 AMF。Intel H.264 同时测软件解码和 D3D11 硬解。不支持的组合保留失败原因，不静默回退。

选定后运行真实屏幕捕获 → 编码 → localhost UDP → 解码 → GPU 呈现。UDP 使用正式分包协议和源端口登记，不按编码上限额外节流。界面只统计最近 1 秒的真实变化帧，另存累计平均；GPU 完成不代表物理扫描完成。当前仍使用 CPU 像素接口，编码包含颜色转换、上传和输出复制，硬解后仍有回读；不能宣称已经实现 GPU 零回读。

## 人工确认顺序

1. 检查普通桌面预览、自动选出的编码器和分层耗时。
2. 点击“启用锁屏 / UAC 捕获”，允许 Windows 提权提示。普通用户进程不能直接取得 SYSTEM 权限。
3. 等待界面显示 SYSTEM，点击“测试 UAC”，保留提示数秒再允许或取消。
4. 点击“锁屏测试”，数秒后自行解锁。工具不会记录、输入或代填密码。
5. 查看 UAC、锁屏的新图像计数，检查运行目录中的逐次证据图像和码流。两项均正确后点击“人工确认通过”。

启用高权限时，提权安装进程将辅助程序复制到 `%ProgramData%/FRD-DesktopProbe/<唯一名称>`，限制为管理员和 SYSTEM 可写，再启动按需 SYSTEM 服务。服务注册启动后立即删除，无开机自启；运行中的服务在窗口关闭或最长 20 分钟后终止其辅助进程。受保护程序副本保留供检查，不修改系统 UAC 策略。删除这些副本需要管理员权限。

SYSTEM 服务只把自身令牌复制到窗口所属会话并启动自己的捕获进程，不注入其他应用。捕获线程连接当前 input desktop，桌面切换或 DXGI 失效后销毁并重建捕获与编码对象；没有 GDI 假通过，也不承诺固定恢复时长。

普通桌面上无法同时肉眼观看被 UAC/锁屏遮住的预览窗口。工具在后台继续接收、解码，并保存安全桌面首次解码图像及每次最多 16 MiB 的压缩码流，解锁后可人工核验。此证据包含屏幕内容，请按需保留。发送端成功不等于接收端验证通过；必须有安全桌面真实新图像、接收解码记录及人工确认。

运行目录包含 `probe/ranking.json`、每种编码器日志、`demo-report.json`、`worker-status.json`、`secure-evidence-*.json`、`secure-decoded-*.bmp` 和安全桌面码流。失败和权限不足均保留完整异常。

可指定输出目录及自动关闭秒数：`DesktopProbe.exe "D:\path\run" 6`。秒数从自动探测和启动完成后计算；此模式只快测普通桌面，不会主动锁屏。`DesktopProbe.exe "D:\path\run" --secure` 在探测后请求提权并保留人工测试窗口。

本机普通、UAC、锁屏均通过并人工确认后，才进入 alipc 阶段。MX150 本身没有 NVENC，alipc 只能验证其实际可用后端和公共逻辑，不能代表 RTX 5070 Ti 性能。

发送端专项短测：`DesktopProbe.exe "D:\path\sender-run" --sender`。可见动画窗口制造真实桌面变化，依次比较原路径、同步像素缓冲复用、直接纹理缩放与原路径复测；每组预热 10 帧、计时 60 个变化帧，完成自动关闭。输出 `sender-results.json`，包含捕获、Map 等待、行复制、分配、编码、合计均值及 P95。每帧执行解码完整性检查，但解码不计入发送端耗时；内存及 GC 数字包含验证解码器，不能称为捕获层独有开销。

Demo 的同步捕获→编码线程已启用像素缓冲复用；编码完成后才允许下一次捕获覆盖。正式程序默认仍返回独立像素数组，避免影响异步消费者。直接纹理缩放仅保留为实验，不作为默认路径。

## 当前原始分辨率测试

当前目标以 README 的完整原始分辨率为准，旧的 720p 专项仅保留作历史对照。以下命令不改变显示器设置，读取 DXGI 原始纹理尺寸，不按 DPI 逻辑尺寸缩放。

- `DesktopProbe.exe "D:\path\fixture" --record-fixture`：打开文字、灰阶、色块及运动窗口，记录 90 帧原始 BGRA 和 manifest。后 30 帧重复一个实际捕获帧，用于排除其他应用更新对静止测试的干扰。完成自动关闭。
- `CodecLatency.exe --low-color "D:\path\ffmpeg" "D:\path\fixture" "D:\path\report" full`：逐个测试彩色、灰度 8/6/4/2/1 位、1 位抖动、RGB565/332、QSV 灰度对照。`quick` 仅 5 Mbps、`quick1` 仅 1 Mbps。输入尺寸来自 manifest，计时不含捕获或磁盘读取。
- `DesktopProbe.exe "D:\path\live" --native-sender`：真实原分辨率捕获→前处理→完整编码出包，逐帧解码验证但解码不计入发送端时间，窗口自动关闭。默认彩色、gray8/4/1、QSV gray8及末尾彩色复测。
- `DesktopProbe.exe "D:\path\gpu" --native-sender --gpu-sender`：原分辨率 GPU NV12→QSV 实验路径。当前 FFmpeg loader 绕过仅为诊断，未用于生产。

诊断配置仅通过本次进程环境变量设置：`FRD_SWS_THREADS=4` 测 FFmpeg 官方多线程转换（额外 BGRA 复制计入总耗时）；默认 1 保留原接口。`FRD_LOW_COLOR_SCALAR=1` 对照标量量化；默认 SIMD 在启用前执行 288 组精确等价自检。`FRD_LOW_COLOR_CANDIDATES`/`FRD_NATIVE_CANDIDATES` 以逗号筛选候选；`FRD_NATIVE_KBPS` 设置实时短测码率，默认 5000。

低位宽候选减少灰阶或色阶，宽高不变，最终仍是标准 H.264。报告区分预热/首帧/计时帧、平均/P95、实际码率、完整出包和解码、量化误差与编码误差。末帧参考/解码 BMP 供人工检查；像素指标不等于可读性。复测差异很小时不宣称排名稳定。原始截图、码流和输出图仅保存在本机结果目录。
