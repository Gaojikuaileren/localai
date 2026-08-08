# `D?` · V14 任务 0:两堵墙的独立量测 —— **实测记录 + 一条待用户重裁的问题**

> 车道:V14(worktree `charming-mendeleev-39c982`,分支 `claude/v14-admin-app-phase1-bdcb84`)
> 日期:2026-08-08 · 基线 `main@c83bf73`
> **性质:量测记录 + 提问。本包一行生产代码都没改,也不改中央四文档。**
>
> ★ 依据:`AUDIT-2026-08-07-v11-toward-split.md` §4 第 1 条与第 5 条(V11 量的两堵墙),
>   由本车道**独立复核**(协调层此前没有复核过)。
> ★ 本包的结论里,**凡是实测的都给出了实测值**;凡是推理的都标着"推理"。两者不混。

---

## 0. 一句话结论

**两堵墙都成立,但它们的"致命程度"不一样,而 V11 的措辞把这一点抹平了:**

| 墙 | 成立? | 是否**绕不过** |
|---|---|---|
| ① nssm 服务 `.\ai-mem` 与中枢身份互斥 | ✅ 成立 | **绕得过** —— 它只在"选服务这条路"时才咬人,而这条路**从来不是必需的** |
| ② 管理员与普通用户都可进 | ✅ 成立 | **绕不过**(对"普通用户自己跑管理端 exe"这个读法而言) |

★ 而 ② 之所以绕不过,机制**不是** V11/审计所写的"完整性等级",**是账户配置文件边界** ——
  这个区别很要紧,因为它决定了"修法"长什么样。详见 §2.2。

---

## 1. 墙 ①:nssm 服务 `.\ai-mem` ⇄ 中枢身份

### 1.1 实测:服务模式确实是仓里的现成落地模式

`Get-CimInstance Win32_Service`(2026-08-08 实测):

```
Embedding     StartName=.\ai-mem   State=Running  Start=Auto
pg-mem        StartName=.\ai-mem   State=Running  Start=Auto
Qdrant        StartName=.\ai-mem   State=Running  Start=Auto
Qdrant-s2     StartName=.\ai-mem   State=Running  Start=Auto
```

⇒ 四个服务,全部以 `.\ai-mem` 跑,全部开机自启。**审计说的"现成模式"属实。**

### 1.2 实测:`ai-mem` 够不着中枢身份 —— **两层各挡一次**

**第一层 · 目录 ACL**(`Get-Acl D:\AI\state\identity`,实测全量 DACL):

```
NT AUTHORITY\SYSTEM              Allow  FullControl
BUILTIN\Administrators           Allow  FullControl
HONGKONGPINGPON\Zori Ma          Allow  Modify, Synchronize
OWNER = HONGKONGPINGPON\Zori Ma
```

⇒ **没有 `BUILTIN\Users`,没有任何 `ai-*`。** 审计原文属实,逐字对得上。
`ai-mem` 读不到 `hub.json` / `store.json` / `ca.cer`。

**第二层 · CA 私钥**:见 §2.2 —— 密钥文件在 `Zori Ma` 的配置文件里,`ai-mem` 同样够不着。

### 1.3 ★ 但这堵墙**不咬人** —— 因为服务那条路从来不是必需的

裁定要的是「**后台运行 + 开机自启**」。V11 把它读成了「所以要做成服务」。
**实测:仓里今天已经有一条现成的、跑着的、不用服务的落地方式。**

`HKCU:\Software\Microsoft\Windows\CurrentVersion\Run`(实测):

```
LocalAI : "E:\.meine\.Proj_Soft\.Proj\.localAI\dist\client\localai-client.exe" --tray
```

配套代码也齐全:`Program.cs:75` 认 `--tray`;`Autostart` 有 `IsEnabled/IsCurrent` 与
一组自检断言(`Selftest.cs:58-70`,含「自启命令带 `--tray`」「路径加引号」「exe 换位置后重写」)。

⇒ **管理端照抄这条路即可**:HKCU 自启 + `--tray` 起在**用户自己的会话**里,
   身份、密钥、ACL **全部原样可用**,一个字都不用动。

★★ **而服务那条路还有一个比身份更硬的毛病:服务跑在 Session 0,结构上不能显示任何界面。**
   而裁定第 2 条要求「双击管理端图标 ⇒ **正常显示界面**」。
   ⇒ 就算把身份问题解决了,**服务也满足不了这条裁定**。

> **⇒ 墙 ① 的正确读法不是"两者互斥所以做不了",而是"服务这条路本来就选错了"。**
> 不需要用户裁定,**按已有的 HKCU + `--tray` 先例做即可**。这条我不再问。

---

## 2. 墙 ②:「管理员和普通用户都可以进」

### 2.1 实测:三层拦点**逐层复核,三层全部属实**

