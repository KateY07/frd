# 同一会话的网络路径切换回归

从仓库根目录运行：

```powershell
dotnet run --project tests/PathSwitchResilience/PathSwitchResilience.csproj -- --same-session-only
dotnet run --project tests/PathSwitchResilience/PathSwitchResilience.csproj -- --same-session-window
```

测试转发器始终向 FRD 暴露同一个本机 TCP 端点和 UDP 端口。暂停时不发送 FIN/RST、不重新握手，也不更改会话身份；TCP 字节暂存，UDP 包丢弃。第一项在同一活动会话内依次暂停 100 ms、500 ms、1 s、3 s、5 s、10 s，确认状态及安全的 ReleaseAll 输入命令恢复且 TCP 连接数不变；再分别验证视频 UDP 丢包/乱序、仅视频 UDP 暂停、仅诊断 UDP 暂停、仅反馈 UDP 暂停、仅键鼠 TCP 暂停，以及明确关闭控制 TCP 的对照组。静止桌面允许不产生新视频帧，因此该项不以每次暂停后都出现新帧作为通过条件。第二项启动真实 Avalonia 主控窗口，确认 10 s 暂停后窗口存活并继续 GPU 呈现；再验证仅键鼠 TCP 关闭不终止视频，控制 TCP 明确关闭后显示手动建立新会话的入口。

这模拟的是固定本机转发端点下的路径中断，不是更换对端地址。测试不证明任意时长断网可恢复，也不代替真实转发器现场测试。窗口测试报告在 `artifacts/path-switch/`；进程生命周期日志写入 `%LOCALAPPDATA%\FRD\session-<PID>.log`，只记录事件、异常类型、HRESULT 和堆栈，不记录口令、会话凭据或剪贴板内容。
