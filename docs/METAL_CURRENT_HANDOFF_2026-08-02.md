# Ryujinx macOS 原生 Metal 当前状态交接

更新时间：2026-08-02（Asia/Shanghai）

本文是面向下一位接手者的**当前停点**，重点是高频阳光闪烁、GPU 取证、内存与正确性风险。
完整历史仍在 [METAL_HANDOFF_2026-07-18.md](METAL_HANDOFF_2026-07-18.md)，但那份文件很长；
开始工作前先读本文和仓库根目录的 [AGENTS.md](../AGENTS.md)。

## 1. 一页结论

- 分支：`codex/native-metal-backend`
- Git HEAD：`0a7fbbdf47a64227289d24d07bd30024e86cf9da`
- HEAD 说明：`metal: retain textures through command buffer completion`
- 可重复基线：`artifacts/terminal/Ryujinx-metal-v68b-clean-lifetime`
- 当前首要问题：Metal 在 TOTK 第一个存档、强阳光场景中以接近逐帧/隔帧的频率在
  **正常场景与近纯白场景之间交替**；HUD 保持正确。
- Vulkan 原版没有这种高频彩色/白色风暴，因此这是 Metal 路径特有问题；上游偶发白闪应另案记录。
- 已排除：简单提交延迟、跨格式纹理别名、argument-buffer 脏标记、颜色写掩码导致的 pass 合并、
  Metal command-buffer 失败、显存持续增长、最终 FSR/present/drawable 单独导致的整帧错误。
- Metal System Trace 表明 GPU 没有报错或长时间卡死；问题更像**最终场景纹理内容、曝光/历史缓冲
  或 tone-map 前后的 ping-pong 内容错误**。
- 第一次原始 GPU 资源取证已解码出 HDR 输入和 tone-map 输出；该随机捕获是一张正常帧，
  还没有抓到确定的白帧。
- `v71` 是尚未运行验证的“只在外部检测到白帧后抓取”的诊断候选。不要把它当成修复版。
- 当前工作树有大量未提交实验，不能直接说“工作区构建 = HEAD 基线”。

## 2. 绝对不要做的事

1. 不要修改、覆盖或启动 `/Applications/Ryujinx.app` 来做自动化测试。它是用户的稳定基线。
2. 不要把新 DLL 塞进已经验证过的 App；资源签名和程序集版本都会被破坏。
3. 不要同时运行两个 Ryujinx 实例。两个实例会并发写共享 shader cache，历史上已经造成缓存损坏。
4. 不要重新启用 `RYUJINX_KEEP_ALIASED_RTS=1` 或全局保留 alias render target。
   这会把有界的在途生命周期变成持续 GPU 内存增长。
5. 不要用降低画质、跳过 shader、跳过同步或屏蔽 draw 来“修复”帧率/闪烁。
6. 不要把“主界面能打开”当作验证通过；Metal DLL 往往到实际启动游戏才加载。
7. 不要在 `TraceDraw` 的绘制编码前读回结果并据此下结论。旧深度探针曾因此得到完全错误的“硬事实”。

## 3. 复现环境

### 3.1 游戏与存档

游戏镜像：

```text
/Users/ssssss/Downloads/switch游戏/v1.4.2整合版带本体和补丁/[The Legend of Zelda Tears of the Kingdom][0100F2C0115B6000][1.4.2][1G+1U][US][20.1.5].xci
```

选择**第一个存档**（Great Sky Island，阳光强的画面）。这是目前闪烁最明显、最容易自动检测的点。
游戏通常显示在外接屏幕；窗口监测器使用 CGWindow API，不依赖主屏位置。

### 3.2 干净基线

```text
artifacts/terminal/Ryujinx-metal-v68b-clean-lifetime/Ryujinx
SHA-256 806fd5705069c65f67ac6d9c5a580745af9f45ace61564e5d149c08f06d3627d
```

该候选来自 HEAD `0a7fbbdf` 的完整 Git archive，不含工作区未提交的 Counter、autorelease、
ToneMapProbe、StrictSync 等实验。它只包含已提交的 command-buffer texture lifetime 修复。

自动化入口：

```text
tools/run_metal_flicker_trial.zsh
```

脚本会：

1. 启动传入的独立候选和上面的 XCI；
2. 等待标题菜单；
3. 用真实 key-down/key-up 选择第一个存档并确认两次；
4. 等待 `DefaultBootEvent`；
5. 在外接屏窗口上采样 15 秒；
6. 只终止它自己启动的精确 PID。

脚本已修正两个旧坑：第二个确认对话框不能漏按；退出时 SIGINT 超时后只对精确 PID 发 SIGTERM，
不会无限 `wait`。

## 4. 高频闪烁的确定性质

### 4.1 v68b 长基线

证据目录：

```text
tools/diagnostics/v68b-sunlight-01/
```

约 99 秒内：

- 1416 次高幅度场景切换；
- 681 次 white；
- 685 次 recovery；
- 白帧中央场景接近纯白，平均 RGB 约 254；
- HUD、菜单栏和状态栏没有一起被染白；
- 约 94% 的中央场景像素在正常/白帧间发生大幅变化。

这不是偶发“一分钟一次”的旧问题，而是稳定的双状态高频交替。旧检测器要求“前一帧安静”，
会在这种风暴里失明；当前 `tools/monitor_metal_window.py` 直接检测 white/recovery 和大范围亮度跳变。

### 4.2 最终呈现不是第一嫌疑

白帧中 HUD 保持正常，说明 drawable、窗口、CoreAnimation 或整帧颜色空间不是唯一根因。
错误发生在 HUD 之前的场景渲染内容里。保存画面的缩略图也曾出现白场景，进一步说明坏内容已进入
游戏自己的场景输出/历史反馈，而不是只在最后一次 present 时出现。

## 5. 已完成的 A/B 与已排除项

| 假设 | 实验 | 结果 |
| --- | --- | --- |
| command buffer 提交太晚 | v68b `RYUJINX_METAL_AUTO_FLUSH=2` | 约 16 秒仍有 228 事件，排除简单提交延迟 |
| 跨格式 alias 串线 | v69 `RYUJINX_DISABLE_FORMAT_ALIASES=1` | 15 秒 204 事件，排除 |
| argument buffer/dirty tracking 陈旧 | v68b `RYUJINX_METAL_FULL_REBIND=1` | 15 秒 205 事件，排除 |
| color-mask 改变却不结束 pass | `RYUJINX_METAL_END_PASS_ON_COLOR_MASK=1` | 15 秒 188 事件，排除 |
| Metal command buffer 执行失败 | Xcode Metal System Trace | command-buffer error 表 0 行 |
| GPU 长 stall 造成交替 | 完成时间与 GPU intervals | 99% GPU interval < 0.26 ms，最长 1.34 ms |
| 显存泄漏直接触发白闪 | `currentAllocatedSize` | 约 1.00 GiB 回落并稳定在约 0.93 GiB |

对应目录：

```text
tools/diagnostics/v68b-sunlight-forceflush-actual/
tools/diagnostics/v69-sunlight-no-format-alias/
tools/diagnostics/v68b-sunlight-full-rebind/
tools/diagnostics/v68b-sunlight-endpass-xctrace-4/
```

## 6. Metal System Trace 的硬数据

有效 trace：

```text
tools/diagnostics/v68b-sunlight-endpass-xctrace-4/metal-system.trace
```

导出结果：

```text
tools/diagnostics/v68b-sunlight-endpass-xctrace-4/command-buffer-errors.xml
tools/diagnostics/v68b-sunlight-endpass-xctrace-4/command-buffer-completed.xml
tools/diagnostics/v68b-sunlight-endpass-xctrace-4/current-allocated-size.xml
tools/diagnostics/v68b-sunlight-endpass-xctrace-4/gpu-timeline.xml
```

统计：

- 跟踪跨度 15.68 秒，473 帧；
- 13,885 次 application command-buffer submission，约 29.4 次/帧；
- 18,082 次 command-buffer completion；
- completion gap 中位数 0.523 ms，p95 3.576 ms，p99 4.265 ms，最大 36.97 ms；
- 只有 4 个 completion gap >16 ms，只有 1 个 >33 ms；
- 216,915 个 Ryujinx GPU active interval，Fragment 101,928、Vertex 101,900、Compute 13,087；
- interval p50 0.0113 ms、p95 0.0951 ms、p99 0.2504 ms、最大 1.3411 ms；
- Metal command-buffer error 为 0；
- GPU allocated size 首值约 1.00 GiB，最后约 0.919 GiB，预热后稳定在约 0.93 GiB。

因此“驱动执行失败/长 stall/显存风暴导致每隔一帧白屏”与数据不符。

## 7. GPU Frame Capture 现状

### 7.1 第一次：文件有效，但 capture 边界在 Commit 前关闭

目录：

```text
tools/diagnostics/v68b-sunlight-scoped-gputrace-2/
```

trace：

```text
tools/diagnostics/v68b-sunlight-scoped-gputrace-2/ryujinx-metal-20260802-012956.gputrace
```

它只有约 40 KiB，metadata 显示 `captured_frames_count = 1`。但 Xcode Replay 明确提示：

```text
GPU Capture is empty
No GPU commands have been captured. At least one command buffer must be
created and committed within the boundaries of a GPU Capture.
```

根因在诊断器，不在游戏：`FrameCapture.OnPresentBegin()` 在
`Pipeline.FlushCommandsImpl()` 提交 command buffer **之前**停止 capture。

### 7.2 第二次：把 scope 延长到 Commit，卷入整帧并冻结

诊断候选：

```text
artifacts/terminal/Ryujinx-metal-v70-capture-commit/
SHA-256 ec9415d02159615afcc37df690ac6d96a11485cbde3caa38d941b72257d3a0fc
```

v70 在目标 tone-map program 前打开 scope 并立即 flush。结果是旧 command buffer 的整帧资源也被
Metal 序列化，生成约 585 MiB 的未完成 trace，并卡在 capture；脚本已终止精确候选 PID。

临时 trace：

```text
/tmp/ryujinx-metal-20260802-015028.gputrace
```

它没有最终 metadata/index，不能直接当完整 `.gputrace` 交付，但 Apple 已落盘原始纹理文件。
若系统临时目录被清理，该 585 MiB 文件可能消失；已解码出的关键 PNG 和统计已放进仓库工作区。

### 7.3 已解码的原始纹理

解码工具：

```text
tools/decode_gputrace_textures.py
```

结果：

```text
tools/diagnostics/v70-sunlight-scoped-gputrace/decoded-textures/stats.json
tools/diagnostics/v70-sunlight-scoped-gputrace/decoded-textures/texture-36742-0-mipmap0-slice0-exposure-025.png
tools/diagnostics/v70-sunlight-scoped-gputrace/decoded-textures/texture-37755-0-mipmap0-slice0-exposure-1.png
```

关键纹理：

| Metal 资源 | 格式/尺寸 | 统计 | 当前解释 |
| --- | --- | --- | --- |
| `MTLTexture-36742` | R11G11B10Float, 1600×896 | luma median 0.967，p99 3.151，max 12.318 | HDR 场景输入 |
| `MTLTexture-37755` | R11G11B10Float, 1600×896 | luma median 0.579，p99 0.931，max 0.993 | tone-map 后的 0–1 输出 |
| `MTLTexture-37532` | R11G11B10Float, 324×180 | 大部分为 0 | 小分辨率后处理/历史资源 |

这次随机抓到的是**正常帧**：tone-map 输出不是纯白。它证明了解码链有效，但还不能裁决白帧中
错误发生在 HDR 输入之前、tone-map 内部还是其后。

## 8. v71 当前状态（未验证）

目录：

```text
artifacts/terminal/Ryujinx-metal-v71-capture-white/
SHA-256 9808aedbaf37c7f4068cb4e5b09db0875338a45145aa673696ce9b2a005490c2
```

状态：

- publish 的前台等待被用户中断，但后台已退出；当前没有 `dotnet`/MSBuild/Ryujinx 测试进程；
- 30 MiB executable 和原生库存在；
- `codesign --verify --verbose=4` 通过，签名是 ad-hoc；
- 未验证标题版本、游戏加载、capture 是否仍冻结；
- 只能作为诊断候选，不能替代稳定 App。

