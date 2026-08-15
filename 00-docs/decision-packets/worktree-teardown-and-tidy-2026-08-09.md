# V28 · 拆工作树 + 整理项目文件(D131)

> 车道:V28(worktree `xenodochial-chaum-14b2e8`,分支 `claude/v28-worktree-cleanup-a99628`)· 2026-08-09
> 本车道做的是**删除与移动**,是全项目最不可逆的动作。
> ★ 本包的价值判据不是「删了多少」,是**「每一样东西为什么可以删」说清了没有**。

---

## §0 一句话结论

- **任务 1(拆工作树)· 做了**:6 个目录 + 6 个空壳孤儿目录全部移除,**一个分支都没删**(前后都是 34 条本地分支),回收 **≈2.06 GB**。
- **任务 2(`dist/` 整理)· 整段跳过**:自检判据不成立 —— 两个版本戳**都**落后 `main`(client −24、admin −10)⇒ V27 未收工。本包只做**只读勘察**,一个字节都没动 `dist/`。
- **任务 3(随包文档指空)· 只登记不改**:量出 **5 处**新的(第 11–15 处),并给出「护栏要不要扩到随包文本」的判据形状与代价 —— 结论是**先别扩**,理由见 §3.3。
- **任务 4(`00-docs/` 归档)· 判定不动**:三个数字挡住了,见 §4。
- `parked/selfpair`:**只是一条分支**,不是工作树、不在磁盘上;本车道全程没有碰它的路径。

---

## §1 任务 1 · 拆工作树

### 1.1 ★ 先更正:交办清单里的那张表是过期的

清单给的六行里,**第一行已经不成立**:

| 清单说的 | 盘上的实况(开工时实测) |
|---|---|
| `xenodochial-chaum-14b2e8` → 分支 `claude/v21-admin-migration-abece2`,删目录 | 该目录**已被本车道自己占用**,分支是 `claude/v28-worktree-cleanup-a99628`。`claude/v21-admin-migration-abece2` 仍在,只是**不再被任何工作树签出** |
| 另外四行标着具体分支名 | 这四个工作树全是 **detached HEAD**,并不在那些分支上 |

⇒ 清单里「★ 别在收尾时把自己那棵一起删了」这句提醒,实际命中的正是**第一行它自己**。
本车道**没有**删 `xenodochial-chaum-14b2e8` —— 人还站在里面。它由协调层在本包并入后处置。

### 1.2 ★★ 一条清单没预料到的风险(差点静默删掉 1.9 GB 未入库产物)

清单引用的实况是「七个工作树全部 `dirty=0`」。**`dirty=0` 不覆盖被 `.gitignore` 挡住的内容** ——
而 `dist/` 正在 `.gitignore:39` 里。逐个查后,**三个**工作树里有未入库的出包产物:

| 工作树 | 里面的 dist | 版本戳 | 与 main 的关系(按 SHA256 逐个对) |
|---|---|---|---|
| `v15-sync-snapshot-pull-f8e45c` | `client-pack` | `20260809-1314+0f9fa1c` | **与 main `dist/client` 逐字节相同**(同为 `3A4B05A7…`)⇒ 重复件 |
| 同上 | `admin` / `admin-pack` | 同上 | 比 main `dist/admin`(`4044671`)旧 ⇒ 已被取代 |
| `stackstop-kill-safety-assertions-0ffd29` | `admin` / `client-pack` | `20260809-1620+59c7af5` | V23 中途件,已被取代 |
| `agent-ad36411fae961778d` | `admin` / `client-pack` | `20260808-1549+88414c3.**dirty**-28467cc1` | 从**未提交的树**出的包,已被取代 |

★ `dist/client/VERSION.txt` 里那句自检口径写着原位是
`…\worktrees\v15-sync-snapshot-pull-f8e45c\dist\client-pack` —— 那只是**当时在哪儿跑的**的记述,
产物已拷进 main 的 `dist/client`,并且**哈希逐字相同**。⇒ 删掉不丢东西,这一条是对上了哈希才敢下的。

### 1.3 逐样三行说明(是什么 · 谁产的 · 全仓引用数)

> 「全仓引用数」= `git grep -I -l -F <名字>` 在**已跟踪文件**里的命中文件数。

