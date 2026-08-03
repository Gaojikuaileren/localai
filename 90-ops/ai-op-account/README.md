# `ai-op` 受限账户 —— 两层 MCP 设计【形态 A】的落地

> ## ⛔ 状态:**封存 · 等 P6**(用户裁定,2026-08-03)
>
> 这套脚本是为**两层 MCP 的层二**(外部控制面)服务的,而层二在方案书里**没有阶段归属**,
> 已随决议包一并封存 —— 见
> [`00-docs/decision-packets/two-layer-mcp-decisions-2026-08-03.md`](../../00-docs/decision-packets/two-layer-mcp-decisions-2026-08-03.md)。
> **现在不要跑它。** 层二不存在的时候,建一个 `ai-op` 账户没有任何东西会去用它,
> 却真实增加了一个需要维护 ACL 的攻击面。
>
> ★ **一件已经【提前执行】的相关事**:`ai-op` 已被加进 `gateway.py` 的 `LOCAL_DENY_ACCOUNTS`。
>   那不是为了这套脚本,而是因为 `classify_caller` 的判据是「不在拒绝表就给 `trusted-local`」——
>   **账户一旦被创建,它默认就是全系统最高档**。名字先进拒绝表,账户以后再建,顺序不能反。
>
> ★ **与本目录无关、但同源的一件事已经做了**:`90-ops/state-acl/` ——
>   那是给**已经部署**的 `{state}` 子目录补断继承,属于 P2/P3a 的回头账,不是层二的前置,
>   **不在封存范围内,可以跑**。
>
> ---
>
> 编制日期:2026-08-03 · 依据「localAI 两层 MCP 最终设计」§3.0 / §9-R1 / §10-Q1
> 状态:**脚本已就位,尚未执行。** 请你自己在提权终端里跑 —— 建账户与改 NTFS ACL
> 属于系统/安全设置变更,不交给 AI 执行(先例:TPM 密钥也是你在本人上下文里跑的 .cmd)。

---

## 1. 这套脚本干什么、为什么

两层 MCP 设计 §3.0 把部署分成两个形态,并明说「用户必须拍板」:

| 形态 | 说明 | 层二的性质 |
|---|---|---|
| **A · 受限操作者(已裁定采用)** | 外部 AI 跑在专用 Windows 标准账户 `ai-op`(非管理员);该账户对 `{state}\**`、机主 profile 目录显式 **Deny ACE**,只对仓库子树可写 | 层二是**遏制边界**。红区/黄区裁决真正成立 |
| B · 同用户操作者(此前的现状) | 外部 AI 以机主身份跑,能直接 `type chat.json`、`pg_dump`、改 `config\*.toml` | 层二是**问责与可复原能力**,**不是遏制**。红区只是「laictl 这条路上看不到」 |

采形态 A 的理由不是洁癖,是**层二的全部强制点都建立在它之上**。设计文档 §9-R1 / §9-R2 已经如实写了:
形态 B 下,「`ctld` 二进制不链接 PG 驱动」「编译期黑名单」这类防线的前提是攻击者不能改源码、
不能重编译 —— 而那恰好是操作者的本职。同理 §3.6 的审计哈希链:**写链的账户与被防的主体是同一个,
链就防不住它**。这两条在形态 B 下**无法修复**,只有换账户能修。

这套脚本就是「换账户」这个动作。

---

## 2. 文件

| 文件 | 干什么 |
|---|---|
| `ai-op-paths.ps1` | **三个脚本共用的唯一一张路径表与判定函数。** 不要直接运行 |
| `create-ai-op.ps1` | 建账户 + 施加 ACL |
| `verify-ai-op.ps1` | **纯只读**校验,输出 PASS/FAIL 清单。**最重要的一个** |
| `revert-ai-op.ps1` | 完整回退 |
| `ai-op-applied.json` | create 落的施加记录(面包屑,不是信任根;revert 不依赖它决定改哪些路径) |

