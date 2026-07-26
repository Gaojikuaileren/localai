# DECISIONS — 决策日志

> 格式定义: 方案书 v2.1 §12.1
> 范围: **v2.1 定稿后、执行期产生的决定。** v2.1 定稿时已有的决议(含 Q1–Q5)见方案书 §16,不在此重复。
> 「推翻条件」是本文件最重要的字段 —— 半年后你需要知道的不是当时决定了什么,而是这个决定现在还成不成立。

---

## 2026-07-26 · HF 缓存按「冗余回收」处理,而非按方案书原文迁移

**背景**
v1、v2.0、v2.1 的 P0 都写着「HF 缓存自 C: 迁至 `D:\AI\cache\hf`(15.7GB)」。
审计(`AUDIT_v2.0.md` F1)指出这个 15.7GB 口径存疑 —— 同一个数字在 v1 里既被称作「已有模型资产」又被称作「HF 缓存」。

**实测结果**
`C:\Users\<user>\.cache\huggingface` = 15.71GB,内容为 Juggernaut-XL-v9 / InstantID /
controlnet-union / controlnet-openpose / inswapper。**两处描述指的是同一批数据** ——
那批模型就是通过 HF hub 下载的,躺在缓存目录里,所以它既是模型资产也是 HF 缓存。

进一步 SHA256 逐文件比对:全部 6 个 >100MB 的 blob 在 `D:\ComfyUI\models` 下
都有哈希完全一致的副本(重复 6 / 无对应 0),且 HF blob 文件名与实算哈希吻合(缓存未损坏)。
**属完整冗余。**

**选项**
- A 照方案书迁到 `D:\AI\cache\hf` —— 只是把 15.7GB 冗余从 C: 搬到 D:
- B 只改 `HF_HOME`,旧的原地留着 —— 零风险但不释放空间
- C 设 `HF_HOME` 指向新位置 + 回收旧的冗余副本

**决定**
选 C。先移入隔离区(`D:\AI\state\quarantine\2026-07-26_hf-cache-from-C`),
随后依你 2026-07-26 的明确要求提前清除,保留 `MANIFEST.md` 作为审计痕迹。

**理由**
迁移一份已被哈希证明重复的数据没有意义。ComfyUI 走 models 目录的直接文件路径,
不经 HF hub,因此不受影响;真需要时 HF hub 会按需重新下载。

**代价与已知风险**
若将来有组件通过 `from_pretrained("RunDiffusion/Juggernaut-XL-v9")` 引用,会触发一次重新下载
(6.62GB,仅 WiFi 866Mbps)。可接受 —— 也可从 ComfyUI 侧的同哈希副本取回。

**推翻条件**
若发现有组件强依赖 HF hub 的缓存布局且重下代价不可接受,则应改为在 `MODELS` 根下
保留一份规范化副本,并在模型注册表登记「重获成本」。

**净效果**:C: 387.7 → 403 GB;D: 无净变化;ComfyUI 完全未动。

---

## 2026-07-26 · P0 的「迁移 HF 缓存」任务作废,方案书需修订

**背景**
上一条决定使 v2.1 P0-2 的原文(「HF 缓存自 C: 迁至 `D:\AI\cache\hf`(大小以上一项实测为准)」)
在动作层面失效 —— 正确动作是回收而非迁移。

**决定**
P0-2 实际执行为「确认冗余后回收 + 设 `HF_HOME` 指向空的新位置」。
方案书 v2.1 §14 P0 与 §2.1 需据此修订(下一版一并处理)。

**同时需修订的还有** `AUDIT_v2.0.md` 的 F1:
审计当时推测「那批模型早已在 D:,v1 记录有误」—— **这个推测是错的**。
真相是 v1 两处描述指同一批数据,v1 没记错,只是没人意识到它们是一回事;
而 D:\ComfyUI 下的是**另一份独立副本**。

**推翻条件**
不适用(事实修正)。

---

## 2026-07-26 · 备份改为「手动触发 + 脚本保证一致性」

**背景**
实测发现 D: 与 E: 是同一块物理盘(Disk 1),即代码根与数据根同盘,该盘故障两者同时丢失。
当时无任何外置介质,一度决定「P0 只交付脚本与策略,首次备份推迟到 P3 前」。

**新信息**
你于 2026-07-26 告知:有移动固态,会手动备份。

**决定**
备份模式定为**手动触发**:你决定什么时候插盘运行,
脚本(`90-ops/backup/backup.ps1`)负责一致性 —— 排除规则、清单、SHA256 校验、报告、同盘拒绝。
`paths.toml` 的 `[backup].status` 由 `not_provisioned` 改为 `manual`。