v71 的临时源码在：

```text
/private/tmp/ryujinx-v70-capture/source
```

它来自 HEAD 的 Git archive，只叠加了 capture 诊断改动：

1. 在 scope 外先 flush，避免把旧帧资源带入；
2. 打开 scope 后再提交一个空 command buffer，从而在 scope 内创建新 command buffer；
3. 把 scope 保留到 present-time commit 后；
4. GPU capture 活跃时关闭每 draw 的巨大日志；
5. 外部窗口检测器可以只在 `white` 事件触发 `/tmp/ryujinx-metal-capture`。

建议第一次只跑一次，不要循环：

```zsh
RYUJINX_TRIAL_GPU_CAPTURE=1 \
RYUJINX_TRIAL_GPU_CAPTURE_ON_WHITE=1 \
tools/run_metal_flicker_trial.zsh \
  artifacts/terminal/Ryujinx-metal-v71-capture-white \
  v71-white-capture-01 \
  METAL_CAPTURE_ENABLED=1 \
  RYUJINX_METAL_CAPTURE_FROM=3ebc3a8f6b77cc8f \
  RYUJINX_METAL_CAPTURE_TO=135f6cd77bde2f74 \
  RYUJINX_METAL_CAPTURE_COMMIT_SCOPE=1
```

如果输出目录已有同名标签，换一个新标签；脚本拒绝覆盖旧证据。

## 9. 接手者下一步（按优先级）

### P0：验证 v71 是否能产生小型、可重放的白帧 capture

1. 确认没有其他 Ryujinx 进程；
2. 执行上一节的一次性命令；
3. 如果 capture 超过约 100 MiB 或游戏冻结，不要继续循环；让脚本退出精确 PID；
4. 用 Xcode Replay 检查是否仍为 empty capture；
5. 将白帧里的同尺寸 HDR/tone-map 纹理解码，与 v70 正常帧对比。

判定矩阵：

| 白帧资源结果 | 结论方向 |
| --- | --- |
| HDR 输入 `36742` 已经接近全白 | 往更上游的 exposure/history/compute producer 查 |
| HDR 输入正常、tone-map 输出 `37755` 白 | 查 `3ebc3a8f6b77cc8f` / `135f6cd77bde2f74` 的采样纹理、uniform、load/store |
| 两张都正常、屏幕白 | 再回到 tone-map 后的 HUD 合成、present 源纹理与 view/slice 语义 |
| 白帧与正常帧交替使用两套资源 ID | 查 history ping-pong、view 底层资源和命令缓冲生命周期 |

### P1：如果 GPU Capture 仍然太重，改用一帧异步纹理快照

这比继续堆 env 开关更有效：

1. 在最终场景 pass 完成后、HUD 之前登记 one-shot copy；
2. 用独立 blit command buffer 把目标纹理复制到 shared buffer；
3. 通过 `CommandBufferScoped` 持有源纹理和底层资源直到 completion；
4. completion callback 只把固定大小数据放入内存 ring；
5. 外部监测器确认 white/recovery 后才将对应 ring 槽写盘；
6. 不要在 draw 编码前 `EndCurrentPass()` + 同步 readback；那会改变被测对象并重演旧探针错误。

至少同时抓：

- `3ebc3a8f6b77cc8f` 前的 HDR 输入；
- 该 draw 后的 tone-map 目标；
- `135f6cd77bde2f74` 后的目标；
- 相关的 8×1 exposure texture/uniform；
- 帧号、guest texture key、Metal identity handle、view handle、command buffer ID。

### P2：白帧根因修完后再做正确性回归

用户另有一个未解决的 gameplay 回归：轮盘可选究极手，但抓不起物体；未修改的 Ryujinx 1.3.3
可以正常抓取。不要把它与视觉闪烁混成一个问题。先用 HEAD clean candidate 与当前 dirty candidate
做二分，重点关注 visibility query/counter、strict sync、fence 和 GPU->CPU 报告路径。

## 10. 当前工作树风险

`git status` 不是干净的。除了 `artifacts/` 和 `tools/`，还有多处未提交源码实验，涉及：

- `SemaphoreUpdater.cs`
- `TexturePool.cs`
- Metal command buffer、counter、encoder state、texture、window、staging buffer
- NvHostCtrl event/report 路径
- 新文件 `CounterManager.cs`、`ObjcOwnership.cs`、`ToneMapProbe.cs`、`StrictSync.cs`

这些改动包含过内存、drawable 释放、visibility query、严格同步等实验。它们可能解释“究极手失效”、
旧版本内存持续上涨或某些候选闪退，但没有被统一验证。接手时：

1. 先 `git status --short`；
2. 不要 reset/checkout 掉用户工作；
3. 正确性实验优先从 `git archive 0a7fbbdf` 的干净临时快照叠单一改动；
4. 只有单变量实测通过后才把改动移回工作树；
5. Git commit 不包含 `artifacts/` 和未跟踪诊断产物，交接时必须单列路径。

## 11. 内存问题的已知教训

过去的“footprint 数十 GiB、RSS 只有几 GiB”主要是 Metal/GPU 资源被引用链长期保留，
包括 TexturePool alias 列表/refcount、全局保留 aliased RT 和 Objective-C/command-buffer 生命周期问题。
修复闪烁不能靠永久保留资源：

- `useResource(s)` 只声明 GPU 驻留，不替应用持有 Objective-C 对象；
- texture view 必须持有底层 texture；buffer-backed texture 必须持有底层 buffer；
- 逻辑句柄可立即替换，旧 Metal 对象要等引用它的 command buffer 完成后才真正释放；
- 每个生命周期修复必须同时跑 10–15 分钟闪烁观察和显存稳定性观察；
- “不闪但内存一直涨”不是修复。

v68b 的 System Trace 中 GPU allocated size 预热后约 0.93 GiB，说明该干净基线没有短时持续泄漏。

## 12. 构建方法

File Provider 工作区里 publish 如果在项目图/裁剪阶段停滞，不要重复碰运气。使用本地临时快照：

```zsh
dotnet build-server shutdown
```

然后在 `/private/tmp` 的完整 Git archive 源码内发布，输出到新的候选目录，并保留版本参数：

```zsh
dotnet publish src/Ryujinx/Ryujinx.csproj \
  -c Release \
  -r osx-arm64 \
  --self-contained true \
  --disable-build-servers \
  -m:1 \
  -p:BuildInParallel=false \
  -p:EnableCompressionInSingleFile=true \
  -p:Version=1.3.3+local-metal-<candidate> \
  -p:AssemblyVersion=1.3.3.0 \
  -p:FileVersion=1.3.3.0 \
  -p:InformationalVersion=1.3.3+local-metal-<candidate> \
  -o /Users/ssssss/Documents/Ryujinx_Optime/artifacts/terminal/<new-candidate>
```

每个候选至少记录：完整路径、Git 基线、叠加改动、SHA-256、签名类型、CDHash、运行状态。

## 13. 最小验收

每个“修复候选”而非纯诊断候选都要完成：

1. 独立目录启动，标题版本正确；
2. 实际加载目标游戏并越过 shader/pipeline 阶段；
3. 第一个强阳光存档连续 10–15 分钟无高频闪烁；
4. 活动监视器/Metal trace 显存预热后趋稳；
5. 究极手能抓取物体；
6. 植被、阴影、瘴气、爆炸、声音和 mod 正常；
7. 与 Vulkan 在同存档、同机位、同配置、同缓存条件比较；
8. 不覆盖已验证 App，不把终端可运行误写成 Finder App 已交付。

## 14. 当前停点

- 没有 Ryujinx 测试进程或 dotnet/MSBuild 构建进程在后台运行。
- v71 文件已生成并通过 ad-hoc `codesign` 验证，但完全未做运行验证。
- v70 的 585 MiB 临时 trace 仍在 `/tmp`；关键纹理的解码结果已持久化到 `tools/diagnostics/`。
- Xcode 曾打开过空的 v68b gputrace；不要把其 Replay 错误解释成 Metal 渲染失败。
- 下一步应是**单次 v71 白帧触发验证**；若仍冻结，停止 GPU Capture 路线，改做异步纹理快照。
- 本文和工具目前都未提交；由接手者在确认内容后决定是否提交。

## 15. 2026-08-02 下午续：FrameProbe 与新的定位结果

### 15.1 新仪器

`src/Ryujinx.Graphics.Metal/FrameProbe.cs`（env `RYUJINX_METAL_FRAME_PROBE=1`，
报告间隔 `RYUJINX_METAL_FRAME_PROBE_INTERVAL`，默认 300 帧）。

设计约束是"不能扰动被测对象"，所以：

1. render pass 开始时只把附件**引用**记进内存数组，零 Metal 调用、零 pass 拆分；
2. 所有 blit 采样集中在 present 时的**一个 blit encoder**，每个目标取相距很远的两块 16×16
   （单块会被一片平坦天空骗到）；
3. 隔 6 帧后再读 shared buffer——那时命令缓冲早已完成，**不需要任何等待**；
4. 每 120 帧只打**一行**聚合日志，避免逐帧日志灭火（已知 Heisenbug）。

判决列是 `onFlat(-2..+2)`：present 走 nvnflinger 缓冲队列，和命令流**不同相**，
所以必须做偏移相关，不能假设同帧对齐。实测对齐在 **offset −1**。

### 15.2 已确定（都有仪器支撑）

| 结论 | 证据 |
| --- | --- |
| 白帧是**常数色**，不是过曝留结构 | 场景区 std=0.33，只有两种颜色，与正常帧残差相关 **-0.011** |
| present/drawable/颜色管理无罪 | 送显源纹理里读到的就是 `0xFFFDFEFE` |
| **不是**坏掉的半个双缓冲 | 两张送显源 60/60 交替，白帧数几乎相等 |
| 是竞态，不是逐帧锁相 | 白帧占比 5%–40%，不与交换链同相 |
| **1920×1080 RG11B10Float 在白帧前一帧整片变常数** | offset −1，5/5、3/3、9/9、10/10、6/6 |
| 上游全部无辜 | 同批 72–161 个目标（含 1600×896 HDR、深度、bloom 链）零命中 |
| 那个常数是**算出来的**，不是 clear | `0x77DDFBBF`≈(0.992,0.992,0.984)；精确 clear 是 `0x781E03C0`=(1,1,1) |
| 写它的 draw 好坏帧完全一致 | 恒为程序 `ff14da1aca4ab8ca`、单纹理输入、cbuf 相同 |
| 源纹理身份与坏帧无关 | `src[3] 3坏/57好`、`src[2] 4坏/64好`，按使用比例分布 |
| 送显纹理近 3 帧内从不是 `RenderTargets[0]`，也无 SetData/CopyTo | 好坏帧均 `absent`（已解析 texture view 到底层资源） |

### 15.3 下一步

送显纹理只有两个稳定指针，却查不到任何"是谁写的"。补齐这三条即可闭环：

1. `NotePass` 目前只记 `RenderTargets[0]`，要记 **MRT 全部索引**；
2. 挂 **blit 拷贝**路径（`TextureCopy.cs`、`CopyFromOrToBuffer`），不只是 `Texture.SetData`/`CopyTo`；
3. 挂 **compute image store**。

### 15.4 踩过的坑（别重犯）

- `pkill -f "artifacts/terminal/Ryujinx-metal"` **一个都匹配不到**：脚本是
  `cd <候选目录> && ./Ryujinx`，进程命令行里没有那段路径。用 `pkill -f "Ryujinx -g Metal"`
  并且**回读验证**，否则会静默变成双实例。
- `run_metal_flicker_trial.zsh` 里 `rg` 遇到含 NUL 的日志会当成二进制**静默不匹配**，
  导致等待标题菜单超时。已改为 `rg -a`。开机到标题菜单要 ~100 秒，超时已调到 240 秒。
- 每区间只打一条样例日志会制造**假相关**（"坏帧绑了别的纹理"就是这么来的），
  结论一律要用 bad/good 分组统计，不能用轶事。

## 16. 单变量后端对比（2026-08-02 晚）——共享层出局