| # | 删掉的 | 是什么 | 谁产的 | 全仓引用数 | 为什么可以删 |
|---|---|---|---|---|---|
| 1 | `.claude/worktrees/gateway-stopgate-attribution-aa9d02` | V25 车道工作树,detached 于 `5571e27` | `git worktree add`(V25 开工) | **0** | `dirty=0`;`5571e27` 是 `main` 的祖先(ahead=0),另有同名分支持有 |
| 2 | `.claude/worktrees/ui-promises-guardrail-d0afb8` | V24 车道工作树,detached 于 `80b6b31` | 同上(V24) | **0** | `dirty=0`;`80b6b31` 是 `main` 的祖先;分支 `claude/ui-promises-guardrail-d0afb8` 留着 |
| 3 | `.claude/worktrees/stackstop-kill-safety-assertions-0ffd29` | V23/V26 车道工作树,detached 于 `e6eb498` | 同上(V23) | **3**(全是决议包/DECISIONS 里**记述车道名**的散文,非路径依赖) | `dirty=0`;`e6eb498` 是 `main` 的祖先;分支 `v26/revived-assertions` 与 `claude/stackstop-…` 都留着 |
| 4 | `.claude/worktrees/v15-sync-snapshot-pull-f8e45c` | V17/V22 车道工作树,detached 于 `1eeedfc` | 同上(V15) | **1**(决议包里的车道记述) | `dirty=0`;`1eeedfc` 是 `main` 的祖先;dist 内容已对哈希(见 1.2) |
| 5 | `.claude/worktrees/agent-ad36411fae961778d` | **V18** 车道工作树,分支 `worktree-agent-ad36411fae961778d`(ahead=3) | 同上(V18) | **3**(两处车道记述 + `admin/App.xaml.cs:215` 一句**引用分支名**的注释,不是路径) | `dirty=0`;★ 三个提交**由分支持有**,删目录不动提交。★★ **分支保留**,见 §6 |
| 6–11 | 6 个空壳:`cert-lifecycle-wiring-f002f3` · `core-identity-certlife-d8fbd5` · `localai-v2-gpu-identity-lease-d9c20e` · `memory-suite-revival-survey-94f0d0` · `pending-6-7-feasibility-6fccaf` · `v3-gate-honesty-36ba41` | **git 完全不知道它们存在**的残留目录(不在 `git worktree list` 里) | 更早的车道;工作树被移除后目录没跟着走 | 各 0–4,**全部是散文里的车道/分支名** | 逐个实测 `find -mindepth 1` = **0 个条目**(连隐藏文件都没有),体积 = 目录项本身。★ `AUDIT-2026-08-06` 里已把其中两个写成「~~已移除~~」,而空壳还在盘上 —— 删的正是这个「命名与磁盘对不上」 |

★ 6–11 **不在交办清单里**。它们是移除前 5 个之后才露出来的,处置依据是「0 文件 0 子目录 ⇒ 删除可证明无损」,
不是「看起来像残留」。**用的是 `rmdir`(非空会自己失败),不是 `rm -rf`。**

### 1.4 前后对照(清单要求贴进来的)

**BEFORE — `git worktree list`(7 条)**
```
E:/.meine/.Proj_Soft/.Proj/.localAI                                                           9a4bac5 [main]
…/.claude/worktrees/agent-ad36411fae961778d                 389a5ad [worktree-agent-ad36411fae961778d]
…/.claude/worktrees/gateway-stopgate-attribution-aa9d02     5571e27 (detached HEAD)
…/.claude/worktrees/stackstop-kill-safety-assertions-0ffd29 e6eb498 (detached HEAD)
…/.claude/worktrees/ui-promises-guardrail-d0afb8            80b6b31 (detached HEAD)
…/.claude/worktrees/v15-sync-snapshot-pull-f8e45c           1eeedfc (detached HEAD)
…/.claude/worktrees/xenodochial-chaum-14b2e8                9a4bac5 [claude/v28-worktree-cleanup-a99628]
```

**AFTER — `git worktree list`(2 条)**
```
E:/.meine/.Proj_Soft/.Proj/.localAI                                            9a4bac5 [main]
…/.claude/worktrees/xenodochial-chaum-14b2e8 9a4bac5 [claude/v28-worktree-cleanup-a99628]
```

**`git worktree prune -v` 输出:空**(退出码 0)。空 = 没有悬挂记录要清 ——
因为全程用的是 `git worktree remove`,没有手工删目录。`.git/worktrees/` 下现在只剩 `xenodochial-chaum-14b2e8` 一个。

