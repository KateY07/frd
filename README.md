# FRD · 远程桌面 CLI（v1.pre9）

FRD 是供二次开发集成的 Windows x64 远程桌面基础组件，当前对外接口为 **CLI（进程级 API）**。同一个 `FRD.exe` 通过命令行启动被控端或主控端；编码预设由 JSON 定义，Avalonia 提供连接状态、控制和画面预览。开发者可以从 C#、Python 或其他语言启动并管理该进程。

真实链路为：屏幕捕获 → 编码 → UDP → 解码 → GPU 呈现。本版提供 FFmpeg `libx264rgb` 等预设；视频只按原始分辨率传输，窗口可等比缩放。保留手动码率与预设切换、键鼠转发、鼠标形状、双向文本/文件/目录剪贴板，以及鼠标穿透的半透明诊断水印。

## 当前锁定的实验目标

探索本机完整原始屏幕分辨率下的捕获、编码延迟极限：从真实捕获开始，到对应完整编码包输出，记录平均值、P95 及分层耗时。保留全部像素宽高；彩色、黑白和更低有效位宽均为可测试候选。不用缩放、跳过慢帧或 GPU 命令提交时间替代完整处理时间。每条候选路径须验证解码正确性并提供画质样本。该独立延迟实验不把网络、解码或呈现计入捕获加编码结果；这些环节另行验证。历史 720p 数据不算本目标达成证据，固定时延预算只作参考，实测决定可达下限。

本轮实验已在 Intel Arc 140T、2880×1800、正常彩色、全画面持续变化和约 30 FPS 条件下结束；最低已测“捕获调用开始→完整编码包”平均值为 CPU LZ4 无损 3.40 ms（19.70 Mbps）。此值不含新画面等待、UDP、解码、渲染和显示扫描，且不是硬件物理下限。当前源码已将 `libx264rgb` 接入实际传输链路，LZ4 实验实现已从运行版本移除，但上述数字来自独立实验，不能直接当作当前 Demo 或发布版性能。完整边界和原始证据见 [目标与验收](docs/目标与验收.md) 与 [延迟结果](tests/CaptureEncodeBench/LATENCY-RESULTS.md)。

## 五分钟入门

1. 在两台 Windows x64 机器安装 .NET 10 x64 Runtime，将完整便携包解压并保留 `FRD.exe`、`codec-config.json` 和 `ffmpeg/`。本版新增预设已随便携包提供。
2. 在被控机器的普通已登录桌面启动服务端，并明确指定监听地址、端口和本次口令；地址替换为被控端实际网卡 IPv4 或 IPv6 地址：

```powershell
.\FRD.exe --host --listen 192.168.1.20 --port 5000 --token "请替换为双方约定的口令"
```

3. 在主控机器连接，地址替换为被控机器的 IPv4/IPv6 地址或主机名：

```powershell
.\FRD.exe --connect 192.168.1.20 --port 5000 --token "与被控端本次启动相同的口令"
```

4. 启动时限时探测可用的低延迟有损编解码预设；主控窗口仍可手动切换预设、调整码率上限。键鼠和剪贴板同步需手动开启。Ctrl+Alt 退出键鼠转发，Ctrl+Alt+F11 切换全屏。

**被控端每次启动必须显式传入 `--token`。** 缺失、空字符串或纯空白口令时，在加载配置和编解码库前向 stderr 输出原因并以退出码 1 退出，不开启窗口或监听；无默认口令，不自动生成，不保存上次口令，也不从 JSON 读取口令。只有匹配口令的客户端能建立会话；键鼠、鼠标状态和剪贴板附属连接还需匹配当前会话。口令区分大小写。

## CLI 接口

| 调用 | 行为 |
| --- | --- |
| `FRD.exe --host --listen ADDRESS --port PORT --token TOKEN` | 被控端：捕获、编码并监听；三个参数每次启动均必填 |
| `FRD.exe --connect HOST --port PORT --token TOKEN` | 主控端：连接、解码、显示与控制 |
| `FRD.exe --help` | 输出文本帮助并退出，不开启窗口或加载编解码库 |
| `FRD.exe --version` | 输出版本并退出 |
| `FRD.exe --help-window` | 打开可视帮助窗口 |
| `FRD.exe` | 本机 localhost 真实桌面演示 |

- 从 v1.pre8 起已移除发布版的 Demo 输入限制。开启转发后，鼠标移动、点击、拖动、滚轮与键盘均使用正式输入通道；`--connect` 不再因地址是 localhost、回环或本机网卡而改接测试窗口。无参数演示恢复真实系统输入，并保留 FRD 窗口防回注；本机共用鼠标与焦点，双机体验需独立桌面。Ctrl+Alt 退出并释放按键。