同一个候选二进制 `artifacts/terminal/Ryujinx-metal-v88-chooser`，同一个存档、同一场景，
唯一变量是 `-g Metal` / `-g Vulkan`。

### 16.1 症状级

外部窗口监视器 `tools/monitor_metal_window.py`（CGWindow，后端无关），各 90 秒：

| 后端 | 白帧事件 | 证据目录 |
| --- | --- | --- |
| Vulkan | **4** | `tools/diagnostics/sym-vulkan/` |
| Metal | **698** | `tools/diagnostics/sym-metal/` |

约 175 倍。**高频阳光风暴是 Metal 特有的**，这次是自测数据，不再是引用他人结论。
（注意：老的低频红/白单帧闪两个后端都有，属于共享层，别和这个混为一谈。）

### 16.2 决策级

共享层新增 `RYUJINX_LOG_PRESENT_CHOICE=1`（`Ryujinx.Graphics.Gpu/Window.cs`，
故意放在后端之上，两个后端能产出同一张表）。两边输出**逐字节相同**：

```text
distinct=2
[scale=1 host=1920x1080 guest=1920x1080 fmt=R8G8B8A8Unorm addr=0x655F1D8000 n=150]
[scale=1 host=1920x1080 guest=1920x1080 fmt=R8G8B8A8Unorm addr=0x655FA48000 n=150]
```

⇒ `FindOrCreateTexture` 在两个后端上交出**同一张 `Image.Texture`**（同客户地址、同尺寸、
scale=1、严格 150/150 交替）。**上层纹理缓存、送显选择、分辨率缩放全部排除。**

### 16.3 Metal 侧仍未解释的矛盾

对被送显的那张宿主纹理，Metal 后端的探针给出：

- 任何 MRT 索引的渲染附件：**0**
- `Texture.SetData` / `Texture.CopyTo`：**0**（阳性对照 `writeHookTotal=9672`、
  `kinds=SetData,CopyToSlice,CopyToLayer,SetDataLevel` 证明钩子活着）
- 送显时 `TextureGroup.SynchronizeMemory`：`g=0`（一个 handle group 都没评估，
  说明 `Texture.SynchronizeMemory` 提前返回）

它却在约 80% 的帧上内容正确、约 5–20% 的帧上是一个常数。

**唯一自洽的解释**：Metal 侧被我当作"送显源"的 `Texture` 对象，与客户实际渲染进去的
宿主纹理**不是同一个对象**（`Image.Texture.HostTexture` 可能是 view，或 storage 被替换过）。

### 16.4 下一步（一次构建即可判定）

在**上层**同时记录两个哈希并对比：

1. `RuntimeHelpers.GetHashCode(texture.HostTexture)`，在 `Gpu/Window.Present` 送显时；
2. 同样的哈希，在客户绑定渲染目标时（`TextureManager` 的 render target 绑定处）。

命中 ⇒ 我的 Metal 侧追踪有误，回去修追踪；从不命中 ⇒ 上层交出的是一张客户从未画过的纹理，
去查 `ReplaceStorage` / view 别名。

## 17. 送显纹理的"无写入者"悖论（2026-08-02 深夜）

用**原生指针**做身份（不再用托管对象），并给每个否定结论都配了阳性对照，结果：

| 写入路径 | 覆盖方式 | 送显纹理上的结果 | 阳性对照 |
| --- | --- | --- | --- |
| 渲染附件（**全部 MRT 索引**） | `EncoderStateManager.CreateRenderCommandEncoder` → `NotePass` | `passes=0` | 开机/菜单期该纹理 `passes=1~2`，说明能测到 |
| `Texture.SetData` ×3 / `CopyTo` ×3 | 各自入口 | `writes=0` | `writeHookTotal=2085`，`kinds=SetData,CopyToSlice,TextureCopy,CopyToLayer` |
| blit 原语（纹理→纹理、缓冲→纹理） | `TextureCopy.Copy` / 两处 `CopyFromOrToBuffer` | `presentWriter=NEVER(view)` | `nativeDsts=156` 个不同目标指针 |
| compute image store | `TextureBindingsManager.SetImage` | 从未绑定 | ⚠️ `imageSet=0`，**阴性对照弱**，不能排除钩子没走到 |
| `TextureGroup.SynchronizeMemory` | 计数器 | `g=0`（提前返回） | — |

已确认 `src/Ryujinx.Graphics.Metal/Pipeline.cs` 里剩下的两处 blit 是 `ClearBuffer`（缓冲）和转发函数，
不写纹理；纹理写入路径已枚举完毕。

**悖论**：这张纹理约 90% 的帧内容正确、约 10–20% 的帧是一个常数，却查不到任何写入者。

**最可能的解释（下一步要验的）**：`presentWriter` 报的是 `NEVER(view)`，说明送显纹理**是一个 view**。
而 `FrameProbe.RootHandle()` **只回溯一层**（`Texture.ViewSourceAuto`）。
`Texture.CreateView` 传的是 `_identitySwizzleHandle`，而对一个 view 来说它本身又是 view，
所以会形成 base → viewA → viewB 的链，`RootHandle(viewB)` 只能拿到 viewA，拿不到 base。
写入落在 base 上就会被全部漏掉——这能一次性解释上表所有的 0。

**修法**：在 `Texture` 里加一个 `ViewRootPtr`，view 构造时从父级继承（需要让
`Texture.CreateView` 把 `this` 也传下去，目前只传 `_identitySwizzleHandle`），
然后所有身份比较都用 `ViewRootPtr`。这是一次构建就能判定的事。

## 18. ⚠️ 测量方法作废重来（2026-08-02 深夜）

### 18.1 噪声底噪比任何待测效应都大

同一个二进制、同一个存档、同样的 90 秒窗口，白帧事件数实测：

```text
1255  1272  1277  1265  1228  1310  1071  1279  1236  643  535
```

**535 到 1310**。低读数在"修复开"和"修复关"两侧都出现过。
所以 **n=1 的跨进程 A/B 在这个课题上完全不可用**。

**这直接影响本文档第 5 节那张"已排除"表**：那些结论全部来自 15 秒单次运行（约 200 事件）的
n=1 测量，**在统计上站不住**，需要用下面的谐波重验后才能继续引用。

我自己也栽在这上面：先测到"mirror 修复开 643 / 关 1265"，报了"腰斩"；
交替重复三次后是 `FIX 1071/1279/1236` vs `NONE 1228/1310/535`，
差值 −171 而 2×SE=508 ⇒ **不显著，结论收回**。

### 18.2 正确工具：单会话内交错

```text
tools/interleave_ab.sh      # 一次会话内按秒轮换档位，记录切换时刻
tools/interleave_score.py   # 按墙钟把事件归入当时生效的档位，报 mean/sd/SE 和 2×SE 判据
src/Ryujinx.Graphics.Metal/MirrorAb.cs   # 每帧重读 /tmp/ryujinx-metal-mirror-ab
```

用法（档位 0/1/2，每段 6 秒，30 轮）：

```zsh
tools/interleave_ab.sh Ryujinx-metal-v105-interleave <标签> "0,1,2" 6 30
tools/interleave_score.py tools/diagnostics/<标签> 1.0
```

要点：

1. 开关必须**每帧重读**（`MirrorAb.RefreshToggle()` 挂在 `Pipeline.Present`），
   翻转随时安全——只 gate 镜像的**创建**，已存在的 pending data 仍按原样正确服务；
2. 闪烁是**阵发**的，段长必须短（6 秒级），否则一整波风暴会整个落进某一档；
3. 判据是 **|差| > 2×SE**，SE 按每段速率的样本算，不是按总数；
4. 切换后丢弃头 1 秒。

### 18.3 已用新谐波测过的

| 对比 | 结果 |
| --- | --- |
| mirror 原始行为 vs 加了陈旧镜像失效 | 3.78/s vs 3.73/s，2×SE=5.55 ⇒ **无效果** |
| mirror 开 vs 完全不建 mirror | 3.78/s vs 2.66/s，2×SE=4.27 ⇒ **不显著** |

（20 秒段、n=6 的第一轮；6 秒段、n=30 的第二轮结果见 `tools/diagnostics/il-mirror2`。）

### 18.4 两个 mirror 修复本身仍是对的

即使没有可测的闪烁收益，这两处仍是真 bug，建议保留：

1. `BufferHolder.SetData` 的 staging fall-through 分支只清了 pending range，
   **没清按 `(offset,size)` 缓存的镜像**——之后只要有任何 pending range 与请求重叠，
   `TryGetMirror` 就会命中那个过期镜像，把写入前的字节喂给着色器；
2. `SignalWrite` 只清转换缓冲缓存，**不碰镜像**——GPU 侧写入后，
   镜像里合成的那份"真缓冲字节"就过期了，没有任何地方会丢弃它。

## 19. 快速谐波与新的排除表（2026-08-02 深夜，续）

### 19.1 谐波（这是本轮最有价值的产出）

```text
tools/fast_ab.sh                         # 开一次机，之后按秒轮换档位，永不重启
src/Ryujinx.Graphics.Metal/MirrorAb.cs   # 每帧重读 /tmp/ryujinx-metal-mirror-ab 的共享档位
FrameProbe 的 frameprobe-ab 日志行        # 进程内逐帧 flat 判定，按档位累加 flat/total
```

关键改进：

1. **响应量换成进程内逐帧判定**（伯努利样本），不再用外部截屏数"事件"。
   统计功效高一个数量级，也不用等 90 秒窗口、不用存 PNG。
2. **一次开机测多个假设**。每个档位 = 基线 + 恰好一个改动，六档共用同一次开机、
   同一段时间、同一波阵发风暴，慢漂移自动抵消。之前每个假设要 2.5 分钟开机 + 4~10 分钟测量。
3. **必须用增量，不能用累计值**：脚本启动时档位是 0，开机黑屏那几百帧全是 flat，
   会把 L0 的比率整体抬高（实测 17.6% vs 增量后的 12.8%）。分析一律以第 3 份报告为基线做差。

判据仍是 |差| > 2×SE。

### 19.2 用新谐波测过的（全部单变量、同会话）

| 档位 | 改动 | 相对基线 | 判定 |
| --- | --- | --- | --- |
| L1 | 两处陈旧镜像失效修复 | +1.39pp | 不显著 |
| L2 | 完全不建 buffer mirror | −0.72pp / +1.74pp（两轮） | 不显著 |
| L3 | `TextureBarrier` 永远拆 pass | +2.15pp | 不显著 |
| L4 | 每次 draw 全量重绑（无视脏标记） | +2.51pp | 不显著 |
| L5 | 关闭状态缓存 | +3.65pp | 勉强越线，但方向是**变差** |
| L6 | 关闭 auto-flush | −0.22pp | 不显著 |

**没有一个改动降低闪烁。** 而且规律很一致：**凡是让流程更保守/更慢的改动都略微变差**
（全量重绑、永远拆 pass、关缓存），没有一个变好。

### 19.3 这条规律本身就是证据

竞态类 bug 加同步应该变好。全部同步/绑定/提交类改动免疫，说明**成因不在"缺同步"这一类**。

剩下同时满足以下三条的只有**着色器侧的未定义行为**：

- Metal 独有（MSL 代码生成与 SPIR-V 不同路）；
- 间歇性（UB 的取值每帧不同）；
- 对所有同步/绑定/提交改动免疫；
- 能算出一个**饱和的常数**（第 15 节测到的 `0x77DDFBBF` ≈ (0.992,0.992,0.984)，
  是色调曲线渐近值，不是 clear）。

典型子类：常量缓冲越界读（Metal 读到相邻数据，Vulkan 有 robustness 返回 0）、
未初始化的 threadgroup 内存、缺失的 barrier 语义。

### 19.4 下一步

1. 在 `src/Ryujinx.Graphics.Shader/CodeGen/Msl/` 给常量缓冲访问加钳位，作为新档位 A/B；
2. 对第 15 节点名的程序 `ff14da1aca4ab8ca` 导出 MSL 与 GLSL/SPIR-V 对照，找取值路径差异；
3. 用同一套谐波验证——现在一次开机能测 6~7 个变体。

