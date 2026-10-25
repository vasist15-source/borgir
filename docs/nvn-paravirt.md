# 方案 C：NVN 层半虚拟化（工作文档）

目标：跳过 Maxwell 硬件模拟这一层。游戏调用的 NVN 函数由我们注入到游戏进程里的代码实现，命令录成紧凑记录，
提交时由宿主直接回放成 Metal 命令。参照 MacNativeGames（MIT）的做法，但适配我们的虚拟机架构：游戏跑在
Apple Hypervisor 里，宿主调用要陷出（每次约 1–2 µs），而游戏每帧调用 NVN 两万八千次以上，所以录制必须
留在游戏侧完成，只有提交、等待、出图、建对象等低频操作才陷出。

## 分层

1. **游戏侧 NVN 层**（`src/Ryujinx.HLE/HOS/Kernel/Process/NvnPv/Guest/*.c`）
   - clang 交叉编译成 AArch64 ELF 目标文件（`-ffreestanding -fno-pic -ffixed-x18`），作为资源嵌入 Ryujinx.HLE。
   - 进程启动时由 `GuestObject` 链接到 nnSdk 附近的空闲地址：代码块（RX，前面一页宿主写的桩）+ 数据块（RW）。
   - `nvnBootstrapLoader` 第一条指令改成跳到 `pv_bootstrap`；游戏后续所有 NVN 函数都从这里取。
   - 宿主调用：`svc #0xF8`，x8 = 操作号，x0–x3 参数，x0 返回。
2. **宿主侧**（`NvnPvHost` + 回放）
   - 读录制（游戏内存里的记录链）、建 Metal 资源、回放、出图、同步。
   - 第一版通过 GAL（现有 Metal 后端）出画面，保证正确性；之后把通道结构优化（前瞻、合并通道、
     存储/加载动作、显式屏障）放进 Metal 后端，那是 GPU 侧收益的主要来源。

## 已确认的游戏事实（TOTK 1.4.2，main 文本段 0x8506000）

- 游戏有两类内联快路径，都先读全局开关 `*(*(0xbe52f28))`，为 0 走函数调用：
  - 595 处读 NVNcommandBuffer `+0x00/+0x08`（命令内存写指针 / 末尾），需要 0x18 字节；
  - 47 处读 `+0x20/+0x28`（控制内存写指针 / 末尾），需要 0x30 字节（贴图句柄等延迟绑定）。
  - 两处都会写 `+0x10` 的脏标志字节。
  - 我们的 cmd_t 在两对位置都放哨兵（cur = ~0，end = 0），让快路径永远判定空间不够，改走 NVN 函数。
- NVN 驱动常量段布局（来自 MacNativeGames 的翻译服务，实测）：
  - c2：BindTexture 的组合句柄在 `0x20 + 8·槽`（32 槽）；分离贴图 `0x168 + 8·槽`；分离采样器 `0x568 + 8·槽`；图像 `0x120 + 8·槽`。
  - c0：存储缓冲表，顶点 `0x110 + 0x10·槽`，片元 `0x510 + 0x10·槽`，计算 `0x310 + 0x10·槽`；计算线程组数在 `0x200`，计算的 UBO 表 `0x210 + 0x10·槽`。
  - NVN uniform 槽 n 对应 Maxwell 常量缓冲 `c(n+3)`；c1（BRX 跳转表等常数）在代码区尾部 `[data + ctl[0x700], data + ctl[0x704])`。
- 句柄编码：组合句柄 = 贴图号（低 20 位）| 采样器号 << 20。
- 深度模式：游戏调 `nvnDeviceSetDepthMode(NEAR_IS_MINUS_W)`，翻译时要做 z = (z + w) / 2。

## 里程碑

- [x] S1 注入框架：编译、链接、挂钩、宿主调用；全部函数透传真驱动，验证游戏照常进世界（v566，2026-10-22）。
- [x] S2 游戏侧完整 NVN 层（486 个入口）+ 宿主空回放（只做 CPU 可见副作用）：v568 开机→标题→读档进世界，
      0 个未实现调用、0 个坏句柄，世界里 49–50 presents/s（游戏自身 50 帧上限）。每帧 NVN 级：4073 绘制、
      190 SetRenderTargets、174 清除、155 Barrier、22 Dispatch、223 UpdateUniformBuffer、161 ReportCounter、138 条件渲染。
      着色器包布局（MacNativeGames totk_shader_rt.m）：代码区从 data 起，图形阶段跳过 0x30 字节头；控制段 +0x700 = c1 起点、
      +0x704 = 代码区总长（c1 在尾部）、+0x714 = NVN 阶段、+0x748 = 计算线程组大小。