| 层 | 审计原话 | 本车道实测 |
|---|---|---|
| 1 | `identity` 的 ACL 只有 Administrators/SYSTEM/机主,**无 `BUILTIN\Users`** | ✅ 属实,见 §1.2 全量 DACL |
| 2 | CA 私钥是**用户作用域**的不可导出 CNG/TPM 密钥(`Ca.cs` 里没有 `MachineKey`) | ✅ 属实,见 §2.2 |
| 3 | 打不开就 `return 3` | ✅ 属实:`20-client-win/app/Program.cs:65`(设备密钥)· `10-core/lan-edge/Program.cs:260`(CA 密钥) |

### 2.2 ★★ 第 2 层的机制要改一个字:**不是"完整性等级",是"账户配置文件"**

**源码**(`10-core/identity/Ca.cs:46-52`):`CngKeyCreationParameters` 只设了
`Provider` / `ExportPolicy` / `KeyUsage` —— **没有 `KeyCreationOptions`**
⇒ 默认 `CngKeyCreationOptions.None` = **CurrentUser 作用域**。
文件开头第 6 行自己就写着这是 D43 S0.10「精简优先」的**有意选择**,
且「signer-service-account ACL isolation 是 **P3b.2**」—— 也就是**当时就知道、并推迟了**。

**实测**(在我这个进程里打开真实的那把 CA 密钥):

```
Exists('localai-ca-f6hsduipeesexb6f', 'Microsoft Platform Crypto Provider') = True
OPEN OK: Algorithm=ECDSA
UniqueName = C:\Users\Zori Ma\AppData\Local\Microsoft\Crypto\PCPKSP\
             b1d4f6bb.../de610ac3....PCPKEY
```

★★ **这是物证,不是推理:CA 私钥的容器就是一个躺在 `Zori Ma` 用户配置文件里的文件。**
   机器作用域的目录 `C:\ProgramData\Microsoft\Crypto\PCPKSP\` 下**只有** Windows 自己的
   `WindowsAIK` / `WindowsEK`,**没有我们的密钥**。

那个文件的 ACL(实测):

```
APPLICATION PACKAGE AUTHORITY\软件和硬件证书或智能卡  Allow  Read, Synchronize
NT AUTHORITY\SYSTEM                                   Allow  FullControl
BUILTIN\Administrators                                Allow  FullControl
HONGKONGPINGPON\Zori Ma                               Allow  FullControl
```

⇒ 一个**非管理员**的普通用户,**既不在这张表里,也不属于 Administrators** ⇒ **结构上打不开**。

**★ 为什么要纠正"完整性等级"这个说法:**
本机 `EnableLUA = 0`(实测),`Zori Ma` 是管理员 ⇒ **它的进程恒为 High**,
没有 split token、没有 Medium 那一档。我这次是在 `High Mandatory Level`(实测)下
**成功打开**了这把密钥 —— 说明在这台机器上**完整性等级根本不是拦点**。
拦点是**账户**。

> 这正是 `ASSERTION-PITFALLS` 第 9 条那族("判据问的是我是什么身份,而不是我做不做得到")。
> 好消息:**第 3 层的两处 `return 3` 已经是对的写法** —— 它们真的去开一次密钥、
> 拿开不开得了当判据(`CaKeyUsable` / `DeviceKeyUsable`),不是问 `IsInRole`。
> ⇒ 第 3 层不是缺陷,它是在**如实转述**第 1、2 层。

### 2.3 ★★★ 决定性的一条:**GUI 不能跨会话**,所以两个读法只剩一个

「都可以进」有两个读法,可行性**完全相反**:

**读法 A —— 普通用户自己跑管理端 exe**(进程以那个用户身份运行)
- 第 1 层:读不到 `identity` 目录 ⇒ ✗
- 第 2 层:打不开 CA 私钥 ⇒ ✗
- ⇒ 而拆分包 §0 第 2 条把「**发身份**」划给了管理端 —— 发身份就是拿 CA 私钥签名。
- ⇒ **读法 A 在今天结构上不可能。**

**读法 B —— 管理端只跑在机主会话里,普通用户"够到"它**
- 网关**已经**支持:`caller_identity` 从 TCP 连接反查调用方真实 OS 账户
  (源端口 → PID → 令牌 SID → 账户名),再查 `config/caller-accounts.toml` 的 allowlist 定档。
- ⇒ 把第二个账户显式加进 `[caller].trusted_local` 即可,**不用动密钥、不用动 ACL**。
- ★ **但**:管理端是一个 **WPF 窗口程序**。Windows 自 Vista 起**会话隔离** ——
  跑在机主会话里的进程**不能在另一个用户的桌面上显示窗口**。
- ⇒ 普通用户要**看见**管理端界面,那个进程就必须跑在**他自己的会话**里 ⇒ **退回读法 A** ⇒ ✗

> ### ★★★ 所以真正的结论是:
> **只要管理端是一个"有窗口的桌面程序",「普通用户也能进」就要求它以那个用户的身份运行;
> 而那个身份打不开中枢身份。这两件事今天在 Windows 上无法同时成立。**
>
> 这不是设计没想周到,是**身份模型 + 会话隔离**两条 OS 性质叠出来的。

### 2.4 ★★ 而且:今天这台机器上**根本没有那个"普通用户"**

`config/caller-accounts.toml` 里有一条**已经裁定过**的记录(2026-08-03,用户当时答的):

```
Alle → 【不登记】 ✅ 已裁定(2026-08-03,用户答):访客账户,无管理员权限,
                    不是家庭成员的登录账户 ⇒ 不进 trusted_local。
                  ★ 将来若第二位家庭成员要用客户端,那是【另一个账户】,
                    届时须显式加进上面的 trusted_local 并留一条决议。