**为什么路径表要单独抽一个文件**:两层 MCP 设计 **§3.5 约束 2** 已经就同一件事裁过 ——
「`write_text_file` 与 `patch_toml_key` 共用同一张路径黑名单表,元测试断言引用同一符号」。
(★ 这里**只引条文不引编号**:那条决议在设计文档内部编作 D65,而本仓库的 D65 是
Hermes Agent Worker —— 两个 D65 是不同的东西。它并入中央文档后的编号见第 10 节。)
理由是两份各自维护的清单必然漂移,而漂移出来的那一条就是没有闸的路。
如果 create 挡五个目录、verify 只查四个,那第五个静默失效时**没有任何东西会变红**。

---

## 3. 跑之前:两个必须显式选的参数

它们**没有默认值**。这不是啰嗦 —— 它们各自决定这套 ACL 到底挡不挡得住,
给默认值就等于替你做了一个安全裁决(fail-closed:未知取值一律拒绝,不设放行默认值)。

### 3.1 `-Membership`

| 取值 | 后果 |
|---|---|
| `users-group` | 加入 `BUILTIN\Users`(且仅此一个组)。**账户能登录、能跑 Claude Code** |
| `no-group` | 一个组都不加。**这个账户跑不了任何东西** |

**实测事实(2026-08-03,`Get-Acl` 读的)**:`C:\Windows` 与 `C:\Program Files` 的 ACL 里
给普通用户的只有 `BUILTIN\Users : ReadAndExecute` 这一条,**没有 `Authenticated Users`**。
所以不在 `Users` 组的账户读不到系统目录,连 `runas` 起一个进程都做不到。

既有的 `ai-mem` / `ai-asset` / `ai-exec` 三个账户确实一个组都不在(`setup-accounts.ps1` 明写
「不加入任何组 —— 最小权限」),但它们是**服务账户**,由服务控制管理器以服务登录方式起进程,
不需要交互登录。`ai-op` 是**要真的跑一个交互式 CLI** 的账户,情况不同。

⇒ 想用起来就选 `users-group`。`no-group` 只在「先把架子摆好、稍后再决定」时有意义。

### 3.2 `-Containment` ★ 最重要的一个选择

| 取值 | 后果 |
|---|---|
| `enumerated` | 只对枚举出来的禁区打 Deny |
| `drive-wide` | 额外在 `D:\` 与 `E:\` 盘根打 Deny,再对仓库子树打显式 Allow、对仓库每一级祖先打「本文件夹」的穿越许可 |

**为什么需要 `drive-wide` —— 一条实测出来的坑**:

`D:\` 与 `E:\` 的盘根 ACL 里都有 `NT AUTHORITY\Authenticated Users : Modify`,并且**向下继承**。
`ai-op` 一建出来就是 Authenticated User。⇒ 在 `enumerated` 模式下,
它**仍然能写这两块盘上禁区之外的其它位置**(仓库的同级目录、`D:\AI` 下没被单独保护的地方……)。

也就是说:**「只对仓库子树授予读写」这句话,在 `enumerated` 模式下不成立。**
`verify` 会为此报一条 FAIL —— 那是如实反映,不是脚本 bug。文档不得声称它成立。

顺带一提,同一个发现也说明:`{state}` 上的 Deny ACE **不是** belt-and-suspenders,而是**承重的**。
`D:\AI\state\identity`(成员表与证书公共材料,D43 S0.8)现在就带着继承来的
`Authenticated Users : Modify` —— 任何新建的本地账户默认就能改它。

`drive-wide` 的代价:盘根加可继承 ACE 会向下传播,两块盘文件多时可能跑几分钟到十几分钟,
期间不要中断。这是一次性成本。

### 3.3 `-ProtectRepoConfig`(可选)

额外把仓库内的 `config\` 设为 `ai-op` 不可读写(CFG-1 四份配置的落点)。
**代价**:`git checkout` / `git pull` 一旦触及 `config\` 里的文件,在 `ai-op` 下会失败。
不开也可以 —— 但那时 `config\*.toml` 对 `ai-op` 可写,见第 7 节诚实声明。

---

## 4. 怎么跑

```powershell
# 0) 先演练,什么都不会改
.\create-ai-op.ps1 -Membership users-group -Containment drive-wide -WhatIf