**`git branch -a` 前后 diff —— 只有一行,而且不是分支增减:**
```
34c34
< + worktree-agent-ad36411fae961778d
---
>   worktree-agent-ad36411fae961778d
```
★ 变的只是行首那个 `+`(「正被某个工作树签出」的标记)。本地分支数 **34 → 34**。
`parked/selfpair` 与 `claude/v21-admin-migration-abece2` 都在。

**磁盘**:`.claude/worktrees/` 由 2110 MB → 533 MB(只剩本车道自己那棵),回收 **≈2.06 GB**。

---

## §2 任务 2 · `dist/` 整理 —— **整段跳过**

### 2.1 判据(照交办清单那三行跑的)

```
sed -n 2p dist/client/VERSION.txt   →  版本戳: 20260809-1314+0f9fa1c
sed -n 2p dist/admin/VERSION.txt    →  版本戳: 20260809-1738+4044671
git log --oneline -1                →  9a4bac5
```

| 包 | 短哈希 | 是 main 的祖先? | 落后 main |
|---|---|---|---|
| `dist/client` | `0f9fa1c` | 是 | **24 个提交** |
| `dist/admin` | `4044671` | 是 | **10 个提交** |

⇒ **两个都没追上 main**(清单只要求「有一个还落后」就跳过,这里是两个)。
**`dist/` 一个字节都没动**,也**没有**「顺手先删几个」。

### 2.2 ★★ 跳过之外,勘察捞到一条必须先说的(否则下一条车道会删错)

`dist/client-pack` **不是残留,而且它比 `dist/client` 新**:

| 目录 | 版本戳 | exe SHA256 前 8 |
|---|---|---|
| `dist/client` | `20260809-1314+**0f9fa1c**` | `3A4B05A7` |
| `dist/client-pack` | `20260809-1738+**4044671**` | `A9C77237` |
| `dist/admin` | `20260809-1738+**4044671**` | `1D3BD92C` |

★ `dist/admin/VERSION.txt` 自己写着「版本戳与同目录旁边的 `localai-client` 是【同一个】…对不上就别混用」。
**盘上与 `admin` 戳一致的那个 client 是 `client-pack`,不是 `client`。**
⇒ 把 `client-pack` 当「残留」删掉,会删掉**唯一一份与在售 admin 配套的客户端**,
并且 `admin/VERSION.txt` 那条自我判据会从「当场不成立」变成「永远不可能成立」。
★★ 这与今天已经栽过的那次(P4 清单把 `dist\client-pack` 称作「残留、没人再更新」)是**同一个形状**,第二次。

### 2.3 备执行清单(下一条车道用;本包**未执行**)

| 对象 | 是什么 | 谁产的 | 引用数 | 建议 |
|---|---|---|---|---|
| `dist/2nd-pc/` | P3b 时代副机包:`localai-client-transport.exe` + `开始配对-第二台PC.txt` | **全仓没有任何脚本产它**(`git grep` 遍 `90-ops` 无命中)⇒ 手放的部署件 | 0 处引用该路径 | ★★ **优先处置**,理由见 §3.2。★ 注意 `20-client-win/transport/localai-client-transport.csproj` **今天仍在仓库里**,所以这个 exe 不是幽灵 —— 有害的是它那份**文案**指的流程 |
| `dist/client-stage` | 待查(本包未展开) | 待查 | 待查 | 先查戳再动 |
| `dist/_backup-20260806` | 待查 | 待查 | 待查 | 先查戳再动 |
| `dist/gw-access.log` / `.log.err` | 网关访问日志(2026-08-05) | 运行期产生 | 0 | 低风险 |
| `dist/admin-pack` | **只有 exe+pdb,没有 `VERSION.txt`/`SHA256.txt`** ⇒ `$Out` 那个半截产物 | `build-client.ps1`(V27 正在修) | 0 | V27 修完 `$Out` 后再判 |
| `dist/host/` | **活的部署件**:`localai-lan-edge.exe` + `localai-identity.exe` + 4 个 `.cmd` + 2 份说明 | ★ 全仓没有脚本能重出它 | — | **不要删**。★★ 并登记:V25 的副机归因有一半在 `localai-lan-edge.exe`(2026-08-06 那份)里,**重出 client+admin 修不好它** —— 请协调层派人 |

---

## §3 任务 3 · 随包文档里指了个空的(只登记,不改)

> `90-ops/build-client.ps1` 是 V27 禁区 ⇒ 本车道**一行都没改**。下表交给协调层派。

### 3.1 表(V24 修掉 10 处;这里是第 11 处起)

