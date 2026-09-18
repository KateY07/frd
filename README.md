# FRD v1.pre4 · 远程桌面 CLI

FRD 是供二次开发集成的 Windows x64 远程桌面基础组件，当前对外接口为 **CLI（进程级 API）**。同一个 `FRD.exe` 通过命令行启动被控端或主控端；编码预设由 JSON 定义，Avalonia 提供连接状态、控制和画面预览。开发者可以从 C#、Python 或其他语言启动并管理该进程。

真实链路为：屏幕捕获 → FFmpeg 编码 → UDP → FFmpeg 解码 → GPU 呈现。支持原始分辨率及 0.75×/0.5× 传输、手动码率与预设切换、键鼠转发、鼠标形状、双向文本/文件/目录剪贴板，以及鼠标穿透的半透明诊断水印。

## 五分钟入门

1. 将完整便携包解压到两台 Windows x64 机器，保留 `FRD.exe`、`codec-config.json` 和 `ffmpeg/`。无需另装 .NET。
2. 在被控机器的普通已登录桌面启动服务端，并明确指定监听地址、端口和本次口令；地址替换为被控端实际网卡 IPv4 或 IPv6 地址：

```powershell
.\FRD.exe --host --listen 192.168.1.20 --port 5000 --token "请替换为双方约定的口令"
```

3. 在主控机器连接，地址替换为被控机器的 IPv4/IPv6 地址或主机名：

```powershell
.\FRD.exe --connect 192.168.1.20 --port 5000 --token "与被控端本次启动相同的口令"
```

4. 在主控窗口选择编码预设、码率上限、传输比例；键鼠和剪贴板同步需手动开启。Ctrl+Alt 退出键鼠转发，Ctrl+Alt+F11 切换全屏。启用转发时，单独按 Esc 或 F11 仍传给远端。

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

- `--port` 为必填 TCP 监听端口，范围 1–65535；视频、诊断和反馈使用协商的 UDP 端口。两端须允许相应网络通信。
- 被控端 `--listen` 必填，没有默认地址。本机测试使用 `localhost`（同时监听 `127.0.0.1` 和 `::1`，仅限本机）；指定 `::` 使用单个 IPv6 双栈 Socket，接受所有网卡的 IPv4/IPv6 连接。`0.0.0.0` 仅监听所有 IPv4 接口；具体 IP 仅绑定该地址，`::1` 仅限 IPv6 回环。IPv6 地址与 `--port` 分开传入，无需方括号；链路本地地址可带 `%接口索引`。
- 运行中的主控/被控进程持续存活。正常关闭窗口后退出码为 0；参数错误、口令拒绝导致启动失败、配置/启动失败返回 1。不可恢复的运行期错误同样写入 stderr，清理资源并以退出码 1 退出；错误口令/无效请求仅拒绝对应连接，预期 UDP 丢包不终止服务。
- 使用控制台 EXE 入口：在终端直接调用时持续阻塞，直到窗口关闭、资源清理完成并返回退出码；不会启动后台子进程后提前返回。二次开发调用方使用 `WaitForExitAsync()` 等待退出；显式使用 `Start-Process` 等异步启动方式时，等待行为由调用方决定。
- 被控监听成功的日志包含 `Remote listener ready:`。集成端应持续读取重定向的 stdout/stderr，避免管道填满阻塞子进程。日志用于诊断，不作为稳定 JSON 协议。
- `--connect` 可附加 `--test-seconds N --report PATH`（1–3600 秒）进行限时演示并输出 JSON 报告；它是开发验证入口，正常会话不必设置。
- 正常退出优先关闭进程主窗口并等待结束，保证捕获、网络及输入状态得到清理。CLI 当前承载在交互式 Windows 桌面中，适合由上层应用管理；被控端不能作为锁屏/安全桌面的无人值守捕获服务。

### C# 调用示例

```csharp
using System.Diagnostics;

var executable = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Programs", "FRD", "v1.pre4", "FRD.exe");
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

- 修改 EXE 同目录的 `codec-config.json`。`presets` 的 `encoderArguments` / `decoderArguments` 是单行 FFmpeg 参数字符串；支持现有构建和设备提供的 H.264、VP9、AV1、NVENC、QSV 等预设。不支持的预设明确失败。
- `initialBitrateKbps` 设置初始码率，`maximumBitrateMbps` 设置滑条上限（默认 100 Mbps，可配置至 1000 Mbps）。码率通过滑条调整，当前数值在标签中以 Mbps 显示；此值限制编码码率，不进行额外链路节流，允许编码缓冲和帧发送突发。
- 当前会话的码率与预设通过主控 UI 调整并传到被控端；JSON 在启动时读取，尚未提供运行中 JSON 热加载或独立外部 RPC 接口。
- 推荐无需管理员权限的目录：`%LOCALAPPDATA%\Programs\FRD\v1.pre4\`，不同版本分目录保留。
- 发布脚本的 pre4 输出目录为 `artifacts/v1.pre4/`，包含便携 ZIP、README 和 SHA-256。源码及版本标签推送至 Git；安装包保留本机，不上传 GitHub Releases。

## 开发与边界

安装 .NET 10 SDK 和 7-Zip 后，在仓库执行 `Get-FFmpeg.ps1` 获取固定版本依赖，再执行 `Publish.ps1` 生成全新便携包；`Build-SingleFileDemo.ps1` 同步生成 `demo/FrdDemo.cs`，该文件不单独维护。工程化源码适度拆分，保留单文件示例供参考。

当前支持 IPv4/IPv6 双栈，为单主控、可信局域网原型，无 NAT 穿透；口令认证不等于传输加密。静止画面不保证零带宽或无损；跨机延迟包含时钟估算误差，GPU 完成不等于物理屏幕扫描完成。键鼠已人工确认，自动键鼠回归保持暂停。

## v1.pre4 界面

- 主控和本机演示使用紧凑控制栏；主控、被控和帮助窗口统一采用 Avalonia Simple 主题。
- 点击全屏按钮或按 Ctrl+Alt+F11 切换全屏；Ctrl+Alt 退出键鼠转发。启用转发时，单独按 Esc 或 F11 保留远端输入含义。
- 展开时可拖动控制栏的空白区域，按钮、下拉框和滑条保持正常交互。收起后显示为 50 像素半透明悬浮球，同样可拖动；单击恢复控制栏。两种状态均限制在视口内，窗口缩放或切换全屏时保留当前位置，必要时调整至可见范围。
- 诊断水印可独立隐藏，保持鼠标穿透且不获取键盘焦点。水印显隐与诊断包开关独立，隐藏水印不会关闭诊断采集或网络诊断包。

以上行为已接入真实桌面 Demo，本机短测通过，UI 已获人工确认。本版先发布再进行完整回归；若发现问题，修复后递增 pre 版本重新发布，保留旧标签。验证状态见本版检查记录。自动键鼠回归继续保持暂停。

进一步阅读：[使用与配置](docs/使用.md)、[目标与验收](docs/目标与验收.md)、[发布说明](docs/v1.pre4-发布说明.md)、[静态检查](docs/v1.pre4-静态检查.md)、[第三方许可证](THIRD-PARTY-NOTICES.md)。
