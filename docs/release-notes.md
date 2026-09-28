# FRD v2.pre5

Windows x64，依赖 .NET 10 x64 Runtime，不自包含，不启用 Native AOT。

- 修复 GitHub Actions 发布步骤对非 ASCII 发布说明文件名的路径解析，改用此稳定路径。
- 保留同一二进制安全桌面辅助服务：被控端可经 UAC 安装、修复并启动 `FRDSecureCaptureProbe`，用于锁屏和 UAC 画面的捕获与输入。

已编译主工程和生成的单文件 Demo。安装辅助服务会改变 Windows 服务配置，首次点击仍由被控端本机用户确认。