- `--port` 为必填 TCP 监听端口，范围 1–65535；视频、诊断、反馈和鼠标移动使用协商的 UDP 端口。鼠标移动只保留最新坐标，丢包或乱序旧坐标不阻塞后续移动；点击、滚轮和键盘继续走独立 TCP 输入连接，确保释放事件可靠到达。两端须允许相应网络通信。
- 被控端 `--listen` 必填，没有默认地址。本机测试使用 `localhost`（同时监听 `127.0.0.1` 和 `::1`，仅限本机）；指定 `::` 使用单个 IPv6 双栈 Socket，接受所有网卡的 IPv4/IPv6 连接。`0.0.0.0` 仅监听所有 IPv4 接口；具体 IP 仅绑定该地址，`::1` 仅限 IPv6 回环。IPv6 地址与 `--port` 分开传入，无需方括号；链路本地地址可带 `%接口索引`。
- 运行中的主控/被控进程持续存活。正常关闭窗口后退出码为 0；参数错误、口令拒绝导致启动失败、配置/启动失败返回 1。主控连接明确关闭时窗口保留原因与手动新建会话入口；用户关闭后以错误退出码结束。错误口令/无效请求仅拒绝对应连接，预期 UDP 丢包不终止服务。
- 使用控制台 EXE 入口：在终端直接调用时持续阻塞，直到窗口关闭、资源清理完成并返回退出码；不会启动后台子进程后提前返回。二次开发调用方使用 `WaitForExitAsync()` 等待退出；显式使用 `Start-Process` 等异步启动方式时，等待行为由调用方决定。
- 被控监听成功的日志包含 `Remote listener ready:`。集成端应持续读取重定向的 stdout/stderr，避免管道填满阻塞子进程。日志用于诊断，不作为稳定 JSON 协议。
- `--connect` 可附加 `--test-seconds N --report PATH`（1–3600 秒）进行限时演示并输出 JSON 报告；它是开发验证入口，正常会话不必设置。
- 正常退出优先关闭进程主窗口并等待结束，保证捕获、网络及输入状态得到清理。源码中通过 SYSTEM 辅助进程显示锁屏与 UAC 安全桌面，并在被控端授权键鼠后向该进程转发输入；双机 Demo 的 UAC、锁屏画面及键鼠操作已人工确认。辅助进程尚未纳入便携包，不能将本次开发测试当作 v1.pre9 的无人值守能力。被控端连接时在右下角显示受控提示，可断开连接或撤销剪贴板、键鼠权限；窗口从普通桌面捕获中排除。声音尚未实现。
- 安全桌面辅助进程目前仅供受信任测试机验证：本地输入密钥可由启动用户读取，测试服务安装在用户目录，尚不满足正式发布所需的特权输入授权及服务文件保护。

### C# 调用示例

```csharp
using System.Diagnostics;

var executable = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Programs", "FRD", "v1.pre9", "FRD.exe");
var info = new ProcessStartInfo(executable)
{
    WorkingDirectory = Path.GetDirectoryName(executable),
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    CreateNoWindow = true
};
// 由上层程序获取本次口令，不把真实口令写进源码或日志。
var token = GetTokenForThisLaunch();
foreach (var arg in new[] { "--host", "--listen", "127.0.0.1", "--port", "5000", "--token", token })
    info.ArgumentList.Add(arg);
using var process = Process.Start(info) ?? throw new IOException("FRD 启动失败");
process.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
process.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.Error.WriteLine(e.Data); };
process.BeginOutputReadLine();
process.BeginErrorReadLine();
await process.WaitForExitAsync();
Console.WriteLine($"FRD 退出码：{process.ExitCode}");
```

`GetTokenForThisLaunch()` 由集成应用实现。启动主控端时将参数换为 `--connect HOST --port PORT --token TOKEN`；关闭时可调用 `CloseMainWindow()` 并等待退出。口令必须作为独立参数传入；不要用字符串拼接构造 Shell 命令，也不要记录参数数组中的口令。

## 配置与交付