# 1) 真跑(每一步会打印它要做什么并要一次确认;输入 y 之外的任何东西都算放弃)
.\create-ai-op.ps1 -Membership users-group -Containment drive-wide

# 2) ★ 必跑:校验(纯只读,可反复跑)
.\verify-ai-op.ps1
```

**有 FAIL 就先别切过去。** `verify` 查的不是「Deny ACE 在不在」,而是
**按 Windows 的 DACL 求值算法算出 `ai-op` 实际拿到几个权限位**。
「ACE 在」与「权限被拒」之间隔着三个坑,`verify` 的注释里逐条写了。

密码:默认 `-PasswordMode prompt`(你当场输,不回显,输两次);
`-PasswordMode random` 会随机生成并**只显示这一次**。
**脚本自身不含任何密码,也不把密码写进任何文件。**

---

## 5. 怎么让 Claude Code 跑在 `ai-op` 下

三种办法,从「最省事但有坑」到「最干净但最重」:

### 5.1 `runas`(最快,推荐先用这个试)

```powershell
# 在你自己的普通(非提权)终端里:
runas /user:ai-op "cmd /k cd /d E:\.meine\.Proj_Soft\.Proj\.localAI"
# 弹出的新窗口里 %USERPROFILE% 已经是 C:\Users\ai-op,在里面跑 claude
```

- ★ **必须从非提权终端起** —— D46 的提权护栏要求外部 AI 与 `laictl` / `ctld` 一律非提权运行。
  从管理员终端 `runas` 出来的进程会带上不该有的完整性级别。
- `runas` 每次都要输密码,且不能免密。嫌烦就用 5.2。
- ⚠ 第一次跑会创建 `C:\Users\ai-op` 的 profile,可能要等十几秒。
- ⚠ `runas` 起的进程**没有你的桌面会话上下文**:Windows 凭据管理器是按用户存的,
  剪贴板与部分 GUI 交互会不正常。

### 5.2 单独登录会话(最像正常用法)

`Win+L` 锁屏 → 切换用户 → 用 `ai-op` 登录 → 在它的桌面里开终端跑 Claude Code。
你自己的会话保持登录状态不受影响(快速用户切换)。

- 优点:环境完整,凭据管理器、终端、编辑器都正常。
- 缺点:两个桌面之间来回切;`ai-op` 的桌面是空的,要重装一遍终端习惯。
- ★ `ai-op` 是**标准用户**,在它的会话里**装不了任何需要管理员的东西**。
  所有工具链要么装在全机(由你在自己账户下提权装好),要么装在 `ai-op` 的 profile 里
  (npm 全局包、nvm、dotnet 用户级 SDK 这类不需要管理员的)。

### 5.3 计划任务(无人值守;**不推荐本期用**)

```powershell
# 由你在提权终端里注册一次;运行时它以 ai-op 身份、非提权启动
$a = New-ScheduledTaskAction -Execute 'C:\Windows\System32\cmd.exe' `
     -Argument '/c cd /d E:\.meine\.Proj_Soft\.Proj\.localAI && claude' 
$p = New-ScheduledTaskPrincipal -UserId 'ai-op' -LogonType Password -RunLevel Limited
Register-ScheduledTask -TaskName 'localai-ai-op-shell' -Action $a -Principal $p
# 注册时会问 ai-op 的密码;RunLevel Limited = 非提权(D46)
```

