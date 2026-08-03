# 两边版本核验:客户端侧已做完;主机侧欠一处 fail-closed + 一处透传(决议草案 · 取号待定)

> 日期:2026-08-03
> 提出车道:**client**(`20-client-win/**`)
> 需要动手的车道:**core**(`10-core/identity/**`、`10-core/lan-edge/**`)
> 性质:**草案**,尚未并入 `DECISIONS.md`。取号按 D75 办 —— 并入那一刻才取号,草案期写 `D?`。
> 依据:`worktree-split-2026-08-03.md` §2.1「越界的唯一合法方式:在自己的决议包里写清要动别人哪一行、
> 为什么,由那条车道去改」。
> 行号一律标注「截至 `a0a651c`」,并**同时给出可搜的代码原文** —— 两层 MCP 决议包已经吃过
> 「行号漂移后证据失效」的亏,不再只靠行号定位。

---

## 1. 用户要什么

原话:「而且还要两边客户端版本一致的核验」。

## 2. 先把「版本」拆开,再看每一层**实际**被什么挡住

| 层 | 是什么 | 实际的门在哪 | 性质 |
|---|---|---|---|
| ① **协议版本** | 请求体 `protocolVersion` / 响应头 `X-LocalAI-Protocol` | **只有连上之后那一步在查**;配对那一刻**不查** | 见 §2.1 |
| ② **客户端构建戳** | `20260803-2018+4e5af32`,`build-client.ps1` 烧进程序集 | 无门,纯显示 | 提示 |

### 2.1 ★ 一条**必须记下来的更正**:六个词**拦不住**协议版本不一致

本次实现过程中,我先在代码注释、界面文案、自检断言三处写下了

> 「协议版本进了 SAS 推导 ⇒ 两边版本不同,六个词直接对不上」

**这是错的。** 提交后核对源码才发现:

- `10-core/identity/Sas.cs` 的 `EncodeTranscript` 里确实有一项 `protocol_version` —— 到此为止说法成立;
- 但 `10-core/lan-edge/Program.cs` 的 `/pair/enroll` 是把**请求体里的**
  `r.GetProperty("protocolVersion").GetInt32()` 原样传下去(截至 `a0a651c` 第 666 行);
- `10-core/identity/Pairing.cs` 的 `Enroll(…, int protoVer, …)`(第 139 行)拿到它**不做任何校验**,
  直接 `Sas.Derive(BuildTranscript(protoVer, …))`(第 155 行);
- 客户端那侧也用自己的常量推(`ClientTransport.cs` 的 `new PairTranscript(1, hubId, …)`)。

⇒ **两边用的是同一个「客户端自报值」**。客户端换成 v2,它自报 2、主机也拿 2 去推,
六个词照样一字不差。**协议版本在配对阶段是 fail-open 的。**

六个词真正拦住的是**中间人**:`hub_id`、CA 证书与 SPKI 指纹、服务器叶子证书指纹、CSR SPKI、
双方随机数、请求 id、claim secret 哈希。这些没有一样是攻击者能自由挑的。**这部分依然成立、依然有效。**

真正在查协议版本的是**连上之后**:`HubClient.NoteProtocol` 比对 `X-LocalAI-Protocol` 响应头,
不一致就置 `ProtocolMismatch`、**拒绝当成在线**(不拿可能误解的格式去解正文)。这一条是真的、fail-closed。

**教训**:错的安全声称比没有声称更坏 —— 它会让人以为一道门存在。
三处文案已按事实改回(提交见 §3),并加了两条断言钉住「那句错话不许再回来」。

### 2.2 第 ② 层为什么只能是提示

同一协议下的不同构建**完全可以互通**。升成硬拦 = 每发一版必须两台同时升,否则整套停摆 ——
与 P3「自家两台机器,能用为先」相反。所以:**只显示,不判断,永不进 prompt。**

---

## 3. 客户端侧已完成

提交 `a0a651c`(实现) + 随后一次更正提交(§2.1 的三处文案与断言)。

- `20-client-win/app/Services/BuildInfo.cs`(新):读 `AssemblyInformationalVersionAttribute`;
  拿不到就返回 `null`、界面显示「开发构建(未经 build-client.ps1 打包)」——
  ★ **不编一个版本号出来**,编了就让「两边对不对得上」失去意义。
- `90-ops/build-client.ps1`:发布加 `-p:InformationalVersion=$ver`。
- `20-client-win/transport/ClientTransport.cs`:`Transport.ClientVersion`;enroll 请求体多带 `clientVersion`。
- `20-client-win/app/Views/DevicesView.cs`:显示「本机客户端:<戳>」+ §2.1 的如实说明(三行)。