## 20. ⚠️ 因果读反了：第 15 节的"断点"其实是下游回声

第 15 节把 1920×1080 RG11B10Float 缓冲的均匀值 `0x77DDFBBF` 解释成
"色调曲线饱和段的渐近值"，据此判定断点在那一级。**这个解释是错的。**

按 R11G11B10 位布局精确解码（R/G 各 5 位指数+6 位尾数，B 为 5+5，指数偏置 15）：

| 分量 | 位值 | 指数/尾数 | 数值 | 对应 8 位 |
| --- | --- | --- | --- | --- |
| R | 0x3BF=959 | e=14, m=63 | 2⁻¹×(1+63/64)=**0.99219** | **253/255** |
| G | 0x3BF=959 | e=14, m=63 | 0.99219 | 253/255 |
| B | 479 | e=14, m=31 | 2⁻¹×(1+31/32)=**0.98438** | ≈251/255 |

而送显的 RGBA8 白帧是 `0xFFFDFEFE` = (253, 254, 254)。
**在 R11G11B10 的精度下（G 的尾数步长约 0.016、B 约 0.031），这两个就是同一个值。**

⇒ 那张浮点缓冲装的是"白色 8 位帧缓冲转成浮点"，是**白帧的回声**。
第 15.2 节测到的"写它的 draw 恒定是单纹理输入的全屏拷贝程序 `ff14da1aca4ab8ca`"
正好印证：它就是个拷贝，位于**下游**。

**教训**：一个"看起来像饱和值"的常数，先按目标格式的位布局精确解码再解释，
不要凭"接近 1.0"就断定它是算出来的。差别在于：算出来的饱和值指向着色器数学，
而"某个 8 位值的浮点表示"指向一次格式转换拷贝——两者的排查方向完全相反。

**因此第 15/17 节的定位要重新表述**：offset −1 的 100% 相关是**下游同步变白**，
不是断点。真正未解的仍是：**是什么让那张 1920×1080 RGBA8 帧缓冲整片变成 ≈(253,254,254)**，
而 Metal 后端在它被送显前从未写过它（第 17 节，多重阳性对照）。

## 21. 断点定位到 1920×1080 合成级（2026-08-02 深夜，完整目标集）

### 21.1 先修掉一个静默截断

`FrameProbe` 每帧最多跟踪 32 个目标，而放宽尺寸下限后每区间出现 72–161 个不同目标
⇒ **后处理链的若干级被静默挤出跟踪集**，此前所有"上游全部无辜"的结论都可能是截断造成的。

已改为 `MaxTargets = 96`、最小尺寸 256×144，并**显式上报 `truncated=`**。
实测 `truncated=0`，跟踪集完整。

### 21.2 完整目标集下的判决

游戏内、600 帧、其中 80–81 帧为 flat：

| 目标 | onFlat(−2..+2) | 均匀值 |
| --- | --- | --- |
| 1920×1080 RG11B10Float | 10/26/**63**/13/11 | `0x77DDFBBF` |
| 1920×1080 RGBA8_sRGB | 8/8/**35**/6/8 | `0xFFFDFEFE` |
| 1920×1080 RGBA8 | 10/16/**43**/41/14 | `0xFFFDFEFE` |
| 1920×1080 RGBA8_sRGB | 2/17/**30**/7/3 | `0xFFFDFEFE` |

**1600×896 及以下的整条场景链（HDR、Depth32Float、bloom 400×224、320×180 等）零命中。**
峰值在 offset 0（同帧）——之前那个 offset −1 是截断加身份错位造成的假象。

⇒ **场景在 1600×896 渲染时是好的；白色是在放大/合成到 1920×1080 这一步产生的。**

注意同一批里同时存在 `RGBA8Unorm` 与 `RGBA8UnormsRGB` 的 1920×1080 目标——
同一块内存被以 sRGB 和非 sRGB 两种视图使用，这本身值得单独查。

### 21.3 draw 普查的局限（下一步要修的）

`tools/diagnostics/census/` 的普查按"程序哈希 + 输入尺寸 + 纹理数"分组，结果只有一个程序
`ff14da1aca4ab8ca`（`rt=1920x1080 src=1920x1080 tex=1`，bad=127/good=550，按比例分布），
**没有出现 1600×896 → 1920×1080 的那一趟**。

原因是探针只记 `TextureRefs` 里**第一个**非空纹理（最低绑定槽），不一定是场景输入。
下一步把 `src` 改成**列出全部绑定纹理的尺寸**，再按 bad/good 分组，
就能认出"输入含 1600×896、输出 1920×1080"的那趟合成，以及它在坏帧上到底缺了什么。

## 22. 格式别名排除（用可信谐波复测）

第 5 节把 `RYUJINX_DISABLE_FORMAT_ALIASES=1` 列为"已排除"，依据是 15 秒单次运行——
该方法已在第 18 节作废。用单会话交错谐波复测（每档约 2700 帧，期间白帧率 12.75%，
闪烁确实存在）：

| 档位 | 白帧率 |
| --- | --- |
| L0 基线 | 12.75% ± 1.27 |
| L7 关闭格式别名 | 12.92% ± 1.30 |

差 +0.17pp，2×SE=1.82 ⇒ **不显著，格式别名确实排除**（这次结论可信）。

为此把该开关从 `TexturePool` 的启动期静态字段改为经由
`GAL.DrawDiagnostics.DisableFormatAliases` 每帧可切，才能做单会话对比。

## 23. 测量陷阱：停在存档列表 = 假的"0 白帧"

一次运行里两档都测出 **0/2105 和 0/2095**，看起来像彻底修好——实际是按键序列没把存档载入，
**整段测量都在存档选择界面上**。菜单不闪，所以零事件。

**`tools/fast_ab.sh` 已加载入确认**：等 `DefaultBootEvent`，10 次重试仍无则中止，
拒绝测量一个菜单。`Dm_OP_0038` 只代表标题画面出现，**不能**当作已进游戏。

**存档选择不能用位置法**：自动存档会把手动存档往下推（曾是第 4 个，现在是第 2 个）。
判据是角标：自动存档条目右下角有 "Autosave"，手动存档没有。

附带旁证：存档列表截图里，测试期间产生的两个自动存档**缩略图是纯白的**——
再次确认白色确实进了游戏自己的帧缓冲，不是送显阶段的问题。

## 24. 白色是固定字面常数，不是采到的场景像素

`FrameProbe` 现在对每个 flat 帧记录送显源的常数值并做直方图。跨越多分钟游戏、镜头持续移动：

```text
0xFF000000 x237     开机/加载黑屏（进游戏后不再增长）
0xFFFDFEFE x217     白帧主值（持续增长）
0xFFFDFDFE x21      抖动配对值
0xFF313131 / 0xFF484848 / 0xFF848484 各 x1   加载淡入淡出
distinct=9
```

游戏内的白帧**只取一对相邻的抖动值 (253,254,254) / (253,253,254)，全程不变**。

### 24.1 这一刀砍掉的假设

**任何取值随画面变化的机制全部出局**：

- 全屏 draw 的 UV/varying 坏掉导致每个像素采同一点（那会采到不同的场景像素）；
- 自动曝光爆炸 / 色调映射饱和（饱和值会随场景亮度小幅变化）；
- 读到相邻缓冲的残留数据（每帧不同）。

**剩下只能是"某处写入了一个常量"**：clear 颜色、常量输出的着色器、或采样器边界色。

### 24.2 当前的核心矛盾

第 17 节已用原生指针身份 + 多重阳性对照确认：**Metal 后端在送显前从未写过那张纹理**
（任意 MRT 索引的附件、`SetData`、`CopyTo`、最底层 blit 原语，全部为 0，
而 `nativeDsts=156`、`writeHookTotal>2000` 证明钩子活着）。

一个"从未被写过"的纹理却含有一个**固定常数**——这两条必须有一条是错的，
而它们各自都有阳性对照。下一步应当直接攻这个矛盾，而不是再提新的机制假设：

1. 在 `Texture` 构造后立刻用一个**可识别的哨兵值**填充新建纹理
   （例如 0xDEADBEEF），看白帧的常数是否变成哨兵值——
   若是，则白帧就是"**从未被写入过的新建纹理**"，问题回到纹理创建/复用；
2. 若哨兵值没出现，说明确实有写入者，那就是我的写入枚举仍有遗漏，
   继续按 `EnsureBlitEncoder()` / `CreateRenderCommandEncoder()` 的调用点逐个补钩子。

哨兵法是这个矛盾的直接判决，且一次运行即可出结果。

## 25. 哨兵判决：确实有写入者，是枚举漏了（矛盾解开）

### 25.1 哨兵法

`RYUJINX_METAL_SENTINEL=1`（`Texture.cs` 的 `FillSentinel`）在**新建**的 1920×1080
四字节纹理里用 `ReplaceRegion` 填满不透明绿。若白帧变绿 ⇒ 该纹理从未被写过；
若仍为白 ⇒ 确实有写入者，是探针的枚举有遗漏。

结果：**白帧仍是 `0xFFFDFEFE`/`0xFFFDFDFE`，绿色从未出现** ⇒ **有写入者，枚举有遗漏。**
第 17 节"Metal 后端从未写过那张纹理"的结论**作废**。

顺带拿到一条事实：全程只填到 **2 张**，且都是 `RGBA8UnormsRGB`
⇒ **1920×1080 的底层存储是 sRGB 格式，那些 `RGBA8Unorm` 目标是它的视图。**

### 25.2 漏在哪里

旧探针在 `EncoderStateManager.CreateRenderCommandEncoder` 里读
`_currentState.RenderTargets`——那是**间接**的编码器状态快照。
真正的漏斗是 `Texture.PopulateRenderPassAttachment`：**所有颜色附件必经此处**，
而且能拿到真正被绑上去的原生指针。

改挂之后立刻见效：

```text
presentWriter[self:Attachment]=flat30,ok270
presentWriter[root:Attachment]=flat29,ok271
<presentSrc@N-0:m=2,passes=1,draws=0,writes=0:flat=59,ok=541>
```

送显纹理**每帧恰好被附加一次**，好帧坏帧比例一致。

### 25.3 方法论教训

**"没有写入者"这种否定结论，必须用哨兵法之类的正向反证来验，不能只靠枚举钩子。**
枚举的完备性无法自证——我挂了渲染附件、`SetData`、`CopyTo`、最底层 blit 原语，
每一处都有阳性对照，却仍然漏了真正的那条。哨兵一次运行就裁决了。

### 25.4 下一步

`draws=0` 仍是按托管对象引用比对 `state.RenderTargets[0]` 得到的，
而附件常常是**视图对象**，与预留条目不是同一个托管对象，所以 draw 记到了别的条目上。
把 `NoteDraw` 的目标归属也改成按 **root 原生指针**匹配，再按好帧/坏帧比较
"写进送显纹理底层资源的 pass 数与 draw 数"——这是点名坏帧缺了什么的最后一步。

## 26. 收敛到"同一趟 draw、同样输入、结果不同"

### 26.1 draw 枚举又漏了一次（第三次同类错误）

探针只挂了 `Pipeline.Draw`，**没挂 `DrawIndexed` 及另外 7 个入口**，
所以送显资源一直报 `draws=0`。补齐 8 个入口后：

```text
N-1 帧（构建这张缓冲的那一帧）：passes=2, draws=1, writes=2
     flat=55, ok=545        （另一份：flat=47, ok=553）
```

**好帧与坏帧的 pass 数、draw 数、写入数完全一致**，没有缺失或多余的 draw。

### 26.2 输入也完全一致

合成 draw 是程序 `480117a3b1123d65`，输入签名只有一种，好坏帧相同：

```text
in=1600x896/R32Float, 400x224/R32Float, 256x256/R16G16B16A16Float,
   260x260/R16Unorm x2, 4x4/R8G8B8A8Unorm, 68x68, 32x32,
   1920x1080/R11G11B10Float, 72x72/Bc4Unorm, 96x96/Bc4Unorm, 800x448/R32Float,
   16x16/R11G11B10Float, 500x400/R16G16Unorm, 800x448/R11G11B10Float x2,
   1024x1024/Bc4Unorm, 1000x800/Astc8x6Unorm, ...  (28 张)
=flat65,ok535
```

