# D? · 证书生命周期闭环:设备证书续签 · 自动轮换 · 第四种归因 · B16 词表

> 车道:core/identity(worktree `core-identity-certlife`,分支 `claude/core-identity-certlife-d8fbd5`)
> 日期:2026-08-05
> 状态:**草案**。D 号按 D75/D82 在并入那一刻才取,本文一律写 `D?`。
> 前置:D49(服务器证书必须可续签)· D43/D44(密钥形态)· D48(管理面仅回环)· D82(车道纪律)
> 落地依据:`decision-packets/P3b_LAN_mTLS_Decision_Packet_2026-07-28.md` §6.2 / §6.3(续期状态机,原推迟至 P3b.2)

---

## §0 勘察:动手之前先把四件事查清(实测,不是推断)

### (a) 实机服务器证书还剩多久 —— **23 天,且从未被续过**

`D:\AI\state\identity\server.cer`:`NotAfter = 2026-08-28T15:14:18+02:00`,
文件 `LastWriteTime` 仍是 `2026-07-29`(= `init` 那一刻)⇒ **D49 之后七天里没有任何人跑过一次 `status`**。
D49 记录的到期日准确。

> ★ 这一条本身就是任务 2 存在的理由:D49 给的是「手动命令 + `status` 里 <10 天提示」,
> 也就是**要有人记得去看**。七天的实测结果是:没有人看。

### (b) 设备证书续签路径 —— **代码确实没有,但设计早就有了**

全仓 grep 只有 `renew-server`(服务器叶证书)。设备证书 90 天(`Pairing.cs:195` `days: 90`)、无任何续签路径,属实。

**但需要更正一处口径**:这不是"从没设计过"。P3b 决策包 §6.3 已经把四路由续期状态机
(`/identity/renew/enroll|status|claim|complete`)写完了,是 `PROJECT_PLAN_v3.0.md:2183`
把它**推迟到了 P3b.2**(与 B16 词表同一行)。本次是把那份既有设计落地,不是另起炉灶。

### (c) ★ **用户的假设错了,而实情更糟**

原假设:设备证书过期 ⇒ 落到 `CertExpired` ⇒ 文案说"主机证书已过期,去主机上续签" ⇒ 指错方向。

**实测四例**(在场证据):

| 情形 | 客户端拿到的异常链 | `ClassifyTlsFailure` 判成 |
|---|---|---|
| 有效设备证书 | HTTP 200 | — |
| **本机设备证书过期** | `HttpRequestException` → `IOException` → `Win32Exception`:「证书链是由不受信任的颁发机构颁发的。」 | **null ⇒ `Offline`** |
| 不带客户端证书 | HTTP 401 | — |
| 主机服务器证书过期 | `AuthenticationException`:「…certificate chain: **NotTimeValid**」 | `CertExpired` ✅ |

⇒ 用户看到的**不是**"主机证书已过期",是「**中枢没开机**」。他会一趟趟跑去主机重启 Edge、
查防火墙、改拨号地址 —— 而主机完全正常。

**两个独立根因:**

1. 异常链里**根本没有** `AuthenticationException` —— TLS 1.3 下服务端的 alert 在首次读时才到,
   包成 `IOException`,`ClassifyTlsFailure` 的兜底那一轮扑空;
2. Win32 层那句话是**本地化**的(本机中文),而判据是英文针。
   ★ **换英文机器也不行**:英文原文是 "The certificate chain was issued by an authority that is not trusted.",
   里面没有 `UntrustedRoot` 这个词。

⇒ `HubState.HubIdentityChanged` 在 Win32 层**结构上永远点不着**。
这是 ASSERTION-PITFALLS 第 8 条(判据依赖非 ASCII / 环境相关字符串),
只不过这次踩在**生产代码**里,而不是断言里。

**★ 还有更坏的一层**:Windows 把这次失败报成「不受信任的颁发机构」,而真正的原因是**时间**
(服务端 `X509Chain` 判的是 `NotTimeValid`)。所以就算有人把针本地化了,它也会把这次失败
判成 `HubIdentityChanged` =「必须重新配对」—— 而重新配对会删掉本机私钥。
**照着这条错误线索走,结局是亲手销毁一个只需要续签的身份。**

