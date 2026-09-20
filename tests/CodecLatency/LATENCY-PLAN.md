# 延迟基准与优化计划（2026-09-19，建议预算，非冻结契约）

## 当前执行边界：停止测试，先收敛方案

用户已明确停止无限调优、灰度及其他候选测试。本阶段保持本机原始 2880×1800、正常彩色，不启动新基准，不新增实现；下文候选及 720p 计划均为历史记录，不能作为继续执行依据。

- 7 ms 是用户提出的捕获＋完整编码输出的方案分流门槛，不是已知硬件下限。QSV 优先参考成熟产品实现及可比公开数据，不能以我们当前 24.39 ms 的实验结果代替硬件能力，也不靠反复参数扫描证明硬件极限。公开数据必须核对硬件代际、尺寸、格式、码率和计时起止，吞吐 FPS 不能直接当作单帧延迟。
- 即使成熟路径仍不能低于 7 ms，编码同时发送仍是值得探索的独立路线；不能因其不缩短整帧编码计算时间，就否定它对最终显示延迟的价值。应同时保留“完整编码完成”与“最终可显示”的指标，避免串行等待。
- 现成机制：x264 的 nalu_process 回调；Intel VPL 的 mfxExtPartialBitstreamParam（具体设备须 Query 确认支持）。当前 FFmpeg 常规 x264/QSV 封装未提供同帧提前输出通路，多 slice 参数不等于提前取包。方案优先复用这些成熟机制，不重写编码算法。
- 先论证首片输出时刻、传输序列化、可重叠解码时间、片段划分造成的压缩损失及实现成本，形成明确收益假设后再决定是否验证。当前不得恢复测试；先前建议的“2 ms 或 20%”只是工程建议，尚未成为用户冻结指标。

接口依据：https://github.com/mirror/x264/blob/master/x264.h 、https://intel.github.io/libvpl/latest/API_ref/VPL_structs_encode.html#mfxextpartialbitstreamparam 。

## 最新锁定目标：本机完整原始分辨率

按用户最新要求，探索本机完整原始分辨率的捕获＋编码延迟极限。此节优先于下方历史 720p 预算。分辨率从 DXGI 原始桌面纹理读取，不采用 DPI 逻辑尺寸，不改变显示器设置、不缩放、不裁剪。计时从捕获调用开始，到该帧完整编码包输出；同时记录新桌面帧的可用时间与采样等待，禁止把提交时间当作完成时间。

彩色、灰度 8/6/4/2/1 位、1 位有序抖动、RGB565/RGB332 均作为候选；低有效位宽仍送入成熟编码器支持的输入格式，不自定义视频编码。分别统计捕获、转换及量化、编码出包、合计平均/P95、实际码率及画质。黑白和量化可以牺牲颜色，但不能减少像素宽高。CPU 路径已使用库内 SIMD，QSV 路径作为独立对照。先用同批真实像素无头隔离编码成本，再将候选接回原分辨率真实捕获验证完整发送端时间；无头结果不可冒充捕获＋编码结果。

初筛 5 Mbps，候选补测 1 Mbps；原有 30 FPS 上限保持。每组记录预热和首帧成本、完整帧数、PTS、异常、灰度等级及图像误差，并保存人工画质样本。当前 5 ms/3 ms 仅为历史参考，不预先声称硬件可达或以改变分辨率达成。正式生产后端保持现状，测试模式独立。

## 当前决策：先解决捕获＋编码（优先于下文历史全链计划）

本轮只优化发送端捕获＋编码，不扩展到接收端呈现。保留 FFmpeg 和现有 CPU 路径；实验性 GPU 路径未通过前不替换生产后端。下面的数字是工程验收目标，不是硬件极限、厂商承诺或已达成结果，也不替代稀疏小更新原契约。