把 ≤64×64 的小输入强制纳入采样（曝光/自适应 LUT 就在这个尺寸区间），
逐帧记录其取值并按好坏帧分组：**没有任何一张的取值偏向坏帧**，
分布完全跟随整体比例。

### 26.3 关于"固定常数 ⇒ 不是曝光"的更正

第 24 节据"白色是固定常数"排除了曝光爆炸。**这个推理是错的**：
色调曲线在饱和段把所有大输入映射到同一个渐近值，**曝光一旦爆掉，输出正好是与场景无关的固定常数**。
曝光假说不能据此排除——但 26.2 的 LUT 取值统计确实没有支持它。

### 26.4 最后一个未排除的输入：常量缓冲

整块哈希无效：缓冲里含逐帧计数器，`cbufDistinct=600`（每帧都不同）。
改为**逐字段均值对比**（`cbufFields`，按好坏帧分别累加前 128 个 float）。

一份报告里出现了很强的信号：

```text
f33[rel=1.000 flat=1.83352E-43 ok=0.0124739]
f34[rel=1.000 flat=1.9531E-43  ok=-0.029106]
f35[rel=1.000 flat=2.07268E-43 ok=0.029106]
```

**1.8E-43 / 1.95E-43 / 2.07E-43 是次正规数**——它们是小整数（约 131 / 139 / 148）的位模式
被当成 float 读出来的结果。同一批 CB 采样里也出现过 `b2(9.753E-43,1.031E-42,...)`。

⇒ **坏帧上，常量缓冲的某些位置装的是整数数据，而好帧上是正常浮点。**
这是"读到了错误偏移 / 陈旧或错绑的缓冲区段"的指纹。

但另外两份报告没有复现 f33–f35，只有 f50–f53 的差异，且**方向在两份之间翻转**
（f53 一次 flat 更低、一次 flat 更高）⇒ 那几个是随场景变化的量，不是信号。

**下一步**：把 `cbufFields` 的统计改成**按帧记录原始值并落盘**（而不是只报均值），
才能确认 f33–f35 的次正规现象是否稳定复现、以及它对应哪个 uniform 绑定与偏移。
均值统计会把偶发的位模式异常稀释掉——这正是它只在一份报告里冒头的原因。

## 27. 常量缓冲次正规异常：有富集，但不足以解释

把"逐字段均值"改成"逐字段**次正规/非有限计数**"（均值会稀释偶发的位模式异常），
按好帧/坏帧分别统计。四份报告：

```text
odd(flat=0,ok=4615)
odd(flat=78,ok=456)  ODD f32..f43 [flat=19%, ok=6%]      <- 有信号
odd(flat=0,ok=0)
odd(flat=0,ok=0)
```

f32–f43（连续 12 个 float，即第 8–10 个 vec4）在坏帧上有 **19%** 的时间是次正规/非有限值，
好帧上是 6%，**富集约 3 倍**。

**但这不足以解释白帧**：81% 的坏帧并没有这个异常，而且另外三份报告完全没有复现
（其中一份好帧上反而有 4615 次）。⇒ **顶多是伴生现象，不是成因。**

结论：合成 draw 的**全部输入都已排查完毕**（28 张纹理的身份与内容、小 LUT 的取值、
常量缓冲的逐字段值与位模式），**没有一项能稳定区分好帧与坏帧**。

### 27.1 这意味着什么

命令流相同、绑定相同、输入内容相同，输出却不同 ⇒ 差异不在**提交给 GPU 的内容**里，
而在**执行**上。剩下的可能性收敛为两类：

1. **着色器执行层面的不确定性**（MSL 代码生成产生的未定义行为、未初始化的
   threadgroup/local 内存、缺失的 barrier 语义）；
2. **我的采样时机**——探针在 present 时采样，若该资源在 present 之后、送显之前还会被改动，
   我看到的就不是被送显的那一份。

第 2 条必须先排除，否则第 1 条无从下手。方法：在 present blit **之后**再采样一次同一资源，
两次采样不一致即证明存在 present 后的改动。

### 27.2 现在有能力做当初做不成的事

交接文档第 9 节的 P0（"抓一帧白帧的 GPU capture"）当初失败，是因为只能用**外部窗口监视器**
触发，延迟不可控。现在 `FrameProbe` 能在**进程内、约 6 帧内**确定判出白帧，
可以精确地在白帧上触发 Metal capture——这是当初不具备的条件。

## 28. 采样点已验证；输入侧全部排除；问题在执行层面

### 28.1 采样点验证

在 present blit **之后**对同一资源再采样一次并与第一次比对：

```text
LATE[unchanged-after-present]=flat256,ok338
LATE[unchanged-after-present]=flat3,ok597
LATE[unchanged-after-present]=flat32,ok568
LATE[unchanged-after-present]=flat41,ok559
```

**从未出现 `CHANGED-after-present`** ⇒ 探针采到的就是被送显的那一份，观测位置正确。

### 28.2 输入侧完整排除表

| 项 | 手段 | 结果 |
| --- | --- | --- |
| 命令结构 | 按底层资源归属统计 pass/draw/写入 | 好坏帧完全一致（passes=2,draws=1,writes=2） |
| 绑定纹理身份 | 28 张的尺寸/格式签名 | 唯一签名，flat65/ok535 |
| 大纹理内容 | 全链 96 目标、两块远距 patch | 白帧时只有 1920×1080 变常数 |
| 小 LUT 取值 | ≤64×64 强制采样、逐值分组 | 无一偏向坏帧 |
| 常量缓冲 | 逐字段均值 + 次正规位模式计数 | 无稳定差异（f32–f43 富集 3× 但只覆盖 19% 坏帧） |
| 采样点 | present 后重采 | 全帧一致 |

### 28.3 剩余空间

**提交给 GPU 的内容完全相同，执行结果却不同** ⇒ 差异在**执行**层面。这也解释了
为什么第 19 节里所有同步/绑定/提交类改动全部无效——它们改的是"提交什么"，而问题不在那里。

剩下的候选：

1. **MSL 代码生成的未定义行为**（`src/Ryujinx.Graphics.Shader/CodeGen/Msl/`）——
   合成程序是 `480117a3b1123d65`，应导出其 MSL 与 SPIR-V 对照；
2. **未初始化的 threadgroup / local 内存**；
3. **资源驻留声明缺失**——Apple GPU 上通过 argument buffer 引用的资源必须
   `useResource`，漏声明则采样结果未定义。注意 `AppliedRenderState` 有
   `LevelResidency` 缓存层；第 19 节的 L5（关状态缓存，含 residency）**没有改善**，
   这条据此降级但未完全排除（L5 同时关掉了三层，可能相互抵消）。

建议下一步先做 3 的**单独**开关（只关 residency 缓存，保留字段与 buffer 缓存），
再做 1 的 MSL 导出对照。

## 29. 找到白帧的直接产生机制：全屏常色雾叠加的 alpha 饱和

### 29.1 怎么找到的

`FrameProbe` 现在对每个写 ≥1900 宽目标的程序调用 `Program.DumpSources`，
MSL 落在 `/tmp/ryujinx-metal-shaders/`（已复制到 `tools/diagnostics/shaders-fullres/`）。
23 个片段着色器里，只有 `17344b41bb2af386` 的输出与像素位置无关：

```metal
out.color0.x = constant_buffers.fp_c3->data[0].x;
out.color0.y = constant_buffers.fp_c3->data[0].y;
out.color0.z = constant_buffers.fp_c3->data[0].z;
out.color0.w = temp_174;      // 只有 alpha 是算出来的
```

**RGB 直接取自常量缓冲，且该着色器不采样任何纹理** ⇒ 这是一个全屏纯色叠加
（雾/大气散射），颜色是雾色，不透明度是算出来的密度。

alpha 的计算：

```metal
temp_171 = -constant_buffers.fp_c4->data[0].z;
temp_172 = fma(temp_170, temp_171, temp_170);        // temp_170 * (1 - c4[0].z)
temp_173 = temp_172 * constant_buffers.fp_c3->data[0].w;
temp_174 = clamp(temp_173, 0.0f, 1.0f);
```

`temp_170` 由前面约 630 行、以 varying `inAttr4` 和常量缓冲 c1/c3/c4 算出。

### 29.2 为什么这条解释和全部观测自洽

| 观测（第 24/26/28 节） | 本机制的解释 |
| --- | --- |
| 白色是**固定常数**、与场景无关 | RGB 就是 `fp_c3->data[0].xyz`，一个常量 |
| 命令流、pass/draw 数、28 张绑定纹理**全同** | 这趟 pass 每帧都跑，变的只是算出来的 alpha |
| 对所有同步/绑定/提交改动免疫 | 不是竞态，是着色器算出的数值 |
| 抖动配对值 (253,254,254)/(253,253,254) | alpha 混合与抖动 |
| **偏偏在强阳光场景** | 该场景的雾色本来就接近白 |
| 1600×896 及以下场景链干净 | 叠加发生在 1920×1080 这一级 |

⇒ **白帧 = 这个全屏雾叠加的 alpha 被 clamp 到了 1（全不透明），整屏被涂成雾色。**

### 29.3 下一步（判决性验证）

加一个 A/B 档位，强制该程序（`DebugLabel == "17344b41bb2af386"`）的输出 alpha 为 0
（或直接跳过该 draw）。用第 19 节的谐波测量：

- **白帧率归零** ⇒ 机制确认，接着查 `temp_170` 为何在 Metal 上偶尔算爆
  （候选：`inAttr4` 的插值、c1/c3/c4 的取值、`fma`/`clamp` 的 MSL 语义、
  以及第 27 节那个 f32–f43 次正规富集是否正是喂给它的输入）；
- **白帧率不变** ⇒ 本机制被证伪，回到第 28.3 的其余候选。

注意：这是**诊断用**的强制，不是修复——真正的修复要落在让 `temp_170` 算对上。

## 30. 常色填充着色器：两次假阴性被阳性对照抓住，最终证伪

### 30.1 候选

游戏内写全分辨率目标的 23 个片段着色器（`tools/diagnostics/shaders-gameplay/`）中，
只有两个的输出与像素位置无关、且完全不采样纹理：

- `17344b41bb2af386`（651 行）：RGB 取自 `fp_c3->data[0].xyz`，alpha 由约 630 行算出；
- `2d938a71143b3873`（81 行）：**颜色和 alpha 全部取自 `fp_c3->data[0]`**，纯全屏常色填充。

第二个尤其像：一个 vec4 读错，颜色与不透明度会**一起**错，整屏变成固定常数。

### 30.2 两次假阴性

给"跳过该 draw"的 A/B 加了**阳性对照**（`overlaySeen` / `overlaySkipped`）后：

```text
17344b41bb2af386: overlaySeen=3311 overlaySkipped=0   （计数冻结）
2d938a71143b3873: overlaySeen=532  overlaySkipped=0   （计数冻结）
```

**跳过一次都没发生，且两者的计数在测量期间完全不增长** ⇒ 这两个程序在稳态游戏中
根本不画（只在加载淡入淡出等过渡时出现一次）。

因此两次"跳过无效果"的结果都是**空的**，而不是证伪；而它们的真实结论是
**这两个着色器都不可能造成稳定 15% 的白帧率**。

### 30.3 教训（与第 25 节同源，今天第四、五次）

**否定结论必须带阳性对照。** 若不是加了 `overlaySkipped`，我会把"跳过它没用"
当成证伪写进文档，并据此排除掉整条"常色着色器"路线——而真相是开关根本没生效。

另外，**导出着色器的门槛不能用帧数**：开机到标题就要约 3000 帧，`_frame > 2000`
根本没排除标题画面。现已改为"见过 1600×896 场景目标之后"——那是游戏内独有的分辨率。

### 30.4 当前结论

游戏内全分辨率着色器中**没有常色输出者在稳态运行**，
⇒ 白色不是某个"输出常量"的着色器画出来的，而是**某个采样纹理的着色器采到了常量**。

