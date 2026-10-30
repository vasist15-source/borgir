# 本地使用指南（Ryujinx_Optime · 原生 Metal 后端）

本分支在 Ryubing 版 Ryujinx 1.3.3 的基础上加了一个**原生 Metal 渲染后端**，主要针对《塞尔达传说：王国之泪》1.4.2 在 Apple Silicon Mac 上调优。Metal 后端仍是实验性的，已知问题见 [修复说明](METAL_FIXES.md) 末尾。

测试环境：Apple M1 Max，macOS 26.5。其他 Apple Silicon 机型应能运行，但没有测过。Intel Mac 不支持 Metal 后端，只能用 Vulkan。

## 1. 编译

需要 [.NET SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 10.0.301 或更高版本（见仓库根目录的 `global.json`）。

```bash
git clone git@github.com:sunsssyc/Ryujinx_Optime.git
cd Ryujinx_Optime
git checkout codex/native-metal-backend
```

发布一个自带运行时的 arm64 版本。版本号参数不要省略：程序集版本必须是 `1.3.3.0`，否则主界面能开、一加载游戏就闪退。

```bash
dotnet publish src/Ryujinx/Ryujinx.csproj -c Release -r osx-arm64 --self-contained true \
  -p:Version=1.3.3+local-metal -p:AssemblyVersion=1.3.3.0 -p:FileVersion=1.3.3.0 \
  -p:InformationalVersion=1.3.3+local-metal \
  -o artifacts/terminal/Ryujinx-metal-local
```

然后用仓库自带的 entitlements 做 ad-hoc 签名。缺了这一步，Hypervisor 和 JIT 权限不生效，游戏无法启动。

```bash
codesign --entitlements distribution/macos/entitlements.xml -f -s - artifacts/terminal/Ryujinx-metal-local/Ryujinx
```

`artifacts/` 已在 `.gitignore` 里，编译产物不会被提交。

## 2. 启动

从终端启动编译好的目录。ad-hoc 签名的程序不保证能从 Finder 双击打开，终端启动是验证过的方式。

```bash
cd artifacts/terminal/Ryujinx-metal-local
./Ryujinx
```

第一次打开后，在 **设置 → 图形 → 图形后端** 里选 **Metal (Experimental)**。也可以直接在命令行指定后端和游戏文件：

```bash
./Ryujinx --graphics-backend Metal "<游戏文件路径>"
```

- **数据目录**：`~/Library/Application Support/Ryujinx`，配置文件 `Config.json`、存档、Mod、着色器缓存都在这里，所有编译版本共用。换版本不会丢存档和设置。
- **密钥与固件**：首次使用必须安装，见下一节。
- **只开一个实例**：两个 Ryujinx 同时运行会互相覆盖着色器缓存和配置。
- **首次进游戏**会编译着色器，前几分钟卡顿是正常的；缓存建好后就不会了。

## 3. 密钥与固件（首次必做）

模拟器本身不含任何任天堂的系统文件。**没有密钥，游戏无法解密，列表里看不到游戏或提示缺少 `prod.keys`；没有固件，游戏启动时报错。** 两者都只能从你自己的 Switch 导出，本仓库不提供，也不要从网上下载别人的。

### 从主机导出

需要一台能运行自制软件的 Switch。

- **密钥**：用 Lockpick_RCM 导出，得到 `prod.keys`，通常还有 `title.keys`。
- **固件**：用自制工具把主机的系统固件导出成文件夹或 ZIP。

具体步骤各工具的说明里都有，也可以参考 [Ryubing Wiki](https://git.ryujinx.app/projects/Ryubing/wiki/Home) 的安装指南。

**两者的版本要对得上**：密钥必须来自固件版本不低于所装固件的主机。固件比密钥新时会解不开，报缺少某个密钥。最稳妥的做法是在同一台主机上同时导出两者。

### 装进模拟器

打开模拟器主界面，在菜单栏的 **操作（Actions）** 里：

1. **安装密匙（Install Keys）**：选 `KEYS...` 指定 `prod.keys` 文件，或选 `文件夹...` 指定放密钥的目录。
2. **安装系统固件（Install Firmware）**：选 `XCI 或 ZIP...` 指定固件压缩包，或选 `文件夹...` 指定解压后的固件目录。

装好后主界面底部状态栏会显示固件版本，游戏列表也能正常读出游戏。

也可以手动放密钥：把 `prod.keys` 和 `title.keys` 复制到下面这个目录，重启模拟器即可。

```
~/Library/Application Support/Ryujinx/system/
```

所有编译版本共用这个数据目录，所以只需要装一次，以后换新版本不用重装。主机更新了系统之后，想用新固件就把密钥和固件一起重新导出、重新安装。

## 4. 王国之泪的推荐设置

- **帧率上限设 50**。UltraCam Mod 的帧率上限在 `sdcard/UltraCam/TOTK/Config/maxlastbreath.ini`（位于数据目录下），设为 60 时游戏自身的计时会出问题，摔死重生时有几率崩溃。50 已长时间验证无问题。

```ini
[UltraCamFPS]
DynamicFPS = True
MaxFPS = 50.00
```

- 图形设置的其他项（分辨率缩放、各向异性过滤等）按个人喜好，和本分支的修复无关。

## 5. 调试开关

所有修复默认开启，**正常游玩不需要设置任何东西**。下面的开关用于对照排查：怀疑某个画面问题和某项修复有关时，切到另一档看问题是否跟着出现或消失。

### 环境变量（启动前设置）

```bash
RYUJINX_METAL_BARRIER_SCOPE=all ./Ryujinx
```

| 变量 | 默认 | 可选值与作用 |
|---|---|---|
| `RYUJINX_METAL_BARRIER_SCOPE` | `deferred` | `all`：每个游戏纹理屏障都结束渲染通道，最保守、帧率略低；`hazard`：已撤回的旧策略，会复现雪山平台单帧消失，仅供诊断 |
| `RYUJINX_METAL_ASYNC_PSO` | 开 | `0`：管线状态改回在渲染线程同步构建（转视角更卡，但新材质不会晚一帧出现） |
| `RYUJINX_METAL_ASYNC_PSO_WAIT_MS` | `4` | 每帧最多等待后台构建的毫秒数，`0` 为不等直接跳过 |
| `RYUJINX_METAL_COUNTER_IN_PASS` | 开 | `0`：遮挡查询回到"每次报告结束渲染通道"（实测两者帧率无差别） |
| `RYUJINX_METAL_ASYNC_PSO_FULLSCREEN_SYNC` | 开 | `0`：全屏绘制（3–6 顶点的后处理）也允许跳过；默认它们原地构建，避免整帧出错 |
| `RYUJINX_METAL_ASYNC_PSO_BURST` | `64` | 一帧排队构建超过该数后剩余的原地构建（读档爆发）；`0` 关闭 |
| `RYUJINX_METAL_PREWARM` | 开 | `0`：不在着色器缓存加载后预先构建管线（第二次启动进关卡时又会边画边建） |
| `RYUJINX_METAL_DEFER_COMPILE` | 开 | `0`：加载着色器缓存时就编译全部 MSL（系统编译缓存冷时启动会慢几秒到几十秒） |
| `RYUJINX_METAL_MTL4_PIPELINES` | 关 | `1`：试验中的 Metal 4 管线。每个程序按顶点布局只完整编译一个"底座"（颜色附件的格式、混合、写掩码留空，深度格式 Metal 4 本来就不写进管线），其余状态由底座特化，一个约 10–30 微秒；底座存进游戏着色器缓存目录下的 `metal4/` 归档，下次启动直接取用。第一次开启会从头编译全部底座（旷野之息 3,885 个，预热线程后台约 45 秒，期间绘制照旧走经典构建），归档约 128 MB |
| `RYUJINX_METAL_MTL4_ARCHIVE` | 开 | `0`：Metal 4 底座只留在内存，不读写 `metal4/` 归档 |
| `RYUJINX_METAL_SKIP_SELF_SPLIT` | 开 | `0`：关闭"自读放行"，更保守但慢很多 |
| `RYUJINX_METAL_FB_FETCH` | 开 | `0`：关闭 framebuffer fetch（R32F 深度自读改走拆通道） |
| `RYUJINX_METAL_BUFFER_MIRRORS` | 开 | `0`：关闭缓冲镜像写入 |
| `RYUJINX_METAL_STATE_CACHE` | `3` | `0`–`3`：绑定状态缓存等级，`0` 为不缓存 |
| `RYUJINX_METAL_PREPASS_REBIND` | `1` | `2`：只记录不修复（复现阳光单帧跳变） |
| `RYUJINX_POOL_DESC_CHECK` | `1` | `0`：复现地底地图单帧消失 |
| `RYUJINX_GPU_PRESENT_SIBLING` | 关 | `1`：恢复旧的输出路径（画面整体变暗） |
| `RYUJINX_MSL_FETCH_OFFSET` | 开 | `0`：复现地底青色硬边雾片 |
| `RYUJINX_METAL_LIVE_STATS` | 关 | `1`：每 600 帧在日志里记录各类 Metal 对象的存活数和已释放仍未回收的数量，查内存上涨用 |

### 热切换文件（游戏运行中即时生效）

写入一个文件，游戏每帧读一次，不用重启。删掉文件就回到默认值。

```bash
echo all > /tmp/ryujinx-metal-barrier-scope
```

```bash
rm /tmp/ryujinx-metal-barrier-scope
```

| 文件 | 内容 |
|---|---|
| `/tmp/ryujinx-metal-barrier-scope` | `deferred` / `all` / `hazard` |
| `/tmp/ryujinx-metal-async-pso` | `1` 开 / `0` 关 |
| `/tmp/ryujinx-metal-counter-in-pass` | `1` 开 / `0` 关（遮挡查询是否在通道内切换） |
| `/tmp/ryujinx-gpu-sync-every-draws` | 每 N 次绘制额外建一个同步点，`0`/删除为关（诊断用，实测无收益） |
| `/tmp/ryujinx-metal-skip-self-split` | `1` 开 / `0` 关 |
| `/tmp/ryujinx-metal-fb-fetch` | `1` 开 / `0` 关 |
| `/tmp/ryujinx-metal-state-cache` | `0`–`3` |

**游玩中只往更保守的方向切**（比如 `deferred` 切到 `all`）。游玩中关掉正确性机制（比如关闭通道拆分）曾导致游戏读到非法内存直接崩溃，要做那类对照先存档。

## 6. 反馈问题时带上什么

- **日志**：`~/Library/Logs/Ryujinx/`，保留最近三次运行，文件名带版本号和时间。
- **闪退报告**：`~/Library/Logs/DiagnosticReports/Ryujinx-*.ips`。
- **画面问题**：截图或录屏，加上所在位置（小地图坐标）和时间。只闪一帧的问题录屏最有用。
- **游戏自身中止**（日志里有 `The guest program broke execution!`）：本分支会额外打印原因，搜下面三条：

```bash
grep -a 'condvar wait failed\|mutex waiters with InvalidState\|Break reason=' ~/Library/Logs/Ryujinx/*.log
```

日志里每隔 120 帧有一行 `Metal sync stats`，其中 `barrier=` 显示当前屏障模式，`per frame: N passes` 是每帧渲染通道数，是判断性能的主要指标。

## 7. 分支与远端

| 名称 | 用途 |
|---|---|
| `codex/native-metal-backend` | 主分支，所有修复都在这里 |
| `probes/2026-09-diagnostics` | 排查期间的诊断探针，默认全关，未整体编译验证过 |
| `upstream` 远端 | 官方 Ryubing 仓库 `https://git.ryujinx.app/ryubing/ryujinx.git` |
| `origin` 远端 | 本仓库 |