### (d) ★ **顺序与用户猜的相反:过期先命中,而且它连 HTTP 状态码都没有**

成员表 `active` 校验**挡不到**过期证书:
`ValidateClient` → `Ca.VerifyChainAndEku` → `X509Chain.Build` 在 **TLS 握手层**就判 `NotTimeValid`
(实测 `ChainStatus = NotTimeValid`),连接当场断,`MapFallback` 里那句 `Store.IsActive(fp)` **一次都跑不到**。

| 判据 | 在哪一层 | 用户/客户端拿到什么 |
|---|---|---|
| **证书过期** | TLS 握手层(先) | **什么都没有** —— 连状态码都没有 |
| **被吊销** | 应用层 `MapFallback`(后) | `401 {"error":{"type":"lan_device_unknown"}}` |

⇒ 两者不但处置相反,**可归因性差一个量级**。这正是设备证书过期难查的结构性原因,
也是 (c) 里那条错误归因的土壤。

> 这两条已钉成常驻断言(lan-edge selftest 甲节),包括一条**反向**断言:
> 若哪天过期校验被挪到应用层、变成 401(可归因性变好),那条断言会**红给人看**,
> 提示客户端的归因逻辑必须同步改 —— 而不是默默通过。

### (e) ★★ 2026-08-06 追加:`HubIdentityChanged` **不是**死路径,而本包初稿的修法会把它弄死

08-05 收工时我提出「`HubIdentityChanged` 可能从写下来就没生效过」。**这个怀疑是错的**,
已由 8 路并行实测(6 个场景 + 4 个补充 + 3 路对抗复核)推翻。如实更正:

**1. 它是活的。** 老判据在**全部六种**身份失效场景里都会判出 `HubIdentityChanged`。
我 08-05 的结论只在 **Win32 那一层**成立(= 服务端拒绝客户端证书那条路径,S9),
而我把它写成了泛化结论。**范围错了。**

**2. .NET 在这条路上发两种形状的消息** —— 这是全部误判的机械原因:

| 条件 | 消息 | 含链状态词? |
|---|---|---|
| 只有链错误 | `...invalid because of errors in the certificate chain: PartialChain` | ✅ 有 |
| **还有名字不匹配** | `...invalid according to the validation procedure: RemoteCertificateNameMismatch, RemoteCertificateChainErrors` | ❌ **一个都没有** |

**3. 而"主机重铸身份"必然落在第二种形状。** `Identity.Init` 用 `Guid.NewGuid()` 铸 hub_id,
CA 名与 `server_name` 全由它派生 ⇒ 重铸**同时**换掉 CA 和 `localai-<short>.local` 名字
⇒ 名字必然不匹配 ⇒ 消息里**没有**任何链状态词。
⇒ 只认 `UntrustedRoot`/`PartialChain` 的判据,在**唯一真正需要它的那一刻**全部落空。

**4. 老代码在这一格是靠那条"该删的兜底"答对的。** 而兜底确实该删:实测拨到一个跑普通 HTTP 的地址
(路由器/NAS 管理页,或 DHCP 把旧地址分给了别人)时,异常是
`AuthenticationException: Cannot determine the frame size or a corrupted frame was received.`,
兜底会判成「必须重新配对」—— 而重新配对**先删本机私钥**。为一个填错地址的问题销毁有效身份。

**⇒ 结论:兜底删,同时加名字不匹配分支,且它必须排在 `NotTimeValid` 之前。**
(消息可能同时带 `NotTimeValid, PartialChain`;先判过期会给出"续签即可"的建议,
而链都不通了,续签一万次也没用。)

**5. 另外两处事实更正(本包 §0(c) 初稿写错了):**
- 签发者不在 `CustomTrustStore` 时链状态是 **`PartialChain`**,不是 `UntrustedRoot`;
  后者只在对方出示**自签名**证书时出现;