- ★ **设计上本期不该走这条**:§3.3 已裁定「客户端未运行时,写与 reveal 一律
  `ERR_NO_APPROVER`(fail-closed)—— 这条明确拒绝『无人值守批量运维』这个诱人但危险的形态」。
  计划任务是无人值守的天然形状。放这里只是为了写全。

---

## 6. 切过去会踩的坑

按「你多半会在头两小时撞上」的顺序排:

1. **★ git 凭据管理器是按用户存的。**
   Git Credential Manager 把凭据存在 Windows 凭据保管库里,**按用户隔离**。
   `ai-op` 第一次 `git push` 会重新要你认证 —— 而 `runas` 起的会话里
   GCM 的交互式弹窗**经常弹不出来或弹在错误的桌面上**。
   ⇒ 建议:在 `ai-op` 下改用 SSH key(`ssh-keygen` 生成在 `C:\Users\ai-op\.ssh`,
   公钥单独加到 GitHub),或者干脆**不让 `ai-op` 推送**,由你自己在本人会话里 review 后推。
   后者其实更符合设计:外部 AI 的产出应该经你过一眼再进历史。

2. **★ `%USERPROFILE%` 变了 ⇒ `.claude` 配置要重来一遍。**
   Claude Code 的用户级配置、认证、MCP 配置、权限 allowlist、
   以及 `C:\Users\<你>\.claude\projects\...` 下的会话记录与自动记忆,
   **全部在你自己的 profile 里**,`ai-op` 一样都看不到(而且现在是被 Deny 的)。
   ⇒ 切过去等于一个全新的 Claude Code 环境:要重新登录、重新配 MCP、重新攒 allowlist。
   仓库内的 `.claude\settings.json`(项目级)会跟着仓库走,那部分不用重配。
   **不要**为了省事把你自己的 `.claude` 拷过去 —— 里面有跨项目的会话记录与记忆。

3. **dotnet SDK 与 NuGet 缓存路径。**
   全机安装的 .NET SDK(`C:\Program Files\dotnet`)`ai-op` 能用(`Users` 组有读+执行)。
   但 NuGet 包缓存默认在 `%USERPROFILE%\.nuget\packages` ⇒ `ai-op` 有自己的一份,
   **首次 `dotnet build` 会把整套依赖重新下一遍**。
   `20-client-win` 的 obj/bin 也会重建。第一次编译慢是正常的。
   想共用缓存可以设 `NUGET_PACKAGES` 到一个两边都可写的目录 —— 但那等于开了一条
   两个身份共享可写目录的通道,**与形态 A 的初衷相反,不建议**。

4. **`node_modules` 权限。**
   仓库里已有的 `node_modules`(如果有)是你自己账户建的,文件所有者是你。
   `ai-op` 通过仓库子树的 Allow(Modify)能改它们,但**改不了它们的 ACL**
   —— create 脚本刻意**没给** `WRITE_DAC`(`verify` 有一条断言专门守着仓库根)。
   ⚠ 但别把这句话说大:Windows 上对象的**所有者**隐含拥有 `WRITE_DAC`,
   所以 `ai-op` 对**它自己新建的**文件/目录照样能改 ACL。
   这条真正保证的是它**改不了仓库根这个对象的 ACL**,因而拆不掉别处的 Deny。
   多数情况够用;遇到 npm 报权限错就整个删掉 `node_modules` 让 `ai-op` 重建。

5. **`ai-op` 是标准用户 ⇒ 装不了东西。**
   任何需要 UAC 的安装器在 `ai-op` 下会要管理员凭据。**不要把它加进 Administrators**
   —— 那样这套脚本的每一条 Deny 都立刻失去意义(管理员能改任何 ACL),
   `verify` 的 `★ ai-op 不在 Administrators` 会变红。
   ⇒ 正确做法:**你**在自己的账户下提权把工具装成全机可用,`ai-op` 只用不装。