- [ ] S3 宿主回放（GAL，`src/Ryujinx.Graphics.Gpu/Nvn/`）：贴图 / 缓冲 / 采样器 / 着色器 / 管线状态 / 绘制 / 出图 / 同步。
      v569（2026-10-22）第一次运行就读档进世界并出完整画面（雨、草、角色、HUD、小地图）。回放跑在 GPU 线程
      （GPFifoDevice.PushHostAction），复用进程的贴图/缓冲缓存和着色器翻译器；GPU 地址 = 客户机地址，
      虚拟池按 64 KiB 页映射到物理池页。v569 暴露并在 v570 修掉的问题：
      - NVN 的 *_I2F/_UI2F 顶点格式 = GAL Sscaled/Uscaled，Metal 无缩放格式：描述符要改成 Sint/Uint
        （翻译器按 Sscaled 声明整型输入再转浮点），否则 Metal 拒绝建管线（v569 九万条报错、对应绘制全丢）。
      - GAL `Format.R8Unorm` 的枚举值是 0，"格式 != 0"把 R8 当成无效格式；改用显式未知哨兵。
      - 缓冲拷贝/清除必须走 BufferCache.CopyBuffer/ClearBuffer（同 DMA 引擎）：游戏用 GPU 拷贝把着色器代码搬进
        CPU 不可访问的池，直接拷宿主缓冲会让客户机内存保持全零，翻译出"全零指令"的计算着色器。
      - 计算着色器共享内存大小 = 控制段 +0x754（字节；TOTK 31 个内核里恰好 6 个用共享内存的非零：0x80/0x180/0x380）。
        局部内存字段尚未定位（样本里没有用局部内存的计算着色器），沿用翻译器默认 128 B。
      - 渲染目标的宿主纹理可能被缓存换掉，每次绘制前比对 HostTexture（同 TextureManager）。
      - WriteTexels 有 3D 贴图（开机时的调色 LUT），块线性 3D 偏移按 BlockLinearLayout 公式自算。
      视口：TOTK 用左上原点（窗口原点模式 1）+ GL 深度（深度模式 0）；GAL 视口是 GL 约定（Metal 后端翻转），
      所以传负高度视口并对调正面朝向，等价于 Maxwell 路径最终给 Metal 的值。
      v571–v585 后续：
      - 性能：着色器按程序序号缓存、属性类型查表（v571）；按绑定去重——Metal 后端每次 set 都标脏并重建参数缓冲（v573）；
        贴图按"池代数 + 句柄"缓存、UBO/顶点流按地址缓存（v575，客户端注册贴图/采样器时 pv_ctl.pool_gen 加 1）；
        提交记录用数组池、逐绘制遍历改下标（v581，消除 LOH 与枚举器分配）。
      - 3D 贴图逐层渲染（v580）：游戏用"只选一层"的视图渲染体积贴图（32³ 噪声、8³ R11G11B10 调色表），之前每层都落在深度 1 的同一贴图上，
        读档地图/存档背景发紫。改为缓存里取整个 3D 贴图，Metal 视图记 depthPlane 写进渲染通道附件。原 Maxwell 路径不读 RtColorState.BaseLayer。
      - 客户端崩溃（v582）：控制内存重叠 8 字节时旧标记的 first 被新标记覆盖；释放范围扩到与区间重叠的标记 + mark_live 校验 + 登记去重。
      - 着色器程序磁盘缓存（v583）：games/<tid>/cache/nvnpv/programs.bin，键 = 代码哈希 + 状态键 + 翻译答案，版本 = CodeGenVersion；热启动载入约 600 个。
      - 虚拟池里的着色器代码要按 GPU 地址读（v584），否则读成全零。
      - TEX_FINAL 释放池引用（v585，每次提交前批处理；逐次推任务会堆积成 53 GB 泄漏）。
      - 仪器：RYUJINX_NVN_PV_TRACE=1 / _TRACE_RT=<地址> / _DUMP=<目录> / _SELFCHECK=1；tools/frame_pacing.py（配 RYUJINX_METAL_FRAME_LINE=1）。
- [ ] S4 世界场景正确运行，和现有路径对比画面与帧率。
      2026-10-22 夜（同存档、同场景、着色器编完）：原路径 37–42 FPS（帧中位数 23–26 ms）；回放约 40–44 FPS（中位数 22–24 ms），
      每帧 320–390 pass、约 3850 绘制，GPU 忙 70–77%，回放线程 11–14 ms/帧。稳定性数据被当晚机器状态污染（交换 47 GB、磁盘 99%），待重启后重测。
- [ ] S5 Metal 后端的通道结构优化（前瞻、合并、存储动作、显式屏障）。
      起点（v585，世界稳定段每帧）：323 pass；结束原因 RAW 依赖拆分≈140、换渲染目标≈130、提交冲刷 23–38、计算 12、拷贝 5；
      "同目标重开"≈167（拆开后在同一组附件上再开 pass，每次重新加载附件）。MacNativeGames 同场景 49 pass。
      回放的优势：执行前已持有整次提交的全部记录，可以前瞻——知道每次绘制采样哪些贴图、写哪些附件、之后谁还读——
      据此决定拆分、快照（只拷被读区域）、或推迟/提前（清屏提前、存储动作按"之后是否还读"决定）。
      顺序：① 提交边界的 Strict 同步改为非强制（栅栏随下一次自然冲刷完成），先量 Flush 类减少多少；
      ② 用前瞻把"同目标重开"中不必要的拆分去掉（游戏只在需要 RAW 时发 Barrier，NVN 屏障位可直接作为依据）；
      ③ 存储/加载动作按前瞻决定（附件之后不再被读、且下一次先清屏 → Store=DontCare / Load=Clear）。

## 许可

`Guest/nvn.h` 由 h1k421 根据 DWARF 重建（MIT）；`nvn_protos.h`、`names.txt` 和若干语义参考来自
MacNativeGames / D2R Mobile 的 NVN 层（MIT），贴图布局算法源自 deko3d（zlib）。