- `TlsFailure.cs` 原注释把"异常链里没有 `AuthenticationException`"归因于 **TLS 1.3 的 alert 晚到**,
  **这个解释是错的** —— 把两端钉到 TLS 1.2 复测,形状一模一样。已更正。
  判据可靠的真正原因是:消息里嵌的是 `SslPolicyErrors` / `X509ChainStatusFlags` 的**枚举名**,
  枚举名不随系统语言变(而 .NET 自己那句话装了语言包**是会**被翻译的)。

**6. 两条死代码(顺带查实,均不影响结论):**
- `HubClient.cs:268` 的 `if (e is X509ChainStatusFlags) break;` —— `X509ChainStatusFlags` 是**枚举**
  (值类型),`Exception` 引用永远不可能是它;编译器出 `CS0184` 警告,该 `break` **不可达**;
- 我自己在 `TlsFailure.cs` 里也写了一条同类死代码(`for(...) if (e is AuthenticationException) return Unknown;`
  与直接 `return Unknown` 等价),已删。

### 另外更正一处事实

用户说「两台机器 2026-07-29 配的对」。**store.json 实证:那两条(`9bd80666` / `9c412f1c`)都已 revoked。**
现役两台是 **2026-08-04** 配的(`b8aa7667` / `ac68df07`),证书到期 **2026-11-02**,不是 10-27。
真实截止日比原估计**晚 6 天**。

---

## §1 裁定

### 1.1 设备证书续签:**两条路由,不经过配对流程**

`10-core/identity/Renewal.cs` + lan-edge 两条路由:

| 路由 | 授权(全在 TLS 层) | 作用 |
|---|---|---|
| `POST /identity/renew/enroll` | 出示**当前 active 的旧证书** | 验新 CSR PoP,签出候选证书(同一 `device_id`) |
| `POST /identity/renew/complete?renewalId=` | 出示**正好是这次签出的候选证书** | 原子切换:新 → active、旧 → superseded、generation++ |

**为什么不走配对流程**:六词 SAS 解决的是「首次建立信任」。续签时信任早就建好了 ——
同一个 `device_id`、同一个 CA、同一把私钥。走配对会:① 要人守在主机前批准(正是"靠人记得的护栏不是护栏");
② 换 `device_id`,设备列表越堆越长;③ 重新生成密钥,销毁既有身份。

**★ 承重的顺序**:`complete` 之前**绝不退休旧证书**。`complete` 的全部意义是
「在退休旧证书之前,先证明新证书真的握得上手」。反过来做,任何让新证书用不上的原因
(SChannel 拿不到凭据句柄、证书没落进存储、时钟偏差)都会让设备**在续签成功的那一刻掉线**。

### 1.2 对 §6.3 的两处**有意偏离**(记账,不是疏漏)

| 原设计 | 本次 | 理由 |
|---|---|---|
| 四条路由(enroll/status/claim/complete) | **两条**(enroll/complete) | status+claim 的前提是"签发可能异步、要等"。本实现 registry 与 signer 同进程、签发同步,enroll 当场就能给出候选。硬留两条永远立刻返回 `candidate_ready` 的路由,是在协议上**伪造一个并不存在的等待状态** |
| 「生成新的 TPM key + CSR」(§6.2) | **复用同一把设备私钥** | 与 D49 服务器证书续签同口径(「不换密钥 = 不触碰任何已建立的信任」)。**代价如实记账:私钥不轮换。** 收益:崩溃重入的状态空间小一个量级,且不会留孤儿 CNG 密钥(那个坑 `Pair()` 已踩过一次) |

★ 要改回密钥轮换须**另立决议**。

★ §6.3 要求「初次配对与续期使用不同状态表、context 和路由授权」——
本实现用**两条独立路由 + 两张独立状态表**满足它。**没有**再加一层挑战签名:
复用同一把密钥之后,"用同一把私钥签个串"证明不了任何 mTLS 没证明的事,
只会多一段看起来很安全、实则恒真的仪式。

### 1.3 自动轮换:**没有自己的持久状态**