**理由**
手动的应当是**时机**,不该是**过程**。人工复制粘贴容易漏目录、漏校验、备到同一块盘上。
脚本把这几件事固定下来,不增加你的负担。

**代价与已知风险**
手动触发意味着**备份频率取决于你是否记得**。记忆库(P3 起)是每天都在变的数据,
与模型权重不同 —— 一周不备就可能丢一周的记忆。这一点在 P3 上线时需要重新评估。

**仍然成立的两条**
1. BitLocker 恢复密钥离线保管,至少两份,不与被加密数据同盘 —— 密钥丢失 = 记忆库永久不可读
2. 「没演练过的备份不算备份」:P3 上线前须跑通一次完整恢复

**推翻条件**
P3 上线后若出现「想不起上次备份是什么时候」的情况,应改为计划任务自动触发 + 插盘检测。

---

## 2026-07-26 · 关闭 NVIDIA App / ShadowPlay 常驻

**背景**
CUDA 默认安装附带了 NVIDIA App · ShadowPlay · FrameView · Telemetry 等,常驻 3 个
`nvcontainer` 进程。你于 2026-07-26 指示关闭。

**决定**
停止并禁用 `NvContainerLocalSystem`(NVIDIA App / ShadowPlay 的容器),
同时禁用 `NVIDIA App SelfUpdate` 计划任务(否则自动更新会把服务重新拉起)。
**保留 `NVDisplay.ContainerLocalSystem`** —— 那是驱动核心,负责控制面板与显示配置。

**回归验证**
`nvidia-smi` 正常 · torch `available=True` · capability `(12,0)` ·
之前编译的 sm_120 kernel 重跑通过。驱动功能零影响。

**⚠ 实测修正了一个我此前给出的错误暗示**

```
关闭前: nvcontainer 3 进程 · 188.8 MB 内存 · 显存 737 MiB
关闭后: nvcontainer 0 进程 ·   0.0 MB 内存 · 显存 737 MiB   ← 未变
```

我此前说「对显存吃紧的项目,ShadowPlay 与 NVIDIA App 值得关」——**它们占系统内存,
不占显存**。实测占显存的全是 GUI 程序(dwm.exe / explorer / SearchHost / LGHUB /
EdgeWebView,乃至 AMD 自己的 RadeonSoftware),因为**显示器接在独显上,所有 GUI 渲染都落在独显**。

**因此本条决策的净收益是 188.8 MB 系统内存 + 少 3 个进程,显存收益为零。**
同时这强化了 P0-4(改接核显)的理由:那是唯一能真正腾出显存的手段。

**代价**
ShadowPlay(即时重放 / 游戏录制)与游戏内覆盖不可用;NVIDIA App 打不开。
**驱动设置仍可通过 NVIDIA 控制面板调整。**

**推翻条件**
若你要用 ShadowPlay 录游戏:

```powershell
Set-Service -Name NvContainerLocalSystem -StartupType Automatic
Start-Service -Name NvContainerLocalSystem
Enable-ScheduledTask -TaskName "NVIDIA App SelfUpdate_*"
```

---

## 2026-07-26 · 备份校验清单改为 GNU sha256sum 兼容格式

**背景**
首次恢复演练时用 `sha256sum -c` 校验备份,**3/3 全部 MISMATCH**;
改用 PowerShell 比对则 3/3 通过 —— 数据完好,是清单**格式**的问题:
UTF-8 BOM + CRLF 行尾 + 反斜杠路径。

**为什么必须修**
灾难恢复的典型场景就是 Windows 起不来。那时只有 Linux live USB 或 WSL 可用,
`sha256sum -c` 是最自然的校验方式。清单只能被 PowerShell 读,
等于**把恢复路径限死在「Windows 还能启动」这个前提上** —— 而这正是备份要应对的场景。

**决定**
`SHA256SUMS.txt` 改为:无 BOM · LF 行尾 · 正斜杠路径 · 哈希与路径间两个空格。
`BACKUP-REPORT.md` 同改为无 BOM,并补齐两套校验命令(bash / PowerShell)与四步恢复流程。

**复验**
`sha256sum -c` 3 个全 OK 退出码 0;`git clone code.bundle` 后 HEAD tree 哈希与原仓库一致。

**推翻条件**
不适用 —— 兼容标准格式没有下行风险。

---

## 2026-07-26 · 显卡驱动由 596.36 升级到 610.62（未预期的变更）

