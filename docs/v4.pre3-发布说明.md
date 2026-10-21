# FRD v4.pre3

Windows x64，Native AOT 单文件 `FRD.exe`，无需安装 .NET 运行时。与 v4.pre1/pre2 可互连，与 v3 不互通。

**新功能：中英文界面**
- 界面、提示、错误信息和命令行帮助都有中文和英文两套，共 190 多条文字。
- 用命令行参数选择：`FRD.exe … --lang en` 或 `--lang zh`（可加在任何调用上，包括 `--host`、`--connect`、`--help`）；也可以设环境变量 `FRD_LANG`。
- 不指定时跟随 Windows 显示语言：中文系统显示中文，其他显示英文。
- 控制端和被控端各自按自己的语言显示；被控端通过网络发来的状态文字按被控端的语言显示。
- 命令行参数 `--lang` 是新增的可选参数，原有参数和退出码不变。

**验证**：布局回归 `--ui-layout-regression` 在中英文下共 80 种尺寸与状态，控件无重叠、无越界；两种语言的本机回环远程会话都通过；英文翻译的占位符与中文逐条核对一致。
**说明**：只翻译了 FRD.exe 的界面文字；README 和发布说明仍是中文，主页是英文。

**单文件版 `frd-v4.pre3.exe`（4.3 MB）**：把 `FRD.exe`、`frd-wintools.exe`、`codec-config.json` 和许可证打成一个 exe（由 [OneSetup](https://github.com/KateY07/onesetup) 的 `--app` 模式打包）。用法与 `FRD.exe` 完全一样，参数、工作目录、控制台和退出码原样传递，例如 `frd-v4.pre3.exe --connect HOST --port 5000 --token ...`。第一次运行解压到 `%LOCALAPPDATA%\OneSetuppps\<内容哈希>`（约 3 秒），之后每次只多约 0.07 秒；环境变量 `ONESETUP_CACHE` 可改位置。没有代码签名，SmartScreen 可能提示。需要 FFmpeg 预设时仍用扩展包，解压到缓存目录里的 `FRD.exe` 旁，或改用 zip 版。校验见 `frd-v4.pre3.exe.sha256`。