`ServerCertRotator` 每一跳重读 `server.cer` 自己的 `NotAfter` 来决定要不要动手,
**没有进度文件、没有"正在续签中"标志位**。

这不是省事,是唯一能让「崩溃后重入不留半套状态」成立的形状:
只要存在第二份状态,它和证书本身就有对不上的可能,而对不上的那一刻恰好是崩溃之后 —— 没人盯着的时候。
反过来,**证书自己就是进度**:`NotAfter` 变新了就是续成功了,没变就是没续成。没有第三种可能。

**fail-closed 的三条**:
- 续签抛错 ⇒ `Failed`,累计失败次数 + 最后错误留在 `Status` 里,Edge 打横幅、`/admin/ping` 吐出去;
- 失败后**继续重试**,达到 `FailuresBeforeLoud` 只是喊得更大声 —— **停止重试 = 静默退回手动**;
- ★ **续签"没抛错但到期时间没前进"也判 `Failed`** —— 不复核的话,一个什么都没做的续签会被记成成功,
  然后一路静默滑到证书真的过期。

**不中断已有连接**:Kestrel 改用 `ServerCertificateSelector`(每次新握手时调用),
续签后换掉 `Edge.CurrentServerCert` 即可 —— 新连接拿新证书,**已建立的 TLS 连接一条都不受影响**。
D49 当时给的是「请重启 Edge 以加载新证书」,而重启会掐断 300 秒的流式聊天连接。

### 1.4 B16 curated SAS 词表(v0 占位 → v1)

`localai-sas-wordlist-v1-en2048`,2048 词,判据全部由断言反向钉死:
恰好 2048、互不相同、3–8 位小写 ASCII、**前 4 字母互不相同**、**不含同音词对**。

> ★ 同音词是这张表最要命的缺陷:念出口比对是它的**主要**用法,而 `right`/`write` 听起来一模一样
> ⇒ 一次真正的不一致会被听成"对上了"。已逐对剔除 `right/write`、`pair/pear`、`peace/piece`、`wear/where`。

**版本与内容机械绑定**:`KnownVersions` 是 `(版本, 内容 SHA-256)` 冻结对照表,只增不删。
改任何一个词 ⇒ 摘要对不上当前版本那一行 ⇒ **当场判红**;想变绿只能新加一行版本。
⇒ **版本字符串无法不跟着变。**(已红测验证:改一个词 `zebra→zebroo`,该断言立刻红。)

**「换表只改显示的词,不改索引、不改安全性」的硬证据**:
一条**冻结索引向量**断言(固定 transcript → `274,9,590,909,1496,1156`)。
索引由 HKDF 推出、与词表无关,所以换表时它必须一位不变。
> 取值可信度:换表当天 `Sas.cs` 与 HEAD **逐字节相同**(`git diff --quiet HEAD -- Sas.cs` 已验),
> 且索引在 `Wordlist` 被用到**之前**就算完了 ⇒ 这六个数确是换表前那份代码的输出,不是"照着新代码抄的"。
> ★ 上面那次红测里,这条断言**保持绿色** —— 是"换表没碰索引"的现场证据,不是推断。

---

## §2 ★★ 要 client 车道改的:逐行清单

> 按 D82,越界的唯一合法方式是在决议包里写清要动别人哪一行、为什么。
> 以下**全部**位于 `20-client-win/app/**`,core 车道一个字都没有动。

### 2.1 `20-client-win/app/localai-client.csproj` —— **先加这一条,否则下面全都编不过**

第 28–36 行是**显式** `Compile Include` 名单(不是通配),新文件不会自动进来。
在第 36 行 `<Compile Include="..\transport\ClientTransport.cs" />` **之后**追加四行:

```xml
<Compile Include="..\transport\TlsFailure.cs" />
<Compile Include="..\..\10-core\identity\CertLifecycle.cs" />
<Compile Include="..\..\10-core\identity\Renewal.cs" />
<Compile Include="..\..\10-core\identity\ServerCertRotator.cs" />
```