- 固定本机 Arc 140T、真实桌面、1280×720 输出、30 FPS、5 Mbps，使用相同运动内容、桌面分辨率和画质设置。计时从捕获调用开始，到对应帧完整编码包输出并释放捕获帧；等待下一次桌面刷新不计入，但必须单列。禁止把命令提交耗时当作 GPU 完成耗时。
- 第一目标：捕获＋编码平均 ≤5 ms、P95 ≤8 ms。达到后再探索平均 ≤3 ms；后者当前无实测可行性保证。分层定位预算为捕获及转换完成 ≤1.5 ms、其余直到编码包输出 ≤3.5 ms；异步重叠时不机械相加或重复计费。
- 当前事实：CPU 复用缓冲约 6.10 ms；GPU QSV 显式等待约 9.97 ms、取消额外等待约 9.63 ms。后两者不能证明优于 CPU，也不能由这些结果断言 QSV 硬件极限。取消等待主要改变了耗时归属。原始数据位于 results/sender-quick-1、gpu-sender-loader-diagnostic、gpu-sender-no-fence。

执行顺序与停止条件：

1. 先隔离编码路径：在 GPU 预备好输入纹理，分别测固定内容与预生成运动序列的提交到对应编码包输出时间，不掺入桌面捕获、颜色转换或 CPU 像素上传。固定内容仅定位固定开销，不能代表运动性能。如果此处仍约 8 ms，停止修改捕获代码，定位 QSV/FFmpeg 同步与会话实现。
2. 对照成熟实现：按 RustDesk hwcodec 的编码器输入帧所有权与纹理映射方式建立最小对照，保持硬件、尺寸、码率、输入内容和计时口径一致。当前 derived pool 的反向映射已返回不支持；不能把它当作成熟路线已验证。现有 QSV loader 绕过仅供诊断，发布前须用受支持的初始化方式或修复依赖取代。
3. 仅在编码路径达标后接入真实捕获与 GPU 转换。每组预热 10 帧、计时 90 帧，先一次短测筛选；有收益才做 A/B/A 复测。检验解码帧数、PTS、实际码率与图像内容，变化哈希只排除重复帧，不替代颜色、裁剪、完整性和画质检查。

如果成熟实现同机对照也未达到 5 ms，则报告该设备及当前依赖的实测未达标，保留更快的 CPU 路径；不重复泛扫参数、不降低画质或改变分辨率冒充达标、不外推 5070 Ti。需要更换依赖或硬件时给出具体阻塞证据。

## 当前优先对标：Parsec

用户要求先以 Parsec 为目标。本机 Arc 140T 最近一次真实桌面 Demo，在 1280×720、30 FPS、5 Mbps 下，1746 个变化帧的捕获开始→GPU 呈现确认平均为 9.65 ms：捕获 3.55、编码 2.36、UDP 0.38、解码 1.27、呈现 2.09 ms。来源 results/desktop-probe-local-view-20260919-213747/demo-report.json。这是一次人工桌面活动会话，不是固定内容的重复基准，更不是物理屏幕间延迟。

本机先以相同口径平均 ≤8 ms 为第一门槛，再探索 ≤5 ms；同时记录 P95、实际变化帧数量和未呈现帧，不能只报告最快一帧。优先处理捕获回读和接收端呈现路径，不再扩大编码器参数扫描。达成该软件口径不等于复现 Parsec 的屏幕间结果，后者需匹配刷新率和内容并作外部测量。

参考分工：Parsec 提供低延迟架构与公开极限演示；Steam Remote Play 提供产品体验与分层诊断对照；Sunshine/Moonlight 提供可检查的发送端、接收端实现；RustDesk hwcodec 提供保留 FFmpeg 的纹理接口参考。Steam 公布了硬件编解码开关、详细性能叠层和 streaming_log.txt 分层日志，但未找到可直接套用于本机的官方极限毫秒数，不杜撰。