```

实测本机账户:非管理员的**真人**账户只有 `Alle` 一个(`Get-LocalUser` + `Get-LocalGroupMember`),
而它 2026-08-03 已被裁定为**访客、明确排除**。其余 `ai-*` 是服务账户,
`CodexSandbox*` / `WsiAccount` 是沙箱/子系统账户,`Administrator`/`Guest` 等均 Disabled。

> ⇒ **2026-08-07 那条「管理员和普通用户都可以进」,今天没有任何一个真实用户能去验它。**
> 它说的是一个**将来的第二位家庭成员**,而 2026-08-03 那条裁定已经写明
> 「那是另一个账户,届时须显式登记并留一条决议」。
> **两条裁定不矛盾,但 08-07 那条在今天是【没有对象】的。**

---

## 3. ⇒ 交给用户的问题(本包**不替用户选**)

墙 ② 绕不过。可走的路只有三条,代价差别很大:

| 路 | 做法 | 代价 | 谁受影响 |
|---|---|---|---|
| **甲** | **把「都可以进」降为「只有机主能进」** | **零** —— 今天就是这样,且今天没有第二个真人账户 | 无。第二位家庭成员将来要用时再单独裁 |
| **乙** | CA 私钥改**机器作用域**(`CngKeyCreationOptions.MachineKey`)+ `identity` 目录放开 ACL | **要重铸 hub 身份 + 全部设备重新配对**(`paths.toml` 白纸黑字:identity 缺失 ⇒ 新 hub_id + 新 CA + 全量重配)。且**削弱**一条安全性质:私钥从"只有机主够得着"变成"本机多个账户够得着" | 两台真 PC 全部要重配对 |
| **丙** | 管理端**只跑在机主会话**;第二个用户用**客户端**经回环够管理面,按 allowlist 定档 | 中等:要新增跨进程契约(D95 记账),且第二用户看到的**不是管理端界面**,是客户端里的一块 | 与裁定第 5 条「副机没有打开管理端面板的按钮」需要一起想 |

**我的建议:走【甲】。** 理由三条:
1. 今天**没有对象** —— 没有第二个真人账户,08-03 已把唯一的候选裁成访客;
2. 【乙】要用**全量重新配对**去换一个**今天没人用**的能力,而它还削弱安全性质;
3. 【丙】要为同一个"今天没人用"的能力**增加一条契约欠债**(现在 1/30,收工要求仍是 1)。

★ 走【甲】不是"绕过去",是**如实缩小裁定的范围并留痕**:
  管理端**只有机主能进**;将来第二位家庭成员真的要用时,那是一次**新的裁定**,
  届时在【乙】【丙】之间选,而那时**有真实对象可以验**。

### 3.1 ✅ 用户已重裁(2026-08-08)——【**甲**】

> **2026-08-07 那条「管理员和普通用户都可以进」,由用户于 2026-08-08 当场重裁为
> 「**只有机主能进**」。**

⇒ 本车道按【甲】实现。**连带确定的三件事:**

1. 管理端**只以机主身份、在机主的交互会话里运行** —— 不做服务、不跨会话;
2. 中枢身份的作用域**一个字不动**(CA 私钥仍是 CurrentUser/TPM、`identity` 目录 ACL 不放开)
   —— 【乙】那条"全量重新配对"的代价**不用付**;
3. **不新增**任何为第二用户准备的跨进程契约 —— 契约总数保持 **30**、欠债保持 **1/30**。

★ **留给将来的钩子**:第二位家庭成员真要用时,`config/caller-accounts.toml` 的
  `[caller].trusted_local` 就是那个登记点(该文件 2026-08-03 那条注释已经写明了这一点)。
  **届时是一次新裁定,不是把本条改回去。**

---

## 4. 本包**不**声称

- **不声称**墙 ① 不存在 —— 它存在,只是**选服务那条路才会撞上**,而那条路满足不了裁定第 2 条;
- **不声称**读法 B 在**非 GUI** 场合也不行 —— 网关那条回环 + allowlist 的路**是通的**,
  不通的是"**让另一个用户看见一个窗口**";
- **不声称**【乙】做不到 —— 它做得到,代价是全量重新配对 + 一条安全性质变弱。**那是用户的账,不是我的**;
- **不声称**已经问过第二位家庭成员是否存在 —— 我只测了**本机账户**。若用户说"马上就要加一个人",
  【甲】的前提就不成立了,那要重算。

---

> V14 · 2026-08-08 · 基线 `main@c83bf73` · **零生产代码改动**
> D 号待用户裁定后由 V0 在并入 `DECISIONS.md` 那一刻分配(当前最大 **D107**)
