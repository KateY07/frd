# FRD v3.pre3

Windows x64，Native AOT 单文件 `FRD.exe`，无需安装 .NET 运行时；与 v3.pre2 功能、协议、端口完全一致，可互连。

- 新增项目主页（GitHub Pages，`/docs` 为根）：https://katey07.github.io/frd/ 。纯英文、深色毛玻璃风格，含特性、原理、下载、入门和更新记录；页面会通过 GitHub API 显示最新发布版本和下载链接。
- README“五分钟入门”改为无需安装 .NET 运行时（v3 起为 Native AOT），并说明 FFmpeg 为可选扩展包。
- 发布流程：本机 `Publish.ps1` 打包后用 `gh release create` 上传，不再使用 GitHub Actions。