## 4. 请 **core 车道**做的两件事

### 4.1(要紧)让**配对那一刻**对协议版本 fail-closed

现在主机对 `protocolVersion` 照单全收。请在 `10-core/identity/Pairing.cs` 的 `Enroll`
(截至 `a0a651c` 第 139 行,搜 `public EnrollResult Enroll(byte[] csrDer`)开头,
与既有的两条 `throw new InvalidOperationException(...)`(窗口关闭 / 队列已满)并列,加第三条:

```csharp
// ★ 协议版本必须【主机自己认】才继续 —— 照单全收等于把版本协商交给对方来定。
//   注意:它虽然进了 SAS transcript,但两边用的是同一个自报值,六个词【拦不住】它(见决议包 §2.1)。
if (protoVer != SupportedProtocol)
    throw new InvalidOperationException($"unsupported protocol version {protoVer} (hub supports {SupportedProtocol})");
```

并加一个 `public const int SupportedProtocol = 1;`。
`/pair/enroll` 已经把 `InvalidOperationException` 映射成 403 + `{error}`,**边缘侧不用改**。

★ 客户端侧要配合的一行:`ClientTransport.Pair` 目前对 403 只抛通用异常;拿到含
`unsupported protocol version` 的 403 时应显示「两边协议版本对不上,请更新其中一端」而不是
「配对窗口没开」。这行**由 client 车道在 core 落地后自己改**。

### 4.2 把 `clientVersion` 存下来并露给主机的审批界面

客户端**已经在发** `clientVersion` 了,主机侧收得下但没存也没露出来 ——
`JsonDocument` 静默忽略不认识的字段(所以**老主机不会因此报错**,这条可以慢慢来)。
后果:**主机上批准配对时看不到对面是哪一版**,第 ② 层因此只在本机可见,不是「两边」。

三处小改(行号截至 `a0a651c`):

1. `10-core/identity/Pairing.cs`
   - `Pending` 类里,搜 `public string DisplayName = "";`(第 29 行)在旁边加
     `public string ClientVersion = "";`,并写上「自报的,只作显示,永不进 prompt」。
   - `Enroll(…)` 加**带默认值**的尾参 `string clientVersion = ""`,在 `DisplayName = displayName,`
     (第 166 行)旁边填 `ClientVersion = clientVersion,`。
     ★ 带默认值 ⇒ `identity/Program.cs` 的 4 处自检调用(第 456/460/515/522 行)与
     `lan-edge/Program.cs` 第 472 行的自检调用**一行都不用改**。
   - `ListPendingDetailed()`(第 90 行)的返回元组补 `ClientVersion` 与 `ProtoVer`。
     ★ `ListPending()`(第 79 行)**不要动** —— 主机控制台在用它。

2. `10-core/lan-edge/Program.cs`
   - `/pair/enroll`(搜 `app.MapPost("/pair/enroll"`,第 655 行)读出来传进去:
     `r.TryGetProperty("clientVersion", out var cv) ? cv.GetString() ?? "" : ""`。
     ★ **必须 `TryGetProperty`** —— 老客户端不带这个字段,用 `GetProperty` 会让它们**配不上对**。
   - `/admin/pairing/pending`(第 725 行)的投影加 `clientVersion = p.ClientVersion, protoVer = p.ProtoVer`。

3. `20-client-win/app/Services/HubAdmin.cs`(**client 车道自己改**,等 core 落地后)
   - `PendingPair` 加两个可空字段并在 `PendingAsync()` 解出来;审批卡片显示「对方客户端:<戳>」,
     **取不到就显示「未上报(旧版客户端)」,不猜**。

## 5. 验收(改完要有断言钉住,否则只有"两台版本不同"时才炸,而那正是它唯一被用到的场合)

- **老客户端仍能配对**:不带 `clientVersion` 的 enroll 请求必须照常成功 —— 钉住 §4.2 那个
  `TryGetProperty` / `GetProperty` 之差。
- **不支持的协议版本被挡住**:`protoVer = 2` 的 enroll 必须拿到 403 且错误文案含版本号 —— 钉住 §4.1。
- **断言要能真的变红**:按项目纪律,先把实现改坏、确认断言 FAIL,再改回。
- 客户端侧当前:`localai-client --selftest` = `PASS=1601 FAIL=0`;本包相关断言逐条验过能变红。