这与第 28 节的结论合起来把范围压到：
**一个采样纹理的全屏着色器，在输入内容相同、绑定相同、命令流相同的情况下，
偶尔产出与场景无关的固定常数。** 唯一自洽的剩余解释仍是执行层面的不确定性。

## 31. 抓到作案的 draw；PreserveInvariance 假说证伪

### 31.1 作案 draw 已确定

在每一趟写全分辨率目标的 draw **之前**采样该目标（`PreDrawSample`）：

```text
PRE[FLAT 480117a3b1123d65=structured SRC=structured] = f64,o0
PRE[ok   480117a3b1123d65=structured SRC=structured] = f0,o537
```

⇒ 坏帧上，**目标和源在这趟 draw 之前都是有结构的，输出却是常数**
⇒ **是 `480117a3b1123d65` 这趟 draw 自己制造了均匀性**，不是搬运了别处的常数。
游戏内只有这一个程序写全分辨率目标。

其 MSL（`tools/diagnostics/shaders-gameplay/`）只有一条采样：

```metal
temp_7 = 1.0f / in.position.w;
temp_8 = temp_7 * (in.inAttr0.x * in.position.w);   // x * w * (1/w)
temp_9 = temp_7 * (in.inAttr0.y * in.position.w);
out.color0 = tex_fp_t_tcb_8.sample(samp, float2(temp_8, temp_9));
```

源有结构而输出是常数 ⇒ **UV 在整屏上退化成了常数**。

### 31.2 假说与证伪

假说：`Program.cs` 的 `PreserveInvariance = true` 禁止编译器折叠 `x*w*(1/w)`，
于是除法真的执行，`w=0` 时 `Inf*0=NaN`，NaN 采样返回固定常数；
SPIR-V 路径没有该标志、会折叠掉，故 Vulkan 无此问题。

实测（`RYUJINX_METAL_NO_INVARIANCE=1`，两次独立运行，进程内逐帧 flat 率）：

| 配置 | flat 率 |
| --- | --- |
| `PreserveInvariance=true`（基线） | **17.33%** |
| `PreserveInvariance=false` | **17.33%** |

**完全相同 ⇒ 证伪。** 用户同时确认"还是会闪"。

### 31.3 下一步必须先补的一个洞

`PreDrawSample` 里的 `SRC` 取的是**第一个宽度 ≥1900 的绑定纹理**，
而着色器采的是**具体的绑定槽 `tex_fp_t_tcb_8`**。两者不一定是同一张
⇒ "源是有结构的"这个结论**可能查错了对象**，必须按槽位重取。
这与第 25/30 节是同一类错误，务必先补齐再据此推理。

### 31.4 用户提出的日夜对照（零构建成本，优先做）

现象是"白天明显、夜里不明显"，但外部检测器按亮度判（`--white-luma 245`），
夜里的**暗常数**会被漏掉，所以"夜里变少"无法区分真假。

**`FrameProbe` 判的是"整片是否同一个值"，不看亮度**，且记录 flat 取值直方图，
因此在一局内跨过日落读 flat 率即可分辨：

- 夜里 flat 率基本不变 ⇒ 故障一直发生，白天只是被曝光放大成刺眼的白；
- 夜里 flat 率明显下降 ⇒ 真与亮度相关，嫌疑收缩到只有强光才跑的那几级。

第一个存档可坐火过夜（用户提供）。当前白天基线：**flat 率 17.33%**，
flat 取值以 `0xFFFDFEFE` 为主。

## 32. 输入侧彻底封口；查阅公开资料无果

### 32.1 按槽位重取源纹理后的结论

第 31.3 节指出 `SRC` 取的是"第一个 ≥1900 的绑定纹理"，可能查错对象。
改为**逐槽位采样所有图像尺寸（≥256×144）的输入**后：

```text
PRE[FLAT 480117a3b1123d65=structured src0=structured src1=structured src2=structured
     src3=structured src128=structured src131=structured src133=structured]=f97,o0
PRE[ok   ... 全部 structured ...]=f0,o503
```

**坏帧上 8 个图像输入全部有结构，目标也有结构，输出仍是常数。**
⇒ 第 31.1 节的定位成立：**`480117a3b1123d65` 这趟 draw 自己制造了均匀性。**

### 32.2 公开资料

查了三轮，**没有可直接套用的现成答案**：上游 Ryujinx 2024-10 已停更，
公开的 TOTK-on-Mac 报告全部走 MoltenVK/Vulkan，与本 fork 的原生 Metal 后端不是同一条路径。

唯一命中症状类别的通则来自 Apple 官方（WWDC23 "Render with Metal"）：
**经由 argument buffer 引用的资源必须显式 `useResource:usage:stages:` / `useHeap`
声明驻留，否则行为未定义。** 未声明驻留的纹理，采样结果就是未定义的——
这正好能产生"同一趟 draw、输入相同、偶尔采出常数"。

但本 fork 的驻留缓存已用单会话 A/B 单独测过（第 19 节 L8，n≈2500）：
关闭后 +0.57pp、2×SE=1.91，**不显著**。故"驻留缓存漏声明"这条已排除。

仍待查的相关点：`IsResident` 用 **encoder 原生指针**判断"是否同一个 encoder"
（`Retarget`），而 Objective-C 会回收已释放对象的地址；若新 encoder 拿到旧指针，
缓存不会被清空。理论上 L8（永远重新声明）应当能盖住这种情况且实测无效，
因此该路径**降级但未完全排除**——`MaxResidentTracked` 上限、`usage` 标志是否正确
（例如采样应为 `Read`，而作为 attachment 的同一资源需要 `Write`）仍值得单独核对。

## 33. 找到第一个有判别力的输入：绑定槽位 146

### 33.1 先记两个被证伪的修复尝试

| 尝试 | 机制 | 结果 |
| --- | --- | --- |
| `PreserveInvariance=false` | 让编译器折叠 `x*w*(1/w)`，消除 NaN | 17.33% vs 17.33%，**证伪** |
| MSL 采样坐标 NaN/Inf 钳位（`RyujinxSafeCoord`） | Metal 上非有限坐标采样未定义 | 16.46% vs 17.33%，噪声带内，**证伪** |

坐标钳位那次已确认生效（`RyujinxSafeCoord` 出现在重新生成的 MSL 里），
且**同时把 `CodeGenVersion` 从 7354 提到 7355**——否则磁盘着色器缓存会直接吐旧翻译，
改动静默失效。改 MSL 代码生成时必须一并提这个版本号。

### 33.2 判别力发现

此前 `PreDrawSample` 对绑定纹理加了 ≥256×144 的尺寸过滤，
**恰好把着色器真正采样的小槽位全部滤掉了**（第六次同类错误）。
去掉过滤、逐槽位全采后：

| 情形 | 坏帧 | 好帧 |
| --- | --- | --- |
| **槽位 146 为均匀** | **93** | **29** |
| 槽位 146 有结构 | 43 | 481 |

- P(坏帧 \| s146 均匀) = **76%**
- P(坏帧 \| s146 有结构) = **8.2%**
- **富集约 9 倍**

`s147` 在好坏帧上都均匀，是背景，不是信号。

这是整个调查里**第一个好坏帧不同分布的输入**——此前 28 张纹理、小 LUT、
常量缓冲逐字段值全部同分布。

### 33.3 下一步

1. 记录 s146 的**尺寸/格式/原生指针**（当前标签只有槽位号），确定它是什么；
2. 追它的写入者：谁在坏帧上让它变成均匀的；
3. 注意它**不是完美预测**（43 个坏帧上 s146 有结构），所以要么还有第二条路径，
   要么均匀性只是与真因共变的表征——需要在确定 s146 身份后重新判断。

### 33.4 ⚠️ s146 未能复现——该发现暂不可用

下一轮（唯一改动是把槽位标签加上尺寸/格式）在**同一个存档**（`PlayerPos` 完全相同）、
**同一个 flat 率区间**（16.6–17.0% vs 17.3%）下，**没有任何槽位被判为均匀**：

```text
PRE[FLAT]=f84,o0
PRE[ok  ]=f0,o505
```

条件相同而结果不同 ⇒ **33.2 的富集结论不可依赖**，在查清原因前不要据此推进。

可能原因（未验证）：

1. `TextureRefs` 的索引在不同运行间不稳定（`s146` 是数组下标，不是着色器的绑定槽位号）；
2. 标签改动影响了聚合键或解析（`bits[2].Substring(1)` 依赖 `index:label:eNN` 三段式，
   新标签含 `[]` 但不含 `:`，理论上不受影响——需实测确认）;
3. `PreDrawSlots=40` 与 `EntriesPerSlot` 的槽位分配在某些帧上耗尽。

**这是本轮第七次同类问题：仪器的可重复性没有先验证就用于推理。**
正确做法是：任何"好坏帧分布不同"的发现，**必须在同一条件下复现一次**才写进结论。

## 34. 对第 31 节定位的自我更正，以及本轮的诚实停点

### 34.1 "是这趟 draw 干的"并不成立

第 31 节据 `PRE[FLAT ...=structured]` 断定 `480117a3b1123d65` 这趟 draw 制造了均匀性。
**这是过度解读**：PRE 只证明"draw 之前目标有结构"，而均匀性是在 present 时观测到的，
**中间的任何步骤都可能是元凶**。

补了 draw **之后**的采样（`PostDrawSample`）：

```text
PRE[ok   AFTER=UNIFORM AFTER=UNIFORM]=f0,o502
PRE[FLAT AFTER=UNIFORM AFTER=UNIFORM]=f50,o0
```

**这趟 draw 之后目标在好帧坏帧上都是均匀的** ⇒ 它每帧都写一个均匀值，
**坏帧缺失的是它之后的某个阶段**。第 31 节的定位据此作废。

注意 `PostDrawSample` 会在每个 draw 后拆分 render pass，扰动很大，
该数据只可用于"是否均匀"的定性判断，不可用于比较 flat 率。

### 34.2 本轮的诚实停点

**问题未修复。** 本轮尝试并证伪的修复：`PreserveInvariance=false`、
MSL 采样坐标 NaN 钳位（两者都验证过真实生效）。
唯一有判别力的发现（槽位 146 富集 9 倍）**在同条件下未能复现**，不可依赖。

### 34.3 方法论总账（本轮共 7 次仪器错误）

| # | 错误 | 后果 |
| --- | --- | --- |
| 1 | 每帧目标表上限 32，静默丢弃 | "上游全部无辜"是假的 |
| 2 | 查表第一个匹配就 break | 真正的写入者被遮住 |
| 3 | 只挂了 9 个 draw 入口中的 1 个 | `draws=0` 是假的 |
| 4 | "没人写它"缺正向反证 | 被哨兵法一次推翻 |
| 5 | 着色器导出用帧数当门槛 | 导出集混入标题画面着色器 |
| 6 | 绑定纹理加 ≥256×144 过滤 | 恰好滤掉着色器真正采样的小槽位 |
| 7 | 未复现即写进结论（s146） | 下一轮同条件下消失 |

**规律：每次我加一个过滤/上限/捷径，都会把要找的东西挡掉；每次我不做正向反证，
否定结论都是错的。** 后续接手者请把这两条当作硬规矩。

### 34.4 建议的下一步：GPU 抓帧

继续"加探针→看差异"的边际收益已明显下降（7 次错误中 5 次是先下结论后发现仪器有洞）。
交接文档第 9 节的 P0（白帧 GPU capture）当初失败是因为只能**外部触发**、延迟不可控；
现在 `FrameProbe` 能在**进程内约 6 帧内**确定判出白帧，可以精确触发 Metal capture。
抓到一帧白帧的完整 GPU 状态，比再挂十个探针都直接。

## 35. 探针把游戏拖死（自伤，第 8 次仪器问题）

v138–v140 里我在**每一趟 draw 之后**调用 `PostDrawSample`，它会
`EndCurrentPass()` + blit 一次。每帧约 2500 个 draw ⇒ **每帧 2500 次 render pass 拆分**，
实测把模拟器拖到 **0.00 FPS（∞ms）**，窗口全黑、无法进游戏。

用户看到的"怎么现在进个游戏还不稳定"就是这个，不是游戏或存档的问题。