- 修改 EXE 同目录的 `codec-config.json`。`presets` 的 `encoderArguments` / `decoderArguments` 是单行参数字符串；FFmpeg 预设支持现有构建和设备提供的 H.264、VP9、AV1、NVENC、QSV 等编码器。不支持的预设明确失败。
- `initialBitrateKbps` 设置有损编码初始码率，`maximumBitrateMbps` 设置滑条上限（默认 100 Mbps，可配置至 1000 Mbps）。码率通过滑条调整，当前数值在标签中以 Mbps 显示。不会按滑条数值额外限制链路，允许编码缓冲和帧发送突发。
- `autoSelectCodec:true` 在启动时对标记 `autoProbe:true` 的有损预设做真实屏幕短测，探测进程最多运行 5 秒；不可用或超时则保留 `initialPreset`。`minimumAutoBitrateKbps` 可让高带宽预设仅在码率足够时参与。双机时被控端测捕获和编码，主控端用样本验证并测解码；结果只是当前画面、当前码率下的启动选择，手动切换始终可用。
- 当前会话的码率与预设通过主控 UI 调整并传到被控端；JSON 在启动时读取，尚未提供运行中 JSON 热加载或独立外部 RPC 接口。
- 本版依赖 **.NET 10 x64 Runtime**，不再自包含运行时，也不启用 Native AOT；仍直接运行 `FRD.exe`。需在主控与被控机器预先安装相应运行时，SDK 非必需；[微软下载页](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)。Avalonia 与 FFmpeg 原生依赖仍随包提供。原生调试符号另存于本机构建产物，不进入便携包。
- 推荐无需管理员权限的目录：`%LOCALAPPDATA%\Programs\FRD\v1.pre9\`，不同版本分目录保留。
- 发布脚本的输出目录为 `artifacts/v1.pre9/`，包含便携 ZIP、README 和 SHA-256。Git 只推送版本标签及其引用的源码提交，不更新远端默认分支；安装包保留本机，不上传 GitHub Releases。

## 开发与边界

安装 .NET 10 SDK 和 7-Zip 后，在仓库执行 `Get-FFmpeg.ps1` 获取固定版本依赖，再执行 `Publish.ps1` 生成全新便携包；`Build-SingleFileDemo.ps1` 同步生成 `demo/FrdDemo.cs`，该文件不单独维护。工程化源码适度拆分，保留单文件示例供参考。

当前支持 IPv4/IPv6 双栈，为单主控、可信局域网原型，无 NAT 穿透；口令认证不等于传输加密。静止画面不保证零带宽或无损；跨机延迟包含时钟估算误差，GPU 完成不等于物理屏幕扫描完成。键鼠已人工确认，自动全局键鼠注入回归保持暂停；本机可运行模拟接收器及专用窗口定向消息测试，不抢系统鼠标。

v1.pre7 的输入发送改为有界流水：保持事件顺序，后台确认，不再每个事件等待一次 RTT 才发下一个；补充延迟、取消、断线及按键释放回归。已完成成熟方案调查、五组共享瓶颈/全双工短测和两组固有延迟/排队对照；这些网络模型不等同于真实公网或 RDP 对测。**本版仍由滑块手动控制编码码率，未实现自动降码率。** 详见 [输入延迟验证](docs/本机输入延迟验证.md)、[公网拥塞调查](docs/公网拥塞控制调查.md)、[公网回归设计与结果](docs/受限公网回归设计.md)。

后续拥塞控制目标保留，但按用户要求暂缓实现：**有损预设的滑块固定为用户码率上限，内部在上限内自动调节实际编码码率，拥塞时优先延迟、允许降低清晰度，恢复后逐步升码率。** 不自动改变预设。诊断区分链路低负载基线、估计的新增网络排队和本地处理/排队；高 RTT 本身不等于带宽不足。此项不属于本版已实现功能，详见 [目标与验收](docs/目标与验收.md)。

## v1.pre9 更新

- 新增全分辨率低位宽彩色/灰度预设、`libx264rgb` 路径与启动时编解码短测；可选 10/20/30/60 FPS，码率和预设继续由用户手动控制。实际画质与设备性能需按场景确认。
- 修复经本机固定端点转发器复现的网络切换故障：同一 TCP 连接暂停超过 5 秒不再因键鼠确认超时被主动关闭；控制、视频、反馈、诊断及输入分别回归。明确关闭控制 TCP 时不伪装成原会话恢复，主控窗口显示原因和手动建立新会话入口。详见 [静态检查与回归](docs/v1.pre9-静态检查.md)。
- `%LOCALAPPDATA%\FRD\session-<PID>.log` 记录异常类型、通道关闭顺序和退出码，不记录口令、会话凭据或剪贴板内容。安全桌面辅助进程仍是单独的受信任测试部署，未包含于便携包。

## v1.pre8 界面

- 主控和本机演示使用紧凑控制栏；主控、被控和帮助窗口统一采用 Avalonia Simple 主题。
- 点击全屏按钮或按 Ctrl+Alt+F11 切换全屏；Ctrl+Alt 退出键鼠转发。启用转发时，单独按 Esc 或 F11 保留远端输入含义。
- 展开时可拖动控制栏的空白区域，按钮、下拉框和滑条保持正常交互。收起后显示为 50 像素半透明悬浮球，同样可拖动；单击恢复控制栏。两种状态均限制在视口内，窗口缩放或切换全屏时保留当前位置，必要时调整至可见范围。
- 诊断水印可独立隐藏，保持鼠标穿透且不获取键盘焦点。水印显隐与诊断包开关独立，隐藏水印不会关闭诊断采集或网络诊断包。

以上控制栏界面继承自已获人工确认的 v1.pre4；本版移除 pre7 的本机专用测试目标和鼠标事件过滤，保留输入流水发送及失败清理。测试目标仅保存在独立测试工程，不进入正式程序或生成的单文件 Demo。未重新执行真实双机或 RDP 对照，自动全局键鼠注入回归继续保持暂停。

进一步阅读：[使用与配置](docs/使用.md)、[目标与验收](docs/目标与验收.md)、[发布说明](docs/v1.pre9-发布说明.md)、[静态检查](docs/v1.pre9-静态检查.md)、[第三方许可证](THIRD-PARTY-NOTICES.md)。