| # | 文件 | 原句 | 实况(逐条查过源码) | 谁生成 |
|---|---|---|---|---|
| **11** | `dist/client/安装说明.txt`(+`client-pack` 同一份) | 「打开 **系统 → 设备**,在已配对卡片里直接【改地址】」 | 「系统」是导航分组(`nav.system`)**在**;「**设备**」**不在** —— `MainWindow.xaml.cs:528`「设备/配对已并入设置」,`SelftestMoved.cs:1667` 还钉着「设备不再单列」。★「改地址」**在**(`DevicesView.cs:254`)。今天的路径是 **系统 → 设置 →「已配对的电脑」→「改地址」** | `build-client.ps1:484` here-string |
| **12** | 同上 | 「六个词的短语要与主机上显示的逐字一致才按"**确认**"」 | 客户端**没有**叫「确认」的键。真正的键叫「**词一致,批准**」,而且**在管理端那个 exe 里**(`admin/Views/HostHubView.cs:902`)。客户端自己的文案说得对:「到主机那台的管理端「主机中枢」页上…再按「词一致,批准」」(`DevicesView.cs:363`)。★ 与 V24 抓到的「接受」**从来没有过**是同一族 | 同上 |
| **13** | 同上 | 「不要"**解除配对**"」 | 客户端上的键叫「**解除本机配对**」(`DevicesView.cs:186`)。★ 与已登记在 `SelftestUiPromises.KnownBroken` 里的 `TlsFailure.cs`「重新配对」同一族 | 同上 |
| **14** | `dist/client/VERSION.txt`(+`client-pack`) | 「管理端 PASS=**134**,FAIL=0」 | 管理端今天(出包形态)是 **PASS=164** —— 就写在旁边的 `dist/admin/VERSION.txt` 里。★ 这不是指名控件,是**写死的数字过期**,但危害同族:读的人会拿 134 去找一个不存在的回归 | `build-client.ps1:451` here-string |
| **15** | `dist/admin/VERSION.txt` | 「版本戳与同目录旁边的 client 是【**同一个**】…对不上就别混用」 | **盘上就对不上**(`4044671` vs `0f9fa1c`)⇒ 这句判据**当场不成立**。★★ 而与它一致的那份 client 确实存在,叫 `client-pack`(见 §2.2)—— 所以这句话不是「说错了」,是**指错了目录** | `build-client.ps1:471` here-string |

### 3.2 ★★ 同族里最有害的一份:`dist/2nd-pc/开始配对-第二台PC.txt`

它不属于「指名了不存在的控件」,属于**整套流程今天不该走** —— 而它就摆在副机部署路径上:

- 写死主机 IP `192.168.178.61`(出现 **3 次**,含粘贴即用的整条命令行);
- 让人在**主机控制台**敲 `approve <请求号前几位>`;
- 用的是 `localai-client-transport.exe`(P3b 时代的无 UI CLI),不是今天的 WPF 客户端。

★ **必须说准的一点**:`list` / `approve` / `deny` / `open` / `close` / `quit` 这些控制台命令
**今天仍然存在**(`10-core/lan-edge/Program.cs:342–372`),`dist/host/主机-开机上线.txt` 里那条路是通的。
所以这份文件的问题**不是**「指了个不存在的命令」,而是:
① 写死的 IP 换网段就失效(`主机-开机上线.txt` 自己都专门警告过这一条);
② 它把副机用户领上 CLI 那条路,而今天的路是**管理端「主机中枢」页 →「词一致,批准」**。
⇒ 判词照搬 V24 那条:**给错方向的指路比不指路更坏。**

### 3.3 ★ 护栏要不要扩到「随包文本文件」——判据形状与代价

**先给结论:今天不扩。** 理由不是「代价高」,是**扩了也盖不住最坏的那两份**。

**若要扩,判据只能是这个形状:**
1. **扫生成器,不扫产物。** `dist/` 在 `.gitignore` 里 ⇒ 干净克隆上产物根本不存在。扫产物的判据在 CI 上要么恒空(而 V24 立的「零命中要判红」会天天红)、要么恒绿。可扫的是 `build-client.ps1:443/451/471/484` 那四个 here-string —— 它们在库里、可 diff、确定性。
2. **要多认一种字面量语法**:PowerShell here-string `@"…"@`。V24 的提取器只认 C# 字面量,且会剔掉 `//` 与 `/* */` —— PS 的注释是 `#`,得另写一套,而这正是 V24 自己踩过三次的那个坑(注释被当成证据)。
3. **要加第三张表:「这份文档属于哪个程序」。** V24 的两张词表按 csproj 编译集建,而随包文档是写给**站在机器前的人**看的 —— 它**本来就该**指向另一个 exe(「到管理端『主机中枢』页」)。所以归属不能由 csproj 推,必须显式登记。