*为什么*:`TlsFailure`/`CertLifecycle` 是下面所有改动的依赖。
(lan-edge 与 transport 两个 csproj 已由本车道同步加好。)

### 2.2 `Services/HubClient.cs:26` —— 枚举补第四态

```csharp
public enum HubState { NotPaired, Connecting, Online, Offline, Revoked, CertExpired, Unauthorized,
                       ProtocolMismatch, HubServerError, HubIdentityChanged, LocalCertExpired }
```

*为什么*:见 §0(c)。四种症状都是"连不上",而处置各不相同;
`LocalCertExpired`(本机设备证书过期)这一格此前**是空的**,它的实际归宿是 `Offline` =「中枢没开机」。

### 2.3 `Services/HubClient.cs:264–283` —— `ClassifyTlsFailure` 整个换成调用 transport

把方法体换成:

```csharp
static HubState? ClassifyTlsFailure(Exception ex, ClientProfile? profile) =>
    TlsFailure.Classify(ex, profile, DateTimeOffset.UtcNow) switch
    {
        TlsFailureKind.LocalDeviceCertExpired => HubState.LocalCertExpired,
        TlsFailureKind.ServerCertExpired      => HubState.CertExpired,
        TlsFailureKind.HubIdentityChanged     => HubState.HubIdentityChanged,
        _ => null,                              // ★ 判不出来就【别猜】
    };
```

*为什么*:
- 现有实现靠**英文异常文本**认因,实测对"本机设备证书过期"完全失灵(§0(c));
- `TlsFailure.Classify` **先查本机证书这个本地事实**,再去解读会漂移的异常文本 —— 顺序是承重的;
- ★ 现有的兜底 `if (e is AuthenticationException) return HubIdentityChanged;`(第 280–281 行)**必须删掉**,
  但**必须连同 §0(e) 的名字不匹配分支一起换**,不能只删不换 —— 见下。

> ### ⚠ 2026-08-06 更正:只删兜底会**制造**一个比原病更重的回归
>
> 本节初稿只说"删掉兜底"。多路实测(§0(e))证明:那样做会让**主机重铸身份**这一格
> 掉进 `Unknown → Offline` =「中枢没开机」—— 而那正是 `HubIdentityChanged` 这一态**存在的理由**。
> 老代码在那一格是靠**被判死刑的这条兜底**答对的。
>
> 兜底**仍然要删**(实测它会在"拨到一个非 TLS 服务"时判出破坏性的"必须重新配对"),
> 但**同时必须加名字不匹配分支**。`TlsFailure.Classify` 已按此修正(commit 见 §3),
> client 车道**直接调用它即可**,不需要自己再写判据。
> ★ 若有人只照初稿删掉兜底而不换判据,**回归会当场发生且没有任何断言拦得住它** ——
> 所以 transport selftest 已补了 5 条针对**实测原文**的断言把这一格钉死。

### 2.4 `Services/HubClient.cs:216` —— 调用点跟着改

```csharp
State = ClassifyTlsFailure(ex, Profile) ?? HubState.Offline;
```

### 2.5 `Services/HubClient.cs:219–220` —— 文案分家

第 219–220 行现在的 `CertExpired` 文案说的是"**主机**证书已过期"。保留它(它现在**只**表示主机侧),
并新增一条 `LocalCertExpired` 分支:

```csharp
HubState.LocalCertExpired => TlsFailure.Explain(TlsFailureKind.LocalDeviceCertExpired),
```

*为什么*:该文案明确劝阻「重新配对」—— 重新配对会删掉本机私钥,把一个**只需要续签**的身份亲手销毁。

### 2.6 `Services/HubClient.cs:286` `ProbeAsync` —— 连之前先自愈,并提前告警

在 `CallAsync("/v1/models")` **之前**插入:

```csharp
if (Profile is not null && TryDial() is { } ep)
    await Transport.RenewDeviceCertIfDue(Profile, ep, AppPaths.StateDir, DateTimeOffset.UtcNow);
```

并新增一个供界面读的提示(**只在 Critical/Expired 时才显示**,`RenewDue` 不打扰用户):