**规矩**：任何**每 draw 触发**的探针都必须假定它会致命——本作每帧 2500 draw、200 pass。
探针只能挂在**每帧一次**（present）或**事件触发**的位置；需要 per-draw 数据时，
只能记内存（零 Metal 调用），绝不能拆 pass 或做 blit。

已回退到只保留 present 时的整帧转储（v141）。

## 36. 整帧真相转储

`FrameProbe` 现在保留 8 帧的**整张 1920×1080 BGRA8 环形拷贝**（64 MiB），
判出 flat 帧后把对应槽写入 `/tmp/ryujinx-flatframe-<frame>.raw`。
比 GPU capture 安全（不会冻结），且给出的是**整帧地面真相**，
不再依赖已多次误导本调查的 16×16 采样块。

第一次转储抓到的是**开机黑屏**（全 0，单一颜色 2,073,600 像素）：
`_inGame` 门槛（见过 1600×896）挡不住加载画面，因为加载期也用该分辨率。
已追加"均匀值必须是亮色"的条件。

## 37. 环境退化：构建产物把机器压垮（第 9 次自伤）

测量在深夜完全跑不动（标题画面 1–3 FPS、自动化按键失效、存档反复载入失败）。
排查顺序与结论：

1. 怀疑 per-draw 探针 ⇒ 已移除（第 35 节），仍然慢；
2. 怀疑整帧转储 ⇒ 收到 `RYUJINX_METAL_FULLDUMP=1` 开关后面，仍然慢；
3. 怀疑 `CodeGenVersion` 提版作废缓存 ⇒ 查磁盘发现**根本没有 Metal 宿主缓存**
   （只有 `vulkan_apple.data`），Metal 每次运行本来就全量编译，与版本号无关；
4. **真因**：`uptime` 显示 load average **16.96 / 19.62 / 19.67**，
   `mds`（Spotlight 索引）占 33.9% CPU、`WindowServer` 39.7%。
   一天里发布了 **110 个候选、10 GB** 构建产物，Spotlight 正在全部索引。

删掉今天的 57 个一次性探针候选（保留文档引用的基线）后：
**10 GB → 6.8 GB，load 16.96 → 7.76，`mds` 退出 CPU 榜首。**

**规矩**：诊断候选是一次性的，**跑完即删**。在同一台机器上边发布边测量时，
构建产物的索引开销会直接污染被测对象——这不是"慢一点"，是让测量完全无法进行。

## 38. 本轮（第 31–37 节）总结

**问题仍未修复。** 本轮实现并证伪的修复：

| 修复尝试 | 验证方式 | 结果 |
| --- | --- | --- |
| `PreserveInvariance=false` | 两次独立运行，进程内 flat 率 | 17.33% vs 17.33% |
| MSL 采样坐标 NaN/Inf 钳位 | 确认 `RyujinxSafeCoord` 进入生成的 MSL | 16.46% vs 17.33% |

两者都已回退（后者连同 `CodeGenVersion` 一起）。

**仍然成立的定位**（见第 28、32 节）：提交给 GPU 的内容在好坏帧上完全相同——
命令结构、28 张绑定纹理的身份与内容、小 LUT 取值、常量缓冲逐字段值、采样点，
全部同分布；差异只在**执行**层面。

**已作废的定位**：第 31 节"是 `480117a3b1123d65` 这趟 draw 制造了均匀性"——
补了 draw 后采样发现该目标在**好坏帧上都**均匀（第 34 节）。

**下一步建议**：整帧转储的方向是对的（不依赖任何过滤条件），但要做成
**判出白帧后才开录**，而不是每帧都拷 8.3 MB。开关已就位（`RYUJINX_METAL_FULLDUMP=1`），
只需把触发改成事件式。

## 39. 测量环境已不可用（本轮硬停点）

事件式整帧转储已实现（v145，`RYUJINX_METAL_FULLDUMP=1`，判出白帧后才开录、
录到下一个白帧即落盘，代价从每帧 8.3 MB 降到十几帧），但**跑不出结果**：

```text
标题画面 1.37 FPS（728 ms/帧），按键导航无法进行，存档反复载入失败
load average 22.10 / 20.32 / 18.40
WindowServer 55% CPU
swap 已用 3.70 GB / 5.12 GB
Ryujinx footprint 6.88 GB
```

排查已排除：per-draw 探针（已移除）、整帧转储（已 env 门控且未 armed）、
着色器缓存版本（Metal 根本没有宿主缓存）、构建产物索引（已删 3.2 GB，load 一度回落到 7.76）。

剩下的是**机器本身**：连续约 12 小时、约 40 次模拟器运行 + 上百次构建之后，
交换区吃掉 3.7 GB，`Ryujinx` 常驻 6.88 GB（**这正是本仓库久未解决的内存泄漏**，
见 [[totk-flash-flicker-investigation]] 的泄漏线：会话约 15–20 分钟 OOM 一次）。

**在交换的机器上继续测量只会产出垃圾数据，比没有数据更糟。** 本轮到此停止。

### 39.1 恢复测量的前置条件

1. 重启或让机器空闲一段时间，确认 `swapusage` 回到低位、load < 5；
2. 诊断候选跑完即删（第 37 节）；
3. 先跑一次 `Ryujinx-metal-v144-gated`（探针全关）确认标题画面回到 30 FPS，
   再开始任何测量——**基线速度是测量前必须验证的前提**。

### 39.2 恢复后的第一件事

`artifacts/terminal/Ryujinx-metal-v145-eventdump` + `RYUJINX_METAL_FULLDUMP=1`，
拿到 `/tmp/ryujinx-flatframe-<frame>.raw`（1920×1080 BGRA8 整帧）。
这是不依赖任何过滤条件的地面真相——本轮 9 次仪器错误里有 6 次源于过滤条件。

### 39.3 基线速度实测：探针无关，机器已不可用

按 39.1 的前置条件跑 `Ryujinx-metal-v144-gated`（`RYUJINX_METAL_FRAME_PROBE` **未设置**，
探针完全关闭），启动时 load 已降到 4.93：

```text
标题画面 00.32 FPS（3099 ms/帧）
```

**同一天早些时候同一位置是 30 FPS。** 探针全关仍然 0.32 FPS ⇒
**慢的原因与本轮所有仪器改动无关**，是机器状态。

`swapusage` 显示 3.70 GB / 5.12 GB 已用且不随空闲释放，而 Ryujinx 常驻约 6.9 GB
（本仓库久未解决的泄漏）。**在这种状态下任何测量都不可信。**

**结论：必须重启机器才能恢复测量能力。** 这不是可以绕过的步骤——
本轮已有多个结论因为在退化环境里测量而被推翻。

### 39.4 修正：不需要重启，但机器的运行间性能方差极大

39.2 写的"必须重启"**是错的**。等 **1 分钟 load < 3** 再启动即可：

```text
v144（探针关）           29.98 FPS   ✅
v145（探针开，转储关）   30.01 FPS   ✅  ⇒ 探针本身开销可忽略
v145（探针开，转储开）    0.77 FPS
v147（同上，精确值触发）  1.66 FPS
v148（环形缓冲 8→2 槽）   0.00 FPS
```

前两行说明**探针本身不慢**。但后三行不能据此归因给转储：
把环形缓冲从 66 MB 降到 16.6 MB **没有任何改善**，而录制在标题画面根本没被触发。

⇒ **这台机器的运行间性能方差大到无法归因**：同一或近似的二进制，一次 30 FPS、
下一次 0–3 FPS。这与本文档第 18 节记录的闪烁率噪声是同一类问题——
**先前每一次"我改了 X 之后变慢了"的判断都可能是这个方差**。

**规矩**：任何性能归因都必须像第 18 节的闪烁 A/B 一样，**同条件重复多次**；
单次对比不可用。启动前必须等 1 分钟 load < 3，并**先验基线 FPS** 再开始测量。

## 40. 本会话最终状态

**白闪问题未修复。**

可信的结论（见第 15、19、24、28、32 节）：Vulkan 4 : Metal 698（共享层逐字节相同）；
白色是固定常数；输入侧全部同分布；差异在执行层面。
已证伪 11 个假设，含 2 个真实实现并回退的修复。

未完成：整帧地面真相转储（`RYUJINX_METAL_FULLDUMP=1`，v147/v148）——
代码已就位且逻辑正确，但因上述性能方差始终没能在游戏内触发一次。

建议下一位接手者：在**空闲机器**上按 39.1 + 39.4 的前置条件重跑该转储；
拿到 `/tmp/ryujinx-flatframe-*.raw` 之后，白帧的整帧像素是目前最有价值、
且**不依赖任何过滤条件**的证据——本会话 9 次仪器错误里有 6 次源于过滤条件。

### 39.5 真正的阻塞：单次运行内的渐进式降速

在**空闲机器**（1 分钟 load 1.78）上启动 v148：

```text
到达标题画面时     17.15 FPS
约一分钟后          1.67 FPS   （菜单按键已无法生效）
```

**同一次运行内、什么都没做的情况下，帧率一分钟里掉了 10 倍。**
这与探针、转储、缓冲大小都无关——是**本仓库久未解决的内存泄漏**
（`Ryujinx` 常驻约 6.9 GB，`swap` 3.6 GB）在标题画面就已经发作。

⇒ **当前环境下无法完成任何需要"进游戏并停留"的测量。**
先前 39.4 记录的 30 FPS 是启动瞬间的读数，不代表可持续。

**这是比白闪更优先的问题**：泄漏不解决，任何闪烁测量都只有开头几十秒可用。
本文档第 90 行起的"泄漏线"记录了历史尝试（v57 显式所有权、v59 弹池均失败）。

## 41. 泄漏计数对账：编码器/命令缓冲完全配平（推翻旧假说）

新增 `src/Ryujinx.Graphics.Metal/LeakCounters.cs`（`RYUJINX_METAL_LEAK_COUNTERS=1`），
对原生对象的创建/销毁做对账，每 60 帧连同托管堆一起上报。
标题画面静置约 15 分钟：

```text
leak tex=1346/1177(+169) views=1285 buf=2136/1338(+798) samp=0/0(+0)
     enc=153271/153271(+0) cb=23906/23915(-9) footprintMiB=811
```

**关键结论：**

1. **编码器 153,271 创建 / 153,271 结束，完全配平；命令缓冲同样配平。**
   ⇒ **推翻**本文档"泄漏线"里 v57（显式所有权配平）与 v59（每帧弹池）针对的
   "autorelease 编码器泄漏"假说——那两次失败的修复瞄错了对象。
2. 纹理未释放数从 +882 收敛到 **+169**（缓存在正常回收），缓冲 +798 稳定。
3. **托管堆仅 811 MiB**，而进程 footprint 约 7 GB ⇒ 差额在原生/GPU 侧，
   但计数显示这些对象是配平的。
4. 90 秒采样窗口内 **footprint 稳定在 7.05–7.11 GB，没有增长**。

⇒ **"内存泄漏"这个叙事本身需要重新审视**：至少在标题画面，
本后端跟踪的原生对象没有净增长。7 GB 是**高基线**而非持续泄漏。

## 42. 未解释的现象：高负载 + 零帧率 + 内存稳定

同一次运行，静置 15 分钟：

```text
FPS 0.00（∞ms）      load average 20.5      系统内存 85% 空闲（32 GB 物理）
footprint 稳定        编码器/命令缓冲配平     托管堆 811 MiB
```

**内存充足、对象配平，却 CPU 满载且出不了帧。** 且同一二进制在别的运行里
到达标题画面时是 17–30 FPS。

Metal 后端**没有宿主着色器缓存**（磁盘上只有 `vulkan_apple.data`），
每次运行都要全量编译 MSL，是高负载的合理来源；但 15 分钟仍未编完、
且帧率不恢复，说明它不是唯一原因。

**这是继续白闪调查前必须先解决的问题**——它使任何需要"进游戏并停留数分钟"的
测量都无法进行，也是本会话后半段大量误判的根源。

## 43. 渲染线程 91% 时间阻塞（`sample` 取证）

`sample <pid> 5` 的结果（`tools/diagnostics/renderthread-stall.sample`）：

