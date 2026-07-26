# P1 测量原始数据

> 按 §12.1 记录协议归档。**这些是证据,不是结论** ——
> 结论在 `../P1-thresholds.md` 各项的「实测值 / 结论」两格里。

| 文件 | 对应项 | 说明 |
|---|---|---|
| `a7-samples.csv` | **A7 · 极端上界** | UE5 + 游戏 + 9 标签 Chrome + 4K 全屏。55 个洁净样本,P99 **6.98 GiB** |
| `a7-scene1-ue5-chrome.csv` | **A7 · 场景1(定 `desktop_floor`)** | UE5(Epic 实时)+ 网页 + HD 视频 —— 用户原话「我的一般工作情景」。64 个样本 / 5.3 分钟,P99 **6.53 GiB** |
| `a7-attribution.csv` | A7 · 逐进程归因 | 极端场景那一轮的逐进程明细(342 条)。**只能排序,不可求和** —— 见下 |

**列义**:`ts` 时刻 · `nvml_mib` NVML 总用量 · `clean` AI 侧是否无进程占显存 ·
`top_proc`/`top_mib` 当刻最大占用方 · `n_procs` 占用 >100 MiB 的进程数。

**采样口径**(三轮一致,见 `../P1-thresholds.md` §1):

- 总量用 `nvidia-smi --query-gpu=memory.used`,**每 5 秒**
- 逐进程用 `\GPU Process Memory(*)\Dedicated Usage`,**仅用于排序,不可求和**(高估 27%)
- 洁净判据按「AI 侧进程是否**占显存**」判,**不按进程名**
  —— Claude 自己的工具进程跑 python 但占 0 MiB,name-based 会误判

**未归档的项**:A1 与 A2 的原始输出是脚本 stdout,数值已全部誊进 `P1-thresholds.md`
(三轮字节级一致,无需保留逐样本记录)。