补充来源：
- Parsec 显存处理与显示同步：https://parsec.app/blog/description-of-parsec-technology-b2738dcc3842
- Steam Remote Play 官方说明：https://help.steampowered.com/en/faqs/view/0689-74B8-92AC-10F2
- Moonlight 接收端：https://github.com/moonlight-stream/moonlight-qt

## 硬件事实与引用边界

- ALIPC：Pentium G5400、Intel UHD 610、GeForce MX150。MX150 在 NVIDIA 官方矩阵中 NVENC 数量为 0，不能承担 NVENC 编码；不是漏测独立显卡。
- 已试 NVENC，本机驱动 457.20 与现有 FFmpeg 组合先报 Cannot load cuMemAllocAsync。运行失败与官方“不具备 NVENC”是两个独立事实，不能用前者冒充后者的验证。
- NVIDIA SDK 13 性能表：1080p、8-bit、4:2:0、P1/CBR/LL、每 NVENC 引擎，Blackwell H.264 977 FPS、HEVC 1134 FPS、AV1 1076 FPS。倒数约 1.02/0.88/0.93 ms 是吞吐等效帧间隔，不是提交到输出的单帧延迟，也不是特定 5070 Ti 的保证。
- 未找到可直接套用到 UHD 610、当前驱动与输入路径的官方单帧延迟下限。不给它编造“理论 1 ms”。

## 已完成测试

通过 TCS 上传 ZIP、校验 SHA256、解压到 ALIPC 用户目录；使用私有 .NET 10 运行时，无系统安装。无头测试复用生产 FfmpegBackend，运动图案预生成；720p/1080p、30 FPS、5 Mbps，10 帧预热、60 帧计时。测试 PTS、完整解码和藏帧，不含屏幕捕获、UDP、显示，也未进行主观等画质比较。

第一轮编码平均（含 CPU 转换、上传、同步和码流复制）：

| 路径 | 720p | 1080p |
|---|---:|---:|
| x264 ultrafast，4 线程 | 5.76 ms | 11.84 ms |
| H.264 QSV | 8.97 ms | 17.33 ms |
| HEVC QSV | 7.89 ms | 15.91 ms |

复测出现整体基准变慢，因此不把跨轮次最小值或差值当成确定收益。QSV low_power、CAVLC、解码线程测试只作候选依据。所有成功编码案例解出全部 60 个计时帧，无滞后 PTS。

独立 CPU 颜色转换微基准：BGRA→NV12，720p 4.33 ms、1080p 9.73 ms；两步转换约 3.42/7.70 ms，但有最大 1 级像素差异。此独立测试不能与另一轮整帧时间相减当作纯 GPU 耗时。它说明必须优先消除 CPU 转换/回读，而非继续枚举编码参数。

原始结果：results/alipc-codec-results.json、alipc-tuning-results.json、alipc-abba-results.json、alipc-conversion-response.json。正式程序未切换预设或发布新版本。

## 明确的分层预算

以下是工程验收方向，不是厂商实测或硬件极限保证。统一先用 1280×720、SDR 8-bit、30 FPS、5 Mbps；localhost 或空闲有线千兆，允许帧突发，不设置额外带宽模拟。每个阶段计算自身持续时间，队列等待另外列出。

| 层 | ALIPC GPU 路径下一阶段平均预算 | 5070 Ti GPU 路径探索平均预算 |
|---|---:|---:|
| 已有新桌面图像→捕获纹理可用 | ≤1.5 ms | ≤0.5 ms |
| GPU 缩放及 BGRA→NV12 | ≤1.0 ms | ≤0.3 ms |
| 纹理提交→该帧完整编码输出 | ≤4.0 ms | ≤2.0 ms |
| 编码输出→完整 UDP 帧到达 | ≤0.5 ms | ≤0.5 ms |
| 完整码流→解码纹理可用 | ≤2.0 ms | ≤1.0 ms |
| 解码纹理→GPU 绘制完成 | ≤1.0 ms | ≤0.5 ms |
| 上述处理链合计 | ≤10 ms | ≤4.8 ms |