```text
GUI.RenderThread  2180 采样
  └ 1984 (91%) Monitor_Wait → SyncBlock::Wait → __psynch_cvwait      ← 在等锁
    161        真正的 Metal 工作（renderCommandEncoderWithDescriptor、
                blitCommandEncoderCommon、setRenderPipelineState）
     33        ThreadNative_YieldThread / Sleep
```

⇒ **"CPU 满载出不了帧"不是渲染慢，是渲染线程被阻塞**；系统的高 load 来自客户机的
大量线程，不是渲染工作。

这与第 41 节的计数结果一致（对象配平、footprint 稳定）：
**问题不是资源泄漏，是同步停顿。**

排查方向（下一位接手者）：找出持有该监视器的一方。候选包括
`SyncManager` 的 `BufferModifiedRange` 等待（第 19 节的统计行显示每 120 帧
有 300–480 次等待、130–330 ms）、`CommandBufferPool.Rent` 的栅栏等待、
以及 GPFIFO 与呈现线程之间的交接。

## 44. 会话终点（2026-08-03 01:20）

**白闪问题未修复。** 本会话新增的可信结论见第 15–43 节；最重要的三条：

1. **Vulkan 4 : Metal 698**（同一二进制、共享层逐字节相同）⇒ bug 在 Metal 后端内部；
2. **输入侧全部同分布**（28 张纹理、LUT、常量缓冲逐字段、采样点）⇒ 差异在执行层面；
3. **编码器/命令缓冲计数完全配平** ⇒ 推翻"autorelease 泄漏"假说，
   v57/v59 两次失败的修复瞄错了对象。

**当前硬阻塞**：渲染线程 91% 阻塞导致的低帧率，使任何"进游戏停留数分钟"的测量无法进行。
它同时是本会话后半段大量误判的根源（性能方差被反复误归因给刚做的改动）。

**建议的下一步顺序**：先解 43 节的同步停顿（可测、可验证，且不需要长时间观察），
再回到 39.2 的整帧地面真相转储（代码已就位：`RYUJINX_METAL_FULLDUMP=1`，v147/v148）。

### 43.1 ⚠️ 对 43 节的自我更正：渲染线程阻塞很可能是正常的

`GUI.RenderThread` 在 Ryujinx 里是**呈现**线程，它本来就靠等待信号量取下一帧。
**它阻塞 91% 通常只说明客户机没能及时产出帧**，而不是它自己被卡住。

且 `CheckProgramLink(true)`（唯一的阻塞式着色器等待）只在
`ParallelDiskCacheLoader` 里调用，**不在渲染路径上**。

⇒ 43 节"渲染线程被阻塞是低帧率的原因"**倒果为因**。真正的瓶颈在上游
（客户机模拟 / GPU 线程），本节的取证不足以定位它。

**给接手者**：要定位这个停顿，应当采样 **GPU 线程**（处理 GPFIFO 的那条）
与客户机线程，而不是呈现线程；或者直接用 `Metal System Trace`
（第 6 节有可用范例）看 GPU 侧的间隔。

### 39.6 启动期帧率不可归因（四次尝试全错）

对"某些运行 0.3–1.7 FPS、另一些 17–30 FPS"，本会话先后归因给：

1. per-draw 探针 → 移除后仍慢；
2. 整帧转储开关 → 关闭后仍慢；
3. 转储环形缓冲大小（66 MB → 16.6 MB）→ 无改善；
4. 该缓冲的分配时机（改为按需分配）→ 仍 0.71 FPS。

**四次归因全错。** 同一二进制、同样的空闲机器，帧率在 0.3 到 30 之间跳。
⇒ **启动期帧率由本会话无法控制的因素主导，不可用于归因。**

**唯一可用的做法**：每次测量前先读一次基线 FPS，低于 15 就重启进程重来，
不要试图解释为什么低，更不要据此推断刚做的改动有害。

## 45. 自动化失败的真正原因：焦点根本没拿到

今晚数十次"存档没载入"的根因不是按键时机，也不是存档行号：

```text
osascript set frontmost → 静默成功
frontmost 回读          → false
当前前台应用            → Claude（驱动本会话的应用自身）
System Events 取窗口    → “无效的索引” (-1719)
```

**所有按键都发给了前台应用，从未到达模拟器。**
Ryujinx 是**裸二进制、没有 .app bundle**，System Events 连它的窗口都拿不到，
`set frontmost` 因此静默失败——而这正是 [[ryujinx-focus-must-use-pid]] 里
早就写过"必须回读 frontmost 验证"的原因，我这一整晚都没照做。

尝试过的替代方案：

- `perform action "AXRaise" of window 1` → 拿不到窗口，失败；
- 连续三次 `set frontmost` + 回读 → 始终是 Claude；
- **`CGEventPostToPid`**（新增 `tools/send_key_to_pid.py`，绕开焦点直接投递给进程）
  → 事件送达，但当时模拟器 0 FPS、无法轮询输入，菜单未推进；该方案本身仍值得保留复用。

⇒ **当代理自身占据前台时，基于 `set frontmost` 的按键自动化不可用。**
要么由人手动进入存档（今晚用户就是这样做的），要么全程使用 `CGEventPostToPid`
且先确认基线帧率足够（第 39.6 节）。

### 45.1 `CGEventPostToPid` 也无法驱动菜单

绕开焦点直接向进程投递按键（`tools/send_key_to_pid.py`）同样没能推进标题菜单：
基线 12 FPS、事件已发出，但存档始终未载入。Avalonia/SDL 的输入路径显然仍依赖
真实的键盘焦点，合成事件在非前台状态下不被接受。

⇒ **在代理自身占据前台的环境里，无法用任何自动化手段把游戏开进存档。**
这不是判断问题，是硬约束。

**唯一可行的分工**：由人把游戏开到目标场景（阳光下的第一个存档，站着不动），
代理在旁边负责测量与转储（`RYUJINX_METAL_FULLDUMP=1`，v150）。
今晚用户手动进存档的那一轮，正是本会话唯一取得游戏内数据的方式之一。

---

## 46. 白闪是**日照门控**的：白天 11.2%，夜里 0.006%（2026-08-03）

分工按 45.1 执行：用户手动进第一个存档、走到火堆边坐到夜晚，代理全程被动读数
（v150，`RYUJINX_METAL_FRAME_PROBE=1`，interval=600）。**单会话、同一存档、同一地点**，
所以没有跨进程漂移，也没有换存档换地点的干扰。

### 46.1 结果

```
DAY    frames= 7800  flat= 877   rate=11.244%     (f=1200..8400)
NIGHT  frames=18000  flat=   1   rate= 0.006%     (f=9000..27000)

比值 2024x。若白天的发生率在夜里成立，应有 2024 个 flat 帧，实测 1 个。
```

逐区间：白天 4.83→23.17% 波动上行；`f=8400→9000` 一步跌到 **0.00%**，
随后 **连续 23 个区间、13800 帧全部 0.00%**，直到 f=25800 才出现单独 1 帧。

### 46.2 为什么这不是仪器死了

`flat=0` 且**恰好为零**是仪器故障的典型形状，按第 5 条规矩先查仪器。查证结果是活的：

```
DAY    fullresSrc[41] n=1200 uni=288      targets=94
NIGHT  fullresSrc[41] n=1200 uni=  0      targets=53
       classified=600（两边相同）
```

采样次数 `n=1200`（600 帧 × 2 个 patch）**完全没变**，只是均匀判定结果为 0。
而且 FrameProbe 判的是"两块相距很远的 16×16 是否同为一个值"，**不看亮度**——
夜间的暗常数它照样会判为 flat。这正是用户提出该测试时点明的关键：
外部检测器按 `--white-luma 245` 判白，暗常数会被它整个漏掉，FrameProbe 不会。

### 46.3 已排除的替代解释

- **不是"目标越多越闪"**：`f=1200` 有 205 个目标而 flat 仅 4.83%（全天最低），
  `f=8400` 只有 94 个目标却是 23.17%（最高）。目标数与 flat 率不构成连续关系；
  白天档（76–94 目标）与夜间档（53–62 目标）是**模式切换**，不是梯度。
- **不是"白天跑、夜里不跑的那两个着色器程序"**：`cfcd14167efd4906` 与
  `d7672a04a1000a87` 在整局里各只出现 3 次和 5 次 draw，是噪声。**此条已作废**，
  不要再据此推进（我一度写成了候选集，随即被计数推翻）。

### 46.4 尚未拆除的混淆：**变暗 vs 停止移动**

用户是"走到火堆边坐下"完成日夜切换的，因此夜间样本同时满足"暗"和"不动"。
从常量缓冲里读相机矩阵（`b6` 行的平移分量）可证实：

```
f= 8400  w=1027     f=9000  w=1096     f=9600..27000  w=1057（一步未动）
```

⇒ 整个夜间窗口相机锁死在 1057，**混淆完整存在**。

一条**偏向"与移动无关"**的证据：`f=9000` 区间内相机从 1027 移到 1096（玩家在走动），
而该区间 flat 已经是 0.00%。但只有单个采样点，按第 5 条规矩**不能当结论**。

**拆除方法（一分钟，必须做）**：夜里跑动 + 转视角两个区间。
- 仍为 0 ⇒ 亮度门控成立，嫌疑锁定到只在强光下运行的那几级
  （光轴/体积散射、bloom、镜头光晕、自动曝光适应）；
- 弹回 10%+ ⇒ 与亮度无关，是移动/流式加载，46.1 的解释需推翻重写。

## 47. FULLDUMP 从来不可能触发——环大小与回读延迟的死结

`RYUJINX_METAL_FULLDUMP=1` 自 v147 起就"代码已就位却从未落盘"。根因：

```csharp
private const int FullSlots    = 2;   // 环只有 2 槽
private const int ReadbackDelay = 6;  // 分类滞后 6 帧
```

槽号 `frame % FullSlots`。因为 6 是偶数，被分类的第 N−6 帧与当前第 N 帧**永远同槽**，
`DumpFull` 里的 `_fullFrame[slot] != frame` 恒真，直接 return。**转储在任何情况下都不会发生。**

`FullSlots` 由 8 改为 2，正是此前为压缩 66 MB→16.6 MB 所做——而那次改动属于本文档
第 39 节记录的**四次错误归因开机帧率**之一。**一个瞄错目标的性能"优化"，把仪器本身关掉了，
并且沉默了三个版本。**

### 47.1 v151/v152 的修复

1. `FullSlots = 8`（必须 > `ReadbackDelay`），并把这条约束写进注释；
2. **删除亮度掩码**。原判据 `(flatValue | 0x101) == 0xFFFDFFFF` 只认白色，
   夜间的暗常数会被整个滤掉——正是第 1 条规矩说的"每加一个过滤就挡掉要找的东西"。
   现在只按均匀性判，与亮度无关。顺带：该掩码连白色都只认一半，
   `(253,254,254)` 能过、`(253,253,254)` 过不了；
3. 改为**最多 4 次、值互不相同**的转储，值写入文件名，避免一次正常的淡入淡出黑屏
   把唯一机会用掉。

### 47.2 工作区曾处于无法编译的状态

FrameProbe.cs 引用了 13 个已被回滚掉的成员（`Texture.ResolveViewRoot` /
`ViewRootPtr`、`Pipeline.CurrentCbs` / `EndCurrentPassForProbe`、`MirrorAb.Level`、
`DrawDiagnostics.Present*` 共 28 个编译错误）。v150 二进制与磁盘源码**不一致**。
已补齐：`ViewRootPtr`/`ResolveViewRoot` 用一张 view→root 的映射表实现且
**以 `FrameProbe.Enabled` 门控**（探针不得改变被测对象）；其余为薄封装。

### 47.3 v152 新增目标普查

`targets=94 vs 53` 说明白天多出约 40 个渲染目标，但**报告里的 per-target 明细被
`stats.Uniform == 0 → continue` 过滤掉了**——从未均匀过的目标一个都不打印，
而白天多出来的那批大概率正属于此。v152 增加不带过滤的 `census:` 段
（按 `宽x高:格式*个数` 汇总全部目标），日夜两份 census 相减即为候选 pass 清单。
