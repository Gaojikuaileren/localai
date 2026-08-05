# AppContainer 回环隔离 · 一次性勘察 spike

> 日期:2026-08-05
> 服务对象:`00-docs/decision-packets/appcontainer-isolation-mcp-transport-2026-08-05.md`
>            (待决 6 隔离路线三选一 · 待决 7 MCP 传输方式)

---

## ★ 这是什么性质的东西(必须先说清)

**这是【一次性勘察产物】,不进门禁,结论已经写进决议包。**

它**不是**长期守着的断言:

- `90-ops/run-tests.ps1` 的反向全表扫描根是写死的 `10-core`,只收 `test_*.py`。
  放在 `90-ops/spikes/` 下的东西**不会被判红,也永远不会被跑**。
- 因此这里**刻意不假装自己在门禁里**。本目录没有一条断言,只有一支勘察程序。
  它的产出是决议包里那张判据表,不是一个绿灯。

★ 这一条是照 2026-08-05 审计第 ⑦ 条的教训写的:`90-ops/debug/selfcheck.py`
因为不叫 `test_*.py`,扫描器根本收不到它,而 README 却写着「由某个测试文件检查」——
那个文件从来没存在过。**「看起来在门禁里、其实没人跑」比「明说不在门禁里」坏得多。**

⇒ 如果将来判定其中某条值得长期守(候选见决议包 §7),那要做的是:
在 `run-tests.ps1` 的 `$RULES` 里显式登记一条,由**主执行层**(它拥有 `90-ops`)去改。
**不要**靠本目录的存在来暗示它被守着。

---

## 跑法

两个入口,都是双击用的:

| 文件 | 要不要管理员 | 干什么 |
|---|---|---|
| `1-普通用户双击.cmd` | **不要** | ① 普通用户能不能自己打开回环豁免 ② AppContainer 到底挡住了什么 |
| `2-豁免粒度-需管理员.cmd` | **要** | 豁免有没有端口粒度(加了豁免之后 18081 会不会一起被放开) |

★ **`1-` 必须由用户在自己的上下文里双击**,不要从已提权的终端里敲。
理由是 D46:agent 的运行上下文可能被提权,而**提权上下文里测出来的
「普通用户能不能」是无效的**。程序自己会检测完整性等级,是 High 就当场喊出来。

### 机器级状态

除了「回环豁免列表」,两个脚本都不碰任何机器级状态:
不改防火墙规则、不装驱动、不启用 Windows 功能、不建账户。

回环豁免列表的处理:

- 跑之前先读一次,记下**进场时是不是空的**;
- 加完测完,先试 `-d` 删单条;
- 删不掉、**且进场时列表本来是空的**,才用 `-c` 清空;
- 进场时列表里本来有别的条目 ⇒ **拒绝清空**,改为让人手动撤 —— 免得清掉别人的豁免。

`AppContainer profile` 是每用户的,建与删都不需要管理员,收尾会删掉。

---

## 命令行

```
AcSpike run [--keep] [--self-exempt] [--lan]   零提权主流程
AcSpike run-exempt                     需管理员:测豁免粒度
AcSpike user-exempt-probe              普通用户能不能自己开豁免(看效果,不看退出码)
AcSpike profile-add / profile-del      单独建 / 删勘察容器
AcSpike unix-check                     AF_UNIX 能不能 bind
AcSpike medium-probe <cmd路径>         造一个受限 token 去跑某个 .cmd(★ 见下方已知缺陷)
AcSpike probe <cfg> <out>              内部用:被投进容器里的那一半
```

两个环境变量:

| 变量 | 作用 |
|---|---|
| `LOCALAI_PROBE` | 文件侧探测目标,形如 `名字=路径;名字=路径` |
| `LOCALAI_TARGET_PORT` | 打一个**已经有人在听**的端口(如真在跑的 `llama-server:18081`)。设了就不自己绑监听 —— 用来去掉「我是用替身监听测的」这条保留意见 |
| `LOCALAI_SOCK_DIR` | AF_UNIX socket 的落点目录(会显式授权给容器 SID)。**不设就落 out 目录,而那是个坏落点**(见下方缺陷 2) |
| `LOCALAI_UNIX_DIRS` | 只给 `unix-check` 用:额外要试的目录,分号分隔 |

**代码里不写死任何盘符**(§11.1 路径契约),真实路径由调用方从 `config/paths.toml` 取。

### ★ `--lan` 默认是关的,别顺手打开

它会在 `0.0.0.0` 上绑监听去测「换成本机 LAN IP 能不能绕过回环隔离」。
代价:Windows 会弹「是否要允许公共网络和专用网络访问此应用」,
而**不管点「允许」还是「取消」都会留下一条持久防火墙规则**
(点「取消」留的是 Inbound **Block** 规则)——
一支勘察程序不该留下这种机器状态。这一格已经测过,不需要每次重跑。

编译产物(`bin/` `obj/`)一律落 `%TEMP%\localai-acspike\`,不进仓库:
仓库根的 `.gitignore` **没有**覆盖 `obj/`,所以本目录自带一份挡着,
两个 `.cmd` 也显式传了 `-p:BaseIntermediateOutputPath`。

---

## 已知缺陷(交回时如实记账,不要当成能用)

1. **`medium-probe` 造不出 Medium 完整性的可用进程 —— 别再往这条路上投时间。**
   token 造得出来(`CreateRestrictedToken` + `DuplicateTokenEx`,降 Medium 确认成功),
   但起出来的进程**一律** `0xC0000142`(STATUS_DLL_INIT_FAILED)。已试过并全部失败:
   `CreateProcessWithTokenW` / `CreateProcessAsUserW`(后者进程创建返回成功,子进程仍然
   `0xC0000142`)· `lpDesktop=winsta0\default` · `CREATE_NEW_CONSOLE` ·
   去掉 `DISABLE_MAX_PRIVILEGE`。
   ⇒ 卡点在**窗口站/桌面的 DACL 没授权给这个受限 token**;MSDN 的解法要改
   `winsta0`/`default` 的 ACL,**为一次测量去动会话级系统对象不合适,故止步**。
   ⇒ 「Medium + Administrators 只用于拒绝」这一格**只能靠用户双击 `1-` 补**。
   **别信这个模式的沉默**,也别再重试上面那几条。

2. **AF_UNIX 的落点很讲究,而且默认落点是坏的那个。**
   实测:在 `{state}` / `{cache}\tmp` / `{code}` / `C:\Windows\Temp` 下
   bind + connect + 收发 + 干净删除**全通**(路径长度与空格都不是变量);
   **唯独机主 `%TEMP%` 下 connect 报 `WSAEINVAL(10022)`**,且失败会留下
   **永久删不掉**的孤立 ReparsePoint(`fsutil reparsepoint query` 报 `Error 1920`),
   把该路径后续的 bind 也一起弄坏。
   ⇒ `run` 的 socket 默认放在 out 目录(就在机主 `%TEMP%` 里)**是坏落点**,
   要测 AF_UNIX 必须用 `LOCALAI_SOCK_DIR` 指到一个实测可用的目录。
   ★ 我初版据此写过一句「AF_UNIX 不适合做长期常驻通道」——**那是过度推广,已作废**。

3. 容器内 `TCP connect` 被静默丢包时表现为**挂住**,不是立刻报错。
   探测器给了 4 秒与 30 秒两档,就是为了把「被丢包」和「只是慢」分开。