**代价与盖不住的地方:**
- 新增一个扫描器 + 一条 PS 解析路径 + 一张手工归属表;
- `build-client.ps1` 今天是 V27 禁区 ⇒ 最早也要等它收工;
- ★★ **`dist/2nd-pc/*` 与 `dist/host/*` 结构上扫不到**:全仓没有任何脚本产它们(已 grep 遍 `90-ops`),
  它们只存在于磁盘、不在版本控制里。而 §3.2 那份**恰恰是今天最有害的一份**。

⇒ **真正的洞不是「护栏没扫 .txt」,是「有两份随包文档没有生成器、也不在 git 里」。**
先把它们**纳入仓库或删掉**,再谈扫描。顺序反了的话,护栏扩完了,最坏的两份仍在护栏外,
而看起来像已经防住了 —— ★ 那正是本仓第一戒律的形状。

---

## §4 任务 4 · `00-docs/` 与仓库根的结构 —— **判定:不动**

### 4.1 先量

- `00-docs/decision-packets/`:**54 份**,最早 `2026-07-28`,最晚 `2026-08-09`,全都带日期。
  ★ 按月分:**2026-07 = 1 份,2026-08 = 53 份**。
- `00-docs/audit/`:**5 个**(4 份审计 + `_LAST-RUN.txt`),最早 `2026-08-05`。

### 4.2 三个挡住它的数字

1. **`90-ops/gate/check_decision_numbers.py:128` 是 `PACKETS.glob("*.md")` —— 不递归。**
   移进 `decision-packets/2026-08/` 之后它扫到 0 份。
   ★ 这**不会**静默变绿:同文件 `:129` 有一条元断言「扫到 0 份 = 路径写错,而 0 份天然全绿」,
   `len(files) >= 5` 不成立就 `return 2`。⇒ 后果是 **D 号闸当场瘫掉**(exit 2),直到有人改这个脚本。
   而这个闸是 `c1529c3` 刚立的、立起来当天就捞出一批漏号 —— 为一次纯观感的搬家去动它,不划算。
2. **102 行引用 · 37 个文件**在提 `decision-packets/` 这个路径,其中 **18 个文件在 `00-docs/` 之外** ——
   含 `.githooks/pre-commit`、`90-ops/run-tests.ps1`、两个 gate 脚本、以及 4 处 C#/Python 源码注释。
   ★ 其中 `run-tests.ps1` 与 `build-client.ps1` 属 V27 禁区,本车道改不了 ⇒ 搬家**做不完整**。
   (`pre-commit:425` 那条 `^00-docs/(DECISIONS\.md|decision-packets/)` 是前缀匹配,**能活**;能活的只有它。)
3. **收益近于零**:53/54 都落在同一个月。按月归档的产物是一个装 1 份的 `2026-07/` 和一个装 53 份的 `2026-08/` ——
   分完之后该翻的还是那 53 份。`audit/` 只有 4 份,更不用分。

★★ 还有一条不是数字的:`decision-packets/` 的目录时间戳在本车道开工期间还在变(18:32),
说明**别的车道正在往里写**。54 个文件的批量搬移与并发写正面相撞 —— 这是 D82 那条并发纪律要挡的形状。

⇒ **不动。改了几处引用:0 处。** 这也是清单认可的合格交回(「若引用面太广、改动风险大于收益,明说不动并给理由」)。

### 4.3 顺带登记(未改)

- `00-docs/STATE.md:3` 抬头写「更新时间 2026-08-06 夜」而正文已是 08-09 基线 ——
  **STATE 是 V27 的文件,本包只登记不改。**

---

## §5 `parked/selfpair` 今天的形态

| 问题 | 实测 |
|---|---|
| 工作树? | **否**。`git worktree list` 里没有它(前后都没有) |
| 磁盘上的目录? | **否**。`find -iname "*selfpair*"` 只命中两份**决议包文档** |
| 分支? | **是** —— `refs/heads/parked/selfpair` @ `2f33314`「封存:主机自配对(判 DO_NOT_COMMIT,不得合入 main)」 |
| 与 main | ahead=**1**,behind=**227** |
| 推过没有? | **没有**。`git ls-remote --heads origin` 只有 `refs/heads/main` |
| 被 `.gitignore` 挡着? | **没有** —— `.gitignore` 与 `.git/info/exclude` 里都搜不到 `selfpair`。它没有任何**机器可读**的保护 |