```csharp
public string? CertWarning => TlsFailure.LocalCertPhase(Profile, DateTimeOffset.UtcNow) is { } ph
    && CertLifecycle.ShouldAlarm(ph) ? "本机设备证书即将到期,正在自动续签…" : null;
```

*为什么*:任务要求「过期**之前**就要看得见,而不是握手失败之后才归因」。
★ `RenewDue` 段**不得**告警 —— 那段系统正在正常自愈;一个正常运转也报警的系统,
两周内就会被学会忽略,于是真出事那次也没人看。

### 2.7 建议(非必须):设备页显示主机侧轮换状态

`/admin/ping` 现在多回一个 `serverCert { daysLeft, phase, consecutiveFailures, lastError, needsAttention }`。
`needsAttention == true` 时应当在主机端界面显著提示 —— 这是自动轮换 fail-closed 的**最后一段路**:
状态已经吐出来了,但没有界面读它,等于没响。

---

## §3 改了哪些文件 · 断言数

| 文件 | 动作 |
|---|---|
| `10-core/identity/CertLifecycle.cs` | **新增** — 两侧共用的生命周期判据(纯函数,注入时间) |
| `10-core/identity/Renewal.cs` | **新增** — 设备证书续签状态机 |
| `10-core/identity/ServerCertRotator.cs` | **新增** — 自动轮换(无持久状态、fail-closed) |
| `10-core/identity/Wordlist.cs` | 重写 — B16 v1 词表 + 版本/摘要绑定 |
| `10-core/identity/Ca.cs` | `IssueLeafWindow`(显式有效期窗口)· `VerifyChainAndEku` 可传校验时刻 |
| `10-core/identity/Store.cs` | `CompleteRenewal` · `SweepStaleRenewalCandidates` · `DeviceIdOfCert` |
| `10-core/identity/Program.cs` | selftest3 / selftest5 扩充 |
| `10-core/lan-edge/Program.cs` | 两条续签路由 · 热换证 · 轮换循环 · `/admin/ping` 扩充 · selftest 扩充 |
| `20-client-win/transport/TlsFailure.cs` | **新增** — TLS 失败四分类 |
| `20-client-win/transport/ClientTransport.cs` | 续签驱动器(崩溃重入)· 词表版本归因 |
| `20-client-win/transport/Program.cs` | 测试 Edge 加续签路由 · selftest 扩充 |
| 两个 csproj(lan-edge / transport) | 加新源文件 |

**门禁数字(`run-tests.ps1 -Full`)**

| 套件 | 前 | 后 |
|---|---|---|
| identity selftest / 2 / 3 / 4 / 5 | 11 / 15 / **28** / 14 / **13** | 11 / 15 / **42** / 14 / **57** |
| lan-edge selftest | 8 | **20** |
| transport selftest | 5 | **37** |
| Python 18 套件 | 978 | 978(未触碰) |
| **合计** | — | **PASS=1174 FAIL=0** |

净增 **+102** 条断言(其中 08-06 修回归时 +5)。

**红测(证明断言不是恒绿的)**
1. 词表改一个词(`zebra→zebroo`)⇒ 摘要断言**红**,冻结索引断言**保持绿**(= 换表没碰索引的现场证据);
2. 摘掉 `TlsFailure.Classify` 里"先查本机证书"那两行 ⇒ `LocalDeviceCertExpired` 断言**红**;
3. (08-06)摘掉名字不匹配那一根针 ⇒ **恰好** S1/S3 两条身份断言**红**,其余全绿。
三次都已还原并逐字节核对。

**★ 一条方法论教训(值得进 ASSERTION-PITFALLS,但那份文件本车道只读)**
原来那条 `HubIdentityChanged` 断言用的针是我**凭印象手写**的
`"The remote certificate is invalid: UntrustedRoot"` —— 一句 .NET **从来不会发出**的话。
判据在真实消息上失灵,而断言拿虚构输入喂它,照样绿。
⇒ **凡是"判据要认某段外部文本"的断言,针必须来自实测输出,不能凭印象写。**
这是 ASSERTION-PITFALLS 现有条目没覆盖的一种假绿:不是判据写宽了,而是**输入是假的**。