**背景**
CUDA Toolkit 的 installer 捆绑显卡驱动。原计划用静默安装排除 Driver 组件,
以避免 596.36 被降级(sm_120 支持依赖驱动)。但 `-extract=` 参数并未只解包而是启动了
安装向导,实际走的是默认全量安装。

**结果**
驱动被**升级**到 610.62,不是降级。回归验证全部通过:

- `nvcc` release 13.2 V13.2.51 可用
- ComfyUI 的 torch 2.11.0+cu128 仍 `available=True`,capability `(12,0)`,实跑 matmul 通过
- 真编译一个 `-arch=sm_120` kernel 并运行成功

**为什么这个结果反而更好**
CUDA 的兼容性方向是「driver ≥ toolkit」。原来 596.36 支持上限是 13.2,
装 13.2 toolkit 属于刚好贴边;升级到 610.62 后 toolkit 处在更宽松的位置。
smoke test 报告 `runtime/drv = 13030 / 13030`,说明驱动带的运行时比 13.2 更新。

**代价**
默认安装附带了 NVIDIA App · ShadowPlay · FrameView SDK · PhysX · Virtual Audio ·
Telemetry Client · Nsight 三件套。常驻 3 个 `nvcontainer`(约 188 MB 内存)。
**对一个显存吃紧的项目,ShadowPlay 与 NVIDIA App 的常驻部分值得评估是否关闭** ——
但这取决于你是否要用 ShadowPlay 录游戏,不由我决定。

**推翻条件**
若 P1 实测发现新驱动在 sm_120 上有性能回退或稳定性问题,回滚到 596.36 并改用
静默安装(正确做法是 `-s <组件名列表>`,组件名需先从安装包 manifest 读出,不能猜)。

---

## 2026-07-26 · 构建环境用封装脚本而非只改 PATH

**背景**
STATE.md 原有阻塞项:「`cmake` 不在系统 PATH」。最直接的解法是把 VS 自带的 cmake 加进 PATH。

**但那样不够**
编译 CUDA 还需要 `cl.exe`、`link.exe`、Windows SDK 的 include/lib 路径 ——
这些只有 `vcvars64.bat` 初始化之后才存在于环境里。只加 cmake 会造成
「cmake 能跑但配置阶段找不到编译器」的假就绪。

**决定**
两件事一起做:
1. 把 VS2022 自带 cmake 3.31.6 与 ninja 1.12.1 加入**用户级** PATH(原值已备份)
2. 写 `90-ops/devshell.ps1`,dot-source 后一次性备齐 MSVC + CUDA + CMake + Ninja,
   并逐项校验。用 **vswhere** 定位 VS,不硬编码安装路径(遵守 §11.1)

**理由**
「可复现」比「方便」重要。将来在另一台机器上,`devshell.ps1` 能自己找到 VS;
而写死的 PATH 不能。

**推翻条件**
若将来独立安装了更新版 cmake 并与 VS 自带版本冲突,应从 PATH 移除 VS 版本,
只保留 `devshell.ps1` 的动态定位。

---

## 2026-07-26 · `cache/hf` 排除出 GC 自动清理 ✅ 已确认并写回方案书

**背景**
v2.1 §8.4 定义 CACHE 是「唯一可静默清理的根」,85% 水位自动清空。
但 HF 缓存虽然语义上可重建,重获成本高:动辄十几 GB,仅 WiFi 866Mbps,
且部分模型将来可能从 HF 下架。

**决定**
`paths.toml` 中设 `[gc].hf_cache_auto_purge = false`,把 `cache/hf` 排除出自动清理,
改为在模型管家(§5.3)中按「最后使用时间 + 重获成本」提示,遵循 MODELS 根的同一原则
——**只提示,不自动删**。

**状态**
✅ 你已于 2026-07-26 确认,已写回方案书 **§8.4.1**。

**补充的论证**(写回文档时补上的)
GC 策略的分界不该只看「能不能重建」,还要看「重建的代价」:

| | 一般缓存(torch / pip / tmp) | `cache/hf` |
|---|---|---|
| 体积 | 几 GB | 十几到几十 GB |
| 重建耗时 | 秒到分钟 | **实测 ~11 MB/s,17GB 约 27 分钟** |
| 能否重建 | 一定能 | **不一定** —— 模型可能下架或改许可 |

一个能重建但要花半小时且可能失败的东西,不该被静默删除。

**推翻条件**
若实际使用中 `cache/hf` 增长失控而磁盘吃紧,应改为「按 last_accessed 分级清理」而非全清或不清。