**整理动作有没有可能碰到它?** 本车道**没有**:
唯一能伤到它的动作是删分支,而本车道**删了 0 条分支**(34 → 34,diff 已贴在 §1.4);
它不在磁盘上,所以目录清理与 `dist/` 整理都够不着它。

★ 但风险是真的、而且已经被人记过一次:`OPEN-ITEMS-2026-08-09.md:124` 写着
「`parked/selfpair` 是有意封存不是残留,但今天没有任何机器可读的东西知道它存在 ⇒ 将来任何自动清理都可能把它当残留清掉」。
本车道靠的是**读到了那一行**,不是靠护栏。⇒ **建议给它一条机器可读的保护**(例:分支名前缀 `parked/` 进删除黑名单的断言)。
本包不代做 —— 它该由拿着 `90-ops/gate/**` 的车道立。

---

## §6 V18(`worktree-agent-ad36411fae961778d`)· 存档说明

> ★ 这就是清单要求「在交接文档或某处留一条存档说明」的那条。**存档位置 = 本节。**
> 放这里而不是塞进 `STATE.md` / `COORDINATOR-HANDOFF.md`:那两份今天有别的车道在动(D82 并发纪律)。
> 请协调层在并入时决定是否上抬进中央文档。

- **目录已删,分支保留**:`worktree-agent-ad36411fae961778d` @ `389a5ad`,ahead=**3**、behind=**74**。
- **★★★ 不要合它。** 协调层已量:`git diff main worktree-agent-ad36411fae961778d` = **+4508 / −17650**。
  合它会删掉 **D115/D116/D117**、`ASSERTION-PITFALLS` 第 18–21 条、D 号闸,以及 V21 的整片迁移
  —— diff 里 `{admin => app}` 那四个文件就是把 V21 搬进管理端的东西**搬回客户端**。
- **它的价值已被逐项超越**:托盘命名常量 · 测试缝 · `--selftest` 分流 · `LIFE` 哨兵 —— 逐项验过 `main` 全都有。
  盘上还留着一处指纹:`20-client-win/admin/App.xaml.cs:215` 的注释写着
  「诚实的测试缝(**捞自未并分支 `worktree-agent-ad36411fae961778d`**,逐段搬,不整片 apply)」。
- ⇒ **只留分支,供追溯;V18 已被 V19–V26 全面超越。**

---

## §7 门禁 · DEBT · 没做的

| 项 | 值 |
|---|---|
| `check_contract_pairs.py` | `TOTAL=31 PAIRED=30 **DEBT=1**` · 331 PASS / 0 FAIL · exit 0 ⇒ **契约满足(仍是 1/31)** |
| `check_decision_numbers.py` | 54 份包 · 88 个编号标题 · 存量欠债 **16/16** · 4 PASS / 0 FAIL · exit 0 |
| 本包引用的 D 号 | D48 D49 D80 D81 D82 D101 D115 D116 D117 —— **逐个查过都有编号标题**,不新增欠债 |
| 本车道的 D 号 | **`D?`** —— 按 D82,取号在并入那一刻 |

**没做的(明说):**
1. **`dist/` 整段没做** —— 判据不成立(§2.1),不是忘了。备执行清单在 §2.3。
2. **随包文档一处都没改** —— `build-client.ps1` 是 V27 禁区,§3 只登记。
3. **`00-docs/` 没归档** —— 判定不动,理由在 §4.2,改了 **0** 处引用。
4. **`00-docs/STATE.md:3` 那处过期抬头没改** —— V27 的文件。
5. **`dist/client-stage` 与 `dist/_backup-20260806` 没查实** —— 它们在跳过的那一节里,没展开。
6. **本车道自己的工作树 `xenodochial-chaum-14b2e8` 没删** —— 人还在里面;请协调层在本包并入后处置。
7. **没给 `parked/selfpair` 立护栏** —— 建议写在 §5,该由拿 `90-ops/gate/**` 的车道做。

**边界遵守情况**:未动 `90-ops/build-client.ps1` · `90-ops/run-tests.ps1` · `00-docs/STATE.md` ·
`20-client-win/**` · `10-core/**`;删分支 **0** 条;未用 `git clean -fd`、未用 `rm -rf`、未用 `--force`。