6. **`drive-wide` 模式:祖先目录穿越。**
   盘根打了 Deny 之后,`E:\.meine`、`E:\.meine\.Proj_Soft`、`E:\.meine\.Proj_Soft\.Proj`
   都会继承到那条 Deny。create 会对每一级打一条「本文件夹」的读+穿越许可来放行。
   **少了这一步,`ai-op` 从盘根根本走不到仓库** —— 这是本模式头号「配好了却用不了」。
   `verify` 的第 ⑤ 节会逐级检查。

7. **`{state}` 下继承已断开的子目录。**
   `{state}\memory` 与 `{state}\secrets` 早就被 `icacls /inheritance:r` 断过继承。
   **打在 `{state}` 根上的 Deny 到不了它们。** create 会运行时扫出所有继承断开的子目录
   单独打;`verify` 的第 ③ 节会独立复查一遍。这是这类脚本的头号静默失效模式。

8. **首次施加会慢,别以为卡死了。**
   给容器加一条可继承 ACE,Windows 会把它传播到整棵子树。机主的 profile
   (几十万个文件)与 `drive-wide` 的两个盘根都属于「传播起来要一会儿」的那种。
   头一次跑 `create` 时某一步停住几分钟是正常的,**不要中断** —— 中断会留下半套 ACL,
   那时先跑 `verify` 看现状,再决定是重跑 `create`(幂等,先摘后加)还是 `revert`。

9. **客户端不受影响,但要注意它跑在谁下面。**
   WPF 客户端读写 `%LOCALAPPDATA%\LocalAI\client`,那是**你自己**的 profile
   —— 客户端仍然在你的会话里跑,不受这套 ACL 影响。
   反过来说:`ai-op` 下的 Claude Code **看不到也改不了客户端数据**,这正是设计要的。
   将来 `ctld` 落地后,层二对客户端数据的读写走 `ctld`(`ai-ctl` 账户),不走 `ai-op`。

---

## 7. ★ 诚实声明(不许省)

> **这套 ACL 挡的是 `ai-op` 这个身份,不挡机主自己。**
> 你在**自己的账户**下跑任何 AI(包括现在这一路会话)时,**形态 A 的保证不成立**。
> 那时层二退化成设计文档 §9-R1 / §9-R2 描述的样子:**问责与可复原能力,不是遏制**;
> 审计哈希链对它无效,因为链的写者与被防的主体是同一个身份。
> 这条不是可以靠"再加一层"补上的 —— 它是身份边界本身。

另外几条同样必须如实写,不得夸大:

| # | 说的是 | 强度 |
|---|---|---|
| 1 | **H2 的通配符禁令**(`**/*.key\|pem\|pfx\|credentials*\|secrets*`) | 🔴 **NTFS 没有按文件名通配的授权。** ACE 挂在对象上。这一条只能靠目录级 Deny 覆盖到的范围 + `ctld` 的编译期黑名单,**ACL 层做不到**。目录之外的散落密钥文件不在保护内 |
| 2 | **H9「外部 AI 永远不能改谁能读什么」** | 🔴 **形态 A 下不由 ACL 强制。** `10-core\gateway\registry.toml`、`10-core\gateway\gateway.py`、`config\*.toml` 都在仓库里,而仓库是 `ai-op` 的工作区。`-ProtectRepoConfig` 只盖住 `config\`,盖不住 `registry.toml`。这条现在靠 `ctld` 的编译期黑名单 + **你看 git diff** 强制 |
| 3 | **计划任务 / 注册表 Run 键** | 🔴 不在 NTFS 管辖内。全机启动目录已 Deny,但 `ai-op` 能在自己的 profile 与用户级任务计划里持久化。挡它需要组策略,本期不做 |
| 4 | **网关是 TCP,ACL 挡不住** | `ai-op` 能不能直连网关(127.0.0.1)由 `gateway.py` 的 `LOCAL_DENY_ACCOUNTS` 决定,**不由 NTFS 决定**。⇒ 这条不在本套脚本的强度之内,只是被它交叉核对。**当前已补齐**(`gateway.py:51` 含 `ai-op`),`verify` 第 ⑦ 节守着它;见第 8 节 |
| 5 | **本机管理员仍可推翻一切** | 与 `DEC:1822`(主机时钟篡改 = 本机管理员,判为 out-of-scope)是同一条边界。`ai-op` 不是管理员,所以这条对它成立;对你不成立 |
| 6 | **`verify` 验的是结构不是运行时** | 它验「拦截该在的地方在不在、算出来生不生效」,不验「以 `ai-op` 身份实际去读会失败」——后者需要该账户的凭据起进程,本套件不持有。与 `verify-isolation.ps1` 的诚实边界同口径 |

---

## 8. 与两层 MCP / Hermes 的关系,以及一条**马上会撞上**的接口约束

### 8.1 层级归属

- 本账户 `ai-op` 是**层二**(外部控制面)的宿主。它跑的是 Claude Code / Codex 这类第三方 AI。
- **Hermes Agent Worker(D65 / `HERMES_INTEGRATION_DESIGN.md`)是【层一的一个驾驶者】,不是第三层。**
  它在网关别名表里与 `llama-server` 同级(DD-1「Hermes 是模型不是服务」),
  它的工具池由 LocalAI 在会话建立时投喂(DD-2),**它拿到的工具池仍然受层一的全部禁区约束**
  —— 层一禁区是「池里不存在该工具」,穿过 worker 边界照样成立,因为池是我方挂载的。
  Hermes 跑在自己的隔离环境(DD-7:WSL2/Hyper-V,独立网络栈),**不跑在 `ai-op` 下**,
  与本套脚本无交集。
- 新档位 `agent-worker`(DD-4)落在**层一**那一侧:它是「哪台设备上的谁在问」的档位维,
  参与层一工具池挂载。而层二的 `ext-operator`(设计文档 §3.2)**不是第五个档位**,
  它对层一工具池的挂载结果**恒为空集**。两者不在同一维上,不要混。

### 8.2 ★ 主进程 M0a 已落地的东西给出的硬约束

主进程已经把这些改进 `10-core/gateway/`(本套脚本只读它们,不碰):

- `registry.toml` 现在**强制** `local_only`(bool)与 `agent_allow`(数组),
  取值必须来自 `gateway.py` 的封闭表 `KNOWN_AGENTS`,不许 `"*"`、不许空数组;
- `_check_local_only()`(`gateway.py:195`,调用点 `gateway.py:191`)六条 fail-closed,含**反向全表断言**;
- `LOCAL_DENY_ACCOUNTS`(`gateway.py:51`)现在是
  `{ai-asset, ai-exec, ai-vigil, ai-ctl, ai-op}`。

> ⚠ 行号取自 2026-08-03 17:xx 的工作树,`gateway.py` **此刻仍在被主进程并发修改** ——
> 引用时以符号名为准,行号只是路标。

⇒ **推论(会马上撞上的两条)**:

1. Hermes 将来加 `agent.default` 别名时,**必须同时补 `local_only` 与 `agent_allow`**,
   否则 `load_registry()` 抛 `RegistryError`,**网关拒绝启动**;
   且 `"agent-worker"` 必须**先进 `KNOWN_AGENTS`**,否则 `agent_allow` 里写它就被判为
   「含未登记的 Agent」而拒绝启动。这不是文档约定,是启动期的真实拦截。
2. **`ai-op` 必须在 `LOCAL_DENY_ACCOUNTS` 里 —— 已补上,但这条约束要长期守住。**
   **NTFS ACL 挡不住 TCP**:`ai-op` 能不能直连 127.0.0.1 的网关,由 `gateway.py` 决定,
   不由本套脚本的任何一条 Deny ACE 决定。理由与 §4.1-② 对 `ai-ctl` 的一样
   (「否则层二就是层一的一条旁路」),而且更硬:`classify_caller` 的兜底是
   「解析不到 → `trusted-local`」,**新账户默认落在放行侧**,而 `trusted-local` 恰好是
   唯一含 S2 读与 E1 解除权的档位 —— 建一个"受限"账户却不登记,等于发给它全系统最高档。
   > **状态(2026-08-03 复核时实测)**:主进程已把 `ai-op` 加进 `LOCAL_DENY_ACCOUNTS`
   > (`gateway.py:51`),并在其上留了大段理由注释。⇒ `verify-ai-op.ps1` 第 ⑦ 节现在
   > **PASS**;它在那条被从表里拿掉时会立刻变红。这不再是缺口,是一条**被断言守着的既有事实**。
   > (本套脚本仍然只读 `gateway.py`,不碰它。)

---

## 9. 回退

```powershell
.\revert-ai-op.ps1 -WhatIf        # 先看会摘哪些
.\revert-ai-op.ps1                # 只摘 ACE,账户保留
.\revert-ai-op.ps1 -DisableAccount
.\revert-ai-op.ps1 -RemoveAccount # 删账户;profile【不删】,移入 {state}\quarantine
```

- 顺序是硬的:**先摘 ACE 再动账户**。反过来会留下一地解析不出名字的孤儿 SID ACE,
  那些 ACE 仍然生效,而且以后没人看得懂它们是谁。
- `-RemoveAccount` 遵守铁律「**永不 delete**」:profile 目录**移入隔离区**,不删除
  (里面可能有外部 AI 干了两天的活)。注册表 `ProfileList` 的登记项脚本**不动**
  —— 那属于系统设置,请你在「系统属性 → 高级 → 用户配置文件」里自己清。
- 回退之后跑 `verify-ai-op.ps1` **应该看到大量 FAIL** —— 那是「禁区不再被 Deny」的
  如实反映,不是故障。
- revert **管不着**的:`ai-op` 配过的 git 凭据(随 profile 走)、它建过的计划任务、
  它装在自己 profile 下的工具链。收尾时脚本会提醒。

---

## 10. D 编号说明

取号前 grep 过:`00-docs/DECISIONS.md` 里**已提交**的最大编号是 **D64**(对抗式复核的收口),
**未提交但已写入**的是 **D65**(Hermes,另一路会话)。本目录**没有新立 D 决议**
—— 它是形态 A 的执行动作,归属设计文档里已经写好的那批决议。

⚠ 给后续会话:那份两层 MCP 设计文档内部把新决议编成了 **D64–D72**,与已占用的
D64 / D65 **撞号**,并入中央文档时必须重新取号。

**取号已由决议包定案(2026-08-03,晚于本 README 初稿)**:
`00-docs/decision-packets/two-layer-mcp-decisions-2026-08-03.md` 把它们扩写成六字段并编为
**D66–D75**(九条 + 新增的 D75「多会话并发写中央文档的纪律」),
`DECISIONS.md` 的「D65 补」也已按 D66–D75 对接。

★ **不是整体平移**,别按差值换算:

| 设计文档内部 | 决议包(定案) | 内容 |
|---|---|---|
| D64 | **D73** | 两层的形态与平面归属;部署采形态 A(`ai-op`) |
| D65 | **D74** | 外部控制面是提案编译器;**共用同一张路径黑名单表** |
| D66–D72 | D66–D72(编号不变) | ext-operator / 出境 / CFG-1 / Vigil / 事务 / 审计链 / 层间隔离 |
| —— | D75(新增) | 多会话并发写中央文档的纪律 |

⇒ 本目录第 2 节引的「共用同一张黑名单表」将来是 **D74**;本目录**未新立 D 决议**,
它是形态 A 的执行动作(归 D73)。决议包本身**尚未并入** `DECISIONS.md`,
真正采纳前编号仍可能再动 —— 所以脚本与本文一律**只引条文不引编号**。
D65 自己留的教训值得再抄一遍:「多会话并行时 D 编号是共享计数器,取号应以【已提交】为准」。