---

## §4 没做的,和为什么没做(★ 跑不了和跑过了必须长得不一样)

1. **`client --selftest` 未跑** —— 原因:worktree 是全新 checkout,`bin/` 不进 git,
   客户端产物从来就不在这儿。**我未改动 `20-client-win/app/**` 一个字节**,
   且按用户指示不去出包、不改 `run-tests.ps1`。这是**覆盖缺口**,不是通过。
2. **§2 的客户端改动全部未实作** —— 归 client 车道(D82)。
   ⇒ 在 client 车道并入之前,`LocalCertExpired` 这一态**在实机上仍然不会出现**,
   设备证书过期依旧显示「中枢没开机」。**本包只把能力和判据备齐,没有改变实机行为。**
3. **设备证书自动轮换在实机上还没有触发器** —— 客户端侧的 `RenewDeviceCertIfDue`
   已实作并端到端测过,但**它的调用点在 `ProbeAsync`(§2.6),属客户端车道**。
   主机侧的服务器证书轮换循环已接进 `run-lan`,是活的。
4. **未在实机上跑过任何一次真的续签** —— 全部证据来自 scratch 目录里的自检。
   实机 hub 的服务器证书**没有被本次改动碰过**(仍是 2026-08-28 到期)。
   ★ 实机验证需要 D46 的签名 `.cmd` 交用户双击(agent 进程提权,TPM 密钥会绑到错的完整性级别),
   本次**未做**。
5. **任务 4(三服务分权 / 7 步 activation saga)未做** —— 前三项吃满了。
6. **私钥不轮换** —— §1.2 的明确裁定,不是遗漏。
7. **(08-06 新查出,未修)三种"本机配对状态已不可用"仍然掉进 `Offline`** ——
   实测:`CaCertB64` 被截断/损坏(`CryptographicException`)、`CaCertB64` 不是合法 base64
   (`FormatException`)、CNG 私钥不在了(`CryptographicException:找不到密钥` —— 重装系统、
   换 Windows 用户、把 `profile.json` 拷到另一台机器都会这样)。
   三者的正确处置都是**重新配对**,而新旧两套判据都判 `Unknown/null → Offline` =「中枢没开机」。
   ★ 这是**第五种**归因(「本机配对状态不可用」),不在本次四分类里。
   未做的原因:它需要再加一个 `HubState`,会扩大 client 车道的改动面;
   且它与本次任务(证书生命周期)是相邻但不同的问题。**建议单开一条。**
8. **(08-06 新查出,未修)`SetDial` 只改 `Profile.Dial`,从不改 `Profile.EdgeUrl`** ——
   若某份档案的 `EdgeUrl` 仍是 `https://<ip>:<port>` 形式(2026-08-04 之前的形状,
   见 `ClientTransport.cs` 里那段注释),它对**正确的**主机也会永久名字不匹配。
   修好归因之后,这种档案会稳定显示「必须重新配对」——**结论是错的,主机没问题**。
   ★ 老代码是**碰巧**躲过的(重新配对会顺手重写 `EdgeUrl`)。属 transport 车道(本车道),
   但已超出本次任务范围,**未做**,单独记账。

---

## §5 推翻条件

1. 若将来设备私钥必须定期轮换(合规或威胁模型变化)⇒ §1.2 第二条作废,
   需回到 §6.2 的"新 key + 新 CSR",并**重做崩溃重入的状态机**(状态空间会大一档);
2. 若证书有效期校验被挪到应用层(过期也能拿到 401)⇒ §0(d) 的结论变,
   客户端归因逻辑须同步改;lan-edge selftest 甲节那条断言会在那一刻变红,**它就是提醒**;
3. 若 SAS 词表要改成中英双语 ⇒ 加一行新版本即可,但需重新审「念出口比对」这条主要用法
   (双语表的同音判据与英文表不同)。