全链 P95 暂以 ALIPC ≤16 ms、5070 Ti ≤8 ms 为探索门槛，必须实际测量，不能由各层 P95 相加证明。应用排队另设平均 ≤0.5 ms 的诊断目标，超出则解释或修复。ALIPC 的 ≤10 ms 需要改为 GPU 纹理路径才有依据探索，当前 CPU 输入实现不算达成。

若 5070 Ti 达到该预算，再探索 3 ms 内处理链；暂不声称全区域视频流平均 <1 ms。稀疏小更新原契约另测，不能由此预算替换。

这些数字不包括等待下一次可捕获刷新和最终物理显示扫描。连续动态在固定 30 FPS、随机相位下采样等待平均约 16.7 ms；即使后续处理 5 ms，用户看到的变化延迟仍不能宣称 5 ms。改变刷新率只能作为单独实验，不能暗改产品 30 FPS 上限。

公网单独记录基础传播、发送/网络排队、序列化和抖动，不套用 0.5 ms 的 LAN 预算。例如 6 KB 在 1 Mbps 物理瓶颈上的序列化约 48 ms，禁用应用限速不能消除它。输入发送不等待视频帧或输入 ACK；输入往返体验还应加入对端应用响应及回传图像等待。

## 与竞品对应的实施路线

1. OBS：参考 GPU 纹理输入、GPU 缩放，避免 CPU 缩放触发非纹理路径；不用直播播放缓冲来定义远程桌面目标。
2. RustDesk：其 hwcodec 仍基于 FFmpeg，Windows 有 QSV/NVENC/AMF 显存路径和 D3D11 解码路径。继续复用 FFmpeg，调整输入输出表面即可，不自造编码器。
3. Sunshine：低延迟预设、单帧 VBV 约束帧突发、保留 GPU 性能余量；码率更稳与单次编码最快是可量化权衡。两 NVENC 的 Split Frame 仅在支持的 HEVC/AV1 路径评估，不假定 H.264 自动减半。
4. Parsec：公开说明无变化可不发帧、拥塞时降低最大码率；其面板“编码延迟”包含捕获，不能与本测试纯编码入口口径直接比较。历史 4–8 ms 演示使用 240 Hz、千兆 LAN 和 100 Mbps 上限，不是 30 FPS/1 Mbps 的保证。
5. RDP：变化检测、缓存、渐进清晰和自适应画质值得参考；不把内容分类引入本项目已约定的范围。

下一步只验证一个架构假设：同一适配器捕获纹理→GPU 色彩转换→FFmpeg 硬件表面编码；接收端解码纹理直接绘制。UDP 压缩码流仍完整经过网络，发送端与接收端不共享原始纹理。先以 QSV 验证架构，再在真正支持 NVENC 的机器验证 NVIDIA 路径。MX150 的图形计算虽可用于转换，但跨 GPU 复制到 UHD 610 可能增加同步成本，不把两块 GPU 强行串联。

## 一手来源

- NVIDIA 支持矩阵：https://developer.nvidia.com/video-encode-decode-support-matrix
- NVIDIA 性能表：https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-application-note/index.html
- OBS 编码源码：https://github.com/obsproject/obs-studio/blob/master/plugins/obs-nvenc/nvenc.c
- RustDesk 编解码库：https://github.com/rustdesk-org/hwcodec
- Sunshine 参数及限制：https://docs.lizardbyte.dev/projects/sunshine/latest/md_docs_2configuration.html
- Parsec 统计定义：https://support.parsec.app/hc/en-us/articles/32381603663636-Stream-Overlay-Stats-and-Logging
- Parsec 极限演示：https://parsec.app/blog/parsec-game-streaming-total-latency-at-240-frames-per-second-c0818cc0daa5
- RDP 图形优化：https://learn.microsoft.com/en-us/azure/virtual-desktop/rdp-bandwidth
