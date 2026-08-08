# D? · 主机客户端的业务调用走【回环网关】—— 拿回主机档位

**日期**:2026-08-08 · **车道**:V13(`claude/v13-host-loopback-tier`)
**D 号**:**取号 `D?`**(落笔时仓库里已提交的最大号 = **D107**;并入时由第 0 条车道回填)
**触发**:2026-08-07 两台真 PC 实机报告 —— 主机上组件面板点「确定」→
「**这台设备不能做这个操作**」;聊天框敲字不起模型。

> ★ 本包**不含**任何管理端拆分相关改动;`lan-device` 的权限**一个字节都没放宽**。

---

## 0. 先说复核结论:协调层给的根因,有一半是错的

任务书给的根因链是:

> V12 改动 A:`run` → `run-lan` ⇒ 业务口只绑网卡,回环上只剩管理面
> ⇒ 主机客户端只能拨 `192.168.178.61:8443` ⇒ 经 lan-edge ⇒ 档位 `lan-device`
> V12 改动 B:`change_resident` 从 `lan-device` 拿掉
> ⇒ 两条互相打架

| # | 转述 | 复核结论 |
|---|---|---|
| 1 | `run-lan` 之后业务口只绑网卡、回环上只剩管理面 | ✅ **属实**(实机 `Get-NetTCPConnection`:`192.168.178.61:8443` / `127.0.0.1:8442` / `127.0.0.1:8080`) |
| 2 | 主机客户端经 lan-edge ⇒ 档位 `lan-device` | ✅ **属实** |
| 3 | `change_resident` 从 `lan-device` 拿掉 | ✅ **属实**(`gpu_policy.py:180`) |
| 4 | **改动 A 是这个症状的成因之一** | ❌ **错**。见下 |

### 0.1 ★ 改动 A 不是成因 —— 主机客户端**从来就是 `lan-device`**

判据在 `gateway.py:581`(`gpu_principal`):

```python
fp = request.headers.get("x-localai-cert-sha256", "")
if fp:
    return "lan-device" if resolve_lan_principal(fp) is not None else "remote-unauthenticated"
```

而 `lan-edge/Program.cs:1593,1598` 对**每一个**转发到网关的业务请求都做两件事:
先剥掉客户端自带的 `X-LocalAI-*`,再注入它自己验过的证书指纹。
⇒ **只要经过 lan-edge,档位就被封顶 `lan-device`,与 lan-edge 绑在哪个地址无关。**

V12 之前 `run` 把业务口绑在**回环**(`Program.cs:545`,`Bind = null ⇒ IPAddress.Loopback`),
客户端拨的是 `127.0.0.1:8443` —— **那仍然是 lan-edge,仍然带指纹,仍然是 `lan-device`**。

⇒ **单靠改动 B 就足以让主机点不动确定。** 改动 A 只换了拨号地址,没换档位。

**为什么这一条必须写清楚**:把 A 也算成成因,下一个人会去「把绑定改回回环」——
改回去之后主机**依旧**是 `lan-device`、依旧点不动,而 D107 刚修好的自配对链会**重新断掉**
(`DiscoverEdgeDialsAsync` 逐张网卡找 8443,它明确跳过回环)。
那是一次**看起来对症、实则两头落空**的修改。

### 0.2 ⇒ 「只能在主机上改」当天确实是一句没有任何设备能满足的话

* `intended_resident_set` 的「谁能写」= 只有 `trusted-local`(方案书四集合表,`gpu_policy.py:143`);
* 而 `trusted-local` 只可能来自**回环 + allowlist 账户 + 无证书指纹**(`classify_caller` + `gpu_principal`);
* 客户端的每一次业务调用都是 `Transport.Send(profile, dial, …)` ⇒ **必经 lan-edge** ⇒ 必带指纹。

⇒ 在 V13 之前,**没有任何一条客户端代码路径能产生 `trusted-local` 的业务请求**。
面板上那句「只能在主机上改」指向的是一个当时**不存在**的入口。

---

## 1. 修法:已判定为主机的这台,业务调用直连回环网关

```
主机(V13 之后)  客户端 ──明文 HTTP──► 127.0.0.1:8080  网关
                 身份 = OS 账户(port→PID→owner→allowlist) ⇒ trusted-local
副机(不变)      客户端 ──mTLS────► 192.168.178.61:8443  lan-edge ──► 127.0.0.1:8080 网关
                 身份 = 证书指纹经成员表反查            ⇒ 封顶 lan-device
```

判据落在**一个纯函数**上,两个方向各测一次:

`HubClient.DecideBusinessTarget(isHostMachine, pairedDial, loopbackPort)`

* `isHostMachine` 来自 **D36 角色判定**(`HostSetup.DecideRole`),由开机分流在起任何一条流
  **之前**用 `HubClient.NoteRole` 写入;
* **不**用 `ThisMachineIsHub()` —— 它自己的注释就写着「仅用于状态显示,不做权限判定」,
  而这里决定的正是这次请求会拿到哪一档;
* 默认 `false`(fail-closed)。判据自己炸了也落在副机那一边(`App.xaml.cs` 的 catch 已保证)。

### 1.1 ★ 回环这条路上**一个 `X-LocalAI-*` 头都不带**(承重)

走 lan-edge 时有一个**剥离者**(Edge 先剥客户端自带的头,再写自己验过的指纹)。
回环这条路上**没有那个剥离者** —— 客户端带什么,网关就原样看到什么。

**实机验证**(同一个回环连接,只多带一个自报指纹):

```
POST /v1/gpu/lease                                  → tier=trusted-local  (403 param, ttl 上限 1800)
POST /v1/gpu/lease  + X-LocalAI-Cert-Sha256: DEADBEEF → tier=remote-unauthenticated (401)
```

⇒ 自带一个指纹过去会把自己**从最高档一路打到 401**。所以这条路上连 `X-LocalAI-Protocol`
也不发:与其记住「哪些头是安全的」,不如让这条路上根本没有这个前缀的头。
断言钉在客户端自检里(见 §3)。

### 1.2 ★ 路由必须**整条**改,不能只改 GPU 那两个端点

中枢按 `principal_device` 认持有者:**回环得 `local`,经 Edge 得 `device_id`**。
`/v1/session/end` 是**按持有者匹配**释放租约的。
⇒ 租约在一条路上拿、退出时在另一条路上还 ⇒ **一条也匹配不上,租约挂满整个 TTL**。

这正是审计 B3 记过的形状(「同一台机器在两个面上叫两个名字」),不许再造一个。
所以 `LeaseKeeper` 的四处调用也一并改走同一条路由。

### 1.3 ★ 这次**没有**动的两件事,以及为什么

| 没动的 | 为什么 |
|---|---|
| `ChatClient`(`/v1/chat/completions`) | 它今天在主机上就是 `lan-device`,**不改 = 零回归**。改成回环会让主机客户端**顺带拿到 E1 解除权**(`E1_OVERRIDE_ALLOWED_TIERS` 只有 `trusted-local` 一个成员)—— 那是一次**安全面的扩权**,不属于本次范围,应当单独裁定 |
| `SyncClient` / `sync_*.py` | 禁区(V15 在动)。同步面的身份只在同步面内部用,与 GPU 租约不交叉 ⇒ 留在 Edge 上**不产生**上面 §1.2 那种失配 |

⇒ 结果是主机客户端在 GPU 面上是**一个**身份(`local`),在 chat/sync 面上仍是 `device_id`。
**如实记账**:这不是"已加固",是一条**还没收口的缝**;它今天不咬人,理由写在上表里。

### 1.4 ★ 反问过一遍:回环网关没起来时,会不会比原来更坏?

**不会,而且归因更准。** lan-edge 的上游就是 `http://127.0.0.1:8080` ——
网关死了,经 Edge 那条路也只会拿到 502。两条路**同生共死**,不存在
「回环不通但 Edge 还能用」的窗口。

差别只在**报出来的话**:回环这条路上**一条 TLS 归因都不许跑**
(`CallAsync` 里单独一支)。`TlsFailure.Classify` 第一步查的是本机设备证书,
在回环上它与失败毫无因果 —— 会把「网关没起来」归成「本机证书过期,请重新配对」,
而**重新配对先删本机私钥**。⇒ 为一件网关没起的事销毁一个完好的身份。
现在这条路只说一句真话:「业务口是回环网关 127.0.0.1:<port>,它没有应答 ——
不是配对/证书/防火墙的问题(回环不过防火墙),是网关没起来。」

★ 顺带记一条**已知的、不是本次引入的**账:`HostSetup.LocateGateway()` 自己写着
「网关**没有随包发布**,只存在于仓库里」。⇒ 在纯发布安装的主机上,自动起栈起不了网关,
两条路一起没有。那是另一条车道的债,本包不动它,但上面那句归因至少让人一眼看到该查哪儿。

---

## 2. 三件必须一并检查的事 —— 逐条结论

### ① 绕过 lan-edge 就跳过了「注入指纹 + 剥离」,有没有代码路径依赖那个指纹?

**没有。** 全仓 `x-localai-cert-sha256` 的读取点只有三处,逐处量过:

| 位置 | 无指纹时 | 结论 |
|---|---|---|
| `gateway.py:581` `gpu_principal` | 落 `classify_caller` 的档 ⇒ `trusted-local` | ✅ 不依赖 |
| `gateway.py:625` `principal_device` | `tier == "trusted-local"` ⇒ 返回 `LOCAL_DEVICE`(固定名字 `local`) | ✅ **明写了回环这一支** |
| `gateway.py:1834` chat 路径 | 本次没走回环(§1.3) | — 不适用 |

`10-core/speech/server.py:106` 也有一个 `VERIFIED_FP_HEADER`,但 `SpeechClient.cs:65` 打的是
`http://127.0.0.1:{Port}` —— **它本来就走回环**,不经本次改动的任何一条路径。

★ 已补一条断言钉住第二行(`test_gpu_policy.py` 第 9 节):
`principal_device(回环 + allowlist 账户 + 无指纹) == LOCAL_DEVICE`。
它为假的方式很具体:哪天有人让设备身份**必须**从指纹解析,退出时就放不掉租约。

### ② 副机不受影响

* **结构**:`DecideBusinessTarget(false, dial, …)` ⇒ `LanEdge`,拨号地址原样 —— 客户端自检钉住;
* **行为**(`test_gpu_policy.py` 第 9 节,**不 monkeypatch `classify_caller`**):
  同一个账户、同一条回环请求,**只多一个证书指纹头**(副机的每一次请求都带它)
  ⇒ `POST /v1/gpu/intended` **403 · dimension=tool**;
* **档位表**:`lan-device` 的 `actions` 仍然恰好是 `{"read", "lease"}` —— 断言用**相等**而非包含,
  偷偷加回 `change_resident` 会当场判红。

### ③ 补一条走真实连接路径的断言

这是今天最贵的一课:**4197 条全绿而核心功能开不起来,因为没有一条断言问过
「主机客户端实际拿到哪个档位」**。原因是两侧各自自洽:

* 服务端 `test_gpu_policy.py` 的 `_probe` **把 `classify_caller` 整个换掉** ⇒ tier 是直接构造的;
* 客户端从来没测过拨号去了哪儿。

⇒ 补的是**一对**断言,两边都走真实路径(详见 §3)。

---

## 3. 新增断言(成对)

### 3.1 服务端 —— `10-core/gateway/test_gpu_policy.py` 第 9 节(+12 条)

**不碰 `classify_caller`,也不碰 `gpu_principal`。** 唯一替身是
`caller_identity.account_from_request`(「操作系统说这个 socket 属于谁」——
合成请求没有真 socket,只有这一件事拿不到);此外
回环判据 → allowlist 查表 → `gpu_principal` → 档位表 → HTTP 层**全部真跑**。

| 方向 | 断言 |
|---|---|
| 主机 | 回环 + allowlist 账户 + **无指纹** ⇒ `trusted-local`;`/v1/gpu/intended` **不是 401/403** |
| 主机 | `principal_device` 不需要指纹也解得出身份(= `LOCAL_DEVICE`)—— 检查 ① |
| 副机 | 同一账户 + **一个指纹头** ⇒ 403 · `dimension=tool` —— 检查 ② |
| 反向 | 回环但不在 allowlist(`CodexSandboxOffline`)⇒ 403 |
| 反向 | 隔离账户 `ai-asset` 即使走回环 ⇒ 403 · `dimension=user` |
| 反向 | OS 身份解析不出来 ⇒ 降档改不动(不是「解析不到就当机主」) |
| 结构 | `lan-device.actions == {"read","lease"}`(相等,不是包含) |

★ 机主账户从 `config/caller-accounts.toml` **真读**,不写死在测试里 ——
写死的话,「把机主从 allowlist 里删掉」这件事在这里不会变红,而它正是这条路的开关。

### 3.2 客户端 —— `20-client-win/app/Selftest.cs`(+14 条)

**行为判据,不是"源码里有没有那个词"**:真开一个 `TcpListener` 冒充回环网关,
让**生产代码路径**(`HubGpu.ApplyAsync` / `RequestIntentAsync` —— 就是面板点确定
与敲字起模型那两格调的东西)真发一次请求,看它**落在哪儿**。

前提也钉住:给它一份 `Dial = 192.168.178.61:8443` 的配对档案 ——
实机上的主机**就是**配过对的,不给档案的话"改回走网卡"这个反证在自检里
根本复现不出来,最贵的那条断言会**假绿**(正是 08-07 那天的形状)。

**★ 实测反证**(把 `DecideBusinessTarget` 的主机那一支改回走网卡):

```
PASS=2129 FAIL=5
  FAIL  ★★★  判定为主机 ⇒ 业务调用的落点是回环网关            ⟨LanEdge 192.168.178.61:8443⟩
  FAIL  ★★★★ 组件面板点「确定」的那一次请求,真的打在了回环网关上 ⟨只剩 GET /health⟩
  FAIL  ★     而且应答读得懂                                  ⟨threw ASN1 corrupted data.⟩
  FAIL  ★★★★ 敲字起模型那一格走的是同一条回环路               ⟨只剩 GET /health⟩
  FAIL  ★★   主机就算档案里存着网卡地址,业务调用也走回环
```

★ 另有一条前提断言把「端口只有一个来源」钉死:`HostSetup.GatewayPort` 同时被
**起网关、探 `/health`、主机拨号**三处读。写成三个字面量的话,自检里换一个假网关
只换得掉其中一个,那条断言测的就变成「这台机器现在跑没跑网关」——**与被测代码无关**。
(为此 `GatewayPort` 从 `const` 改成读 `LOCALAI_GATEWAY_PORT` 的属性,与 `HubAdmin.AdminPort` 同款。)

---

## 4. 实机实测(2026-08-08,主机本机 · 真网关在跑)

在跑的中枢:`192.168.178.61:8443`(lan-edge 业务口)· `127.0.0.1:8442`(管理面)· `127.0.0.1:8080`(网关)。

| # | 做了什么 | 结果 |
|---|---|---|
| 1 | 以 `hongkongpingpon\zori ma` 从回环打 `POST /v1/gpu/lease`(ttl 故意超上限,必被拒) | `"tier":"trusted-local"` · `max_ttl_s: 1800`(`lan-device` 的上限是 900)—— **两个字段互相印证** |
| 2 | 同一条请求 + 自报指纹头 | `tier: remote-unauthenticated` · HTTP 401 —— §1.1 那条纪律的实证 |
| 3 | 回环打 `POST /v1/gpu/intended`(世代号故意用过期的 1,当前 8) | **HTTP 409 `generation_conflict`** —— **不是 403**。即:**过了权限层**。快照复核世代号仍是 8,**什么都没改** |

★ 第 3 条就是 08-07 那句「这台设备不能做这个操作」的正面对照:
**同一个端点、同一台机器、同一个人,换一条连接路径,403 变成 409。**

★ 顺带一条实机旁证:当前快照里那份 `client_session` 租约的 holder 是
`ac68df07-f6de-49f7-ae68-652b4d6f9a27` —— 一个 **device_id**,不是 `local`。
这就是「今天在跑的这个客户端走的是 lan-edge」的直接证据,也是 §1.2 要防的那个失配。

### 4.0 ★★★ 实机量出来的一件**没人说过**的事:两格是【串联】的

第 4 条实测:回环打 `POST /v1/gpu/intent {"alias":"assistant.fast"}`
→ **HTTP 409 `NOT_PERMITTED`**,组件是 `llm.assistant.8b@8k`。
原因是快照里 `permitted_on_demand` 是**空集合**。

而写 `permitted_on_demand` 归 `permit_on_demand` 动作,**只有 `trusted-local` 有它**
(`gpu_policy.py` 档位表;`lan-device` 是 `{"read","lease"}`)。

⇒ **在 V13 之前,没有任何一条客户端路径能产生 `trusted-local` 请求
⇒ 这份授权【谁也写不了】⇒「意图即起」在两台机器上都是结构性死路。**

这不是"敲字起模型没做好",是**它的前置授权入口从来没有存在过**。
所以实机两格的顺序是**串联**,不是并列:

1. 先在主机面板上勾一次『允许按需装载』并点确定(= 第一格,V13 让它第一次成为可能);
2. 然后敲字才可能把模型拉起来(= 第二格)。

★ 界面上那句「请在**主机**的「系统 › 模型」里勾一次」以前同样是一句**没有设备能满足**的话
—— 与「只能在主机上改」同一族。V13 一起解开。

### 4.1 ★★★★ 实机两格 —— **都通过了**(2026-08-08,本机真中枢)

两格走的是与修好后的客户端**完全同一条路**:回环网关 `127.0.0.1:8080`,
身份 `hongkongpingpon\zori ma`,不带任何 `X-LocalAI-*` 头。

**第一格 · 组件面板点「确定」**

```
POST /v1/gpu/intended  {"if_generation":11,"components":["speech.lite"]}
→ HTTP 200  {"ok":true,"state":"READY","message":"已应用"}
   世代 11 → 15
   intended_resident  []  →  ["speech.lite"]
   committed_resident []  →  ["speech.lite"]
   随后一拍 actual_resident 也跟上 ⇒ 不变式 I2(READY 且 actual == committed)成立
```

★ **对照 08-07 那次实机**:同一个端点、同一台机器、同一个人 ——
那天是 403「这台设备不能做这个操作」,今天是 **200 已应用**。

**第二格 · 敲字意图即起**

```
① 先勾『允许按需装载』(这一步 V13 之前【谁也写不了】,见 §4.0)
   POST /v1/gpu/intended {"if_generation":15,"components":["speech.lite"],
                          "permitted_on_demand":["llm.assistant.8b@8k", …]}
   → 200 已应用 · permitted_on_demand 真的写进去了
② 再发 ChatView 打字时发的那一发
   POST /v1/gpu/intent {"alias":"assistant.fast"}
   → 200 {"code":"OK","message":"已按需装载","component":"llm.assistant.8b@8k",
          "plane":"transient"}
      lease: kind=model_ref  holder="local"   ← ★ 正是回环/trusted-local 那个身份
   → 快照:transient_resident ["llm.assistant.8b@8k"]
      **free_gib 14.13 → 8.94** ← 显存条真的动了,~5.2 GiB,与它 5.31 的 peak 对得上
```

⇒ **08-07 报 ❌ 的那两格,今天在实机上都是 ✅。**

★ 没做的只有**鼠标那一层**(双击客户端、在面板上勾、在输入框里敲)——
那需要把本次构建覆盖掉正在跑的 `dist\client`。判据链上**除界面事件外的每一环**都实测过了:
路由(自检里真开监听量落点)· 档位(实机 tier=trusted-local)· 端点(上面两格真跑)。

### 4.2 ★★★ 而这两格**顺手撞出三条真缺陷** —— 都不是 V13 引入的

它们此前**一次都不可能被触发**:没有任何设备能走完这条路,所以没人到得了这里。
V13 把路打通之后第一件事,就是把它们暴露出来。

| # | 实测 | 后果 |
|---|---|---|
| 1 | `speech.lite` **装载不吃显存**(load 前后 `free_gib` 纹丝不动,`non_ai_used_gib_inferred` 反被算成 **-0.28**) | 它一旦进过 committed,**卸载必然撞上「显存未回收」那道闸** —— 因为本来就没有可回收的 |
| 2 | 上面那条 422 `vram_not_reclaimed` 把中枢打进 **`RECONCILING`**,而 `RECONCILING` **拒收一切新事务**(单写者) | **卡死**:再发任何变更都是 409 `busy`。状态不落盘,**只能重启进程**才出得来 |
| 3 | 按需装载起的 `llama-server.exe` 在 Broker 判定「已卸载」(`actual_resident` 已空)之后**仍然活着、仍然占着 ~5.2 GiB** | 孤儿进程。网关重启后的启动重整**把它当成 committed 又采纳了一遍**(`committed_resident: ["llm.assistant.8b@16k"]`),显存再也回不来 |

**处置(已做完,机器已复核回到接手时的样子)**:杀掉孤儿 `llama-server`(Broker 已经不认它了)
→ 重启网关(状态不落盘,重启即回空集合)→ 复核:
`state=READY · 全部集合为空 · free_gib 14.26 · I2/I3/I4 三条不变式成立 · 回环仍是 trusted-local`。

★★ **这三条不归本车道修**(动的是 Broker 的卸载与重整,不是路由),但必须记下来:
它们串起来是一条**从「点一次确定」到「整台中枢卡死且显存要不回来」**的路,
而今天它已经**真实可达**了。⇒ 建议下一条 GPU 车道优先看第 2 条:
一个**没有任何出路、只能重启进程**的状态,比它要防的那个错误更贵。

---

## 5. 门禁数字

★ **逐套件报**,不报一个合计数 —— STATE 那一行本来就是逐套件对账的
(「33 个套件里 6 个变了、27 个 ±0」)。**改前的数全部是实跑量的,不是减出来的。**

| 套件 | 改前 | 改后 | 差 |
|---|---|---|---|
| `10-core\gateway\test_gpu_policy.py` | 87 | **102** | **+15** |
| 客户端 `--selftest` | 2120 | **2144** | **+24** |
| 其余 22 个 Python 套件 | — | — | **±0** |
| 9 个 dotnet 套件(identity ×5 / lan-edge ×3 / transport) | 315 | 315 | **±0** |
| 跨进程契约欠债 | **1 / 30** | **1 / 30** | ±0(未新增路由/契约) |

聚合(**在本 worktree 里实跑**):

| 跑法 | 改前 | 改后 |
|---|---|---|
| `run-tests.ps1`(fast · 23 个 Python 套件) | PASS=1766 FAIL=0 | **PASS=1781 FAIL=0** |
| `run-tests.ps1 -Full`(+9 个 dotnet) | 2081 | **PASS=2096 FAIL=0** |
| 客户端 `--selftest`(单独跑 `bin\Debug` 那份) | 2120 FAIL=0 | **2144 FAIL=0** |

★ **rebase 到 main(含 V15 的 sync 全量拉取)之后重跑**:
fast **PASS=1814 FAIL=0** · 客户端 `--selftest` **2144 FAIL=0** · 契约欠债仍 **1 / 30**。
(1781 → 1814 的 +33 是 V15 带进来的,不是本车道的。)

★ `.githooks/pre-commit` 的前两段(绝对路径 · **行尾 churn**)已对整份改动**空跑过**并放行
(第三段会跑网关自检,超时未等完 —— 那一段的内容就是上表 fast 层那 1781 条)。
★★ 空跑那一趟顺手抓到本次的一处自伤:`Selftest.cs` 在仓库里是**纯 LF**,而我用
Python 文本模式改一个局部变量名时把整份翻成了 CRLF ⇒ `git diff --stat` 报 **8659 deletions**,
而这次改动**一行都没删过**。已按字节改回 LF(diff 归位成 `+322/-0`),
并记进 ASSERTION-PITFALLS 第 7 条(**第 9 次**)。

★★ **这三个数不能直接和 STATE 那行 4197 对账,理由要写清楚**:

1. **worktree 里 `-Full` 根本不跑客户端自检** —— 它跑的是 `dist\client` 那份出厂产物,
   而 worktree 里没有 `dist\client`(运行器自己如实报了这一条)。所以 2093 **不含**那 2134。
2. 出厂产物那一趟**旁边没有源码**,一批源码判据会按 ASSERTION-PITFALLS 第 11 条**跳过**;
   而上面那 2134 是在 `bin\Debug` 跑的(源码在旁边,实测命中 285 次)。
   **两趟的客户端自检数天然不同,不是同一个量。**
3. ⇒ 并入 main、出包之后**必须重跑一次 `-Full`**,用那个数更新 STATE。
   **本包不去猜那个数**(4197+26 是一次算术推断,而上面第 2 条说明它不成立)。

★ **四处反证都实跑过**(判据能为假,不是恒绿):

| 反证 | 结果 |
|---|---|
| `DecideBusinessTarget` 主机那一支改回走网卡 | 客户端 **FAIL=6**(含两条 ★★★★ 端到端) |
| 给 `lan-device` 加回 `change_resident` | `test_gpu_policy.py` **FAIL=4** |
| 删掉 `App.xaml.cs` 里 `Hub.NoteRole(...)` 那一行 | 客户端 **FAIL=3** |
| 把 `Hub.NoteRole(...)` 挪到 `Gpu.Start()` 之后 | 客户端 **FAIL=2** |

★ 四处反证做完**都已还原**,上表数字是还原后重跑的。

---

## 6. ★ 中央四文档草稿(**本包不改那四份**,由第 0 条车道串行并入)

### 6.1 `DECISIONS.md` —— 新增一条(取号 `D?`)

> **D? · 主机客户端的业务调用走本机回环网关,不再绕经 lan-edge**(2026-08-08)
>
> **裁定**:已由 D36 判定为中枢主机的那台,其客户端的**业务调用**直连
> `127.0.0.1:<网关端口>`(明文 HTTP,不带任何 `X-LocalAI-*` 头),
> 身份由网关按 OS 账户解析(`classify_caller` → `config/caller-accounts.toml` allowlist)
> ⇒ `trusted-local`。副机不变:经 lan-edge 的 mTLS 业务口 ⇒ `lan-device`。
>
> **理由**:lan-edge 对每个转发请求注入验证过的证书指纹,而 `gpu_principal`
> 一见指纹即封顶 `lan-device`。⇒ 在此之前**没有任何一条客户端路径能产生
> `trusted-local` 请求**,于是规格里「只有主机变更面能写 `intended_resident_set` /
> `permitted_on_demand`」指向的入口**结构上不存在**;界面上「只能在主机上改」
> 是一句没有任何设备能满足的话(实机 2026-08-07 复现)。
>
> **明确不做**:① 不给 `lan-device` 加回 `change_resident`(那是放宽副机,不是修主机);
> ② 不动档位体系;③ 不动管理端(已裁定拆分,另一条车道)。
>
> **已知未收口**:`ChatClient` 与 `SyncClient` 仍走 lan-edge ⇒ 主机在 chat/sync 面上
> 仍以 `device_id` 出现。chat 是**有意**留着的(改过去会顺带给主机 E1 解除权,属扩权,
> 需单独裁定);sync 是禁区(V15)。两者与 GPU 租约身份不交叉,今天不咬人。
>
> **将来**:管理端要走的也是这条回环路 —— 本条不是临时脚手架。

### 6.2 `STATE.md` —— 基线那一行

> 全仓基线(2026-08-08 · V13 并入之后):`-Full` → **PASS=<并入并出包后实跑填入> FAIL=0**。
> **逐套件只有两处变**:`10-core\gateway\test_gpu_policy.py` 87 → **102**(+15);
> 客户端 `--selftest` **+24**(V13 的真实连接路径断言 + 对抗式复核补的那几条)。其余 31 个套件 **±0**。
> 跨进程契约欠债 **1 / 30**(未新增路由,未新增契约)。
>
> ★★ **不要把上一版 4197 加 26 就填进来**:V13 那 +14 是在 `bin\Debug`(源码在旁边)量的,
> 而 `-Full` 跑的是 `dist\client` 出厂产物那一趟 —— 那一趟一批源码判据会**跳过**
> (ASSERTION-PITFALLS 第 11 条),两趟的客户端自检数天然不是同一个量。**必须实跑。**

### 6.3 `PROJECT_PLAN` —— 一句

> P4 的「组件挑选面板」与「意图即起」在**主机上**第一次具备可用前提:
> 权限层不再 403(实机 409 复核过)。剩余门槛是**用户先在主机上勾一次
> 『允许按需装载』**——见本包 §4.0,两格是串联的。

### 6.4 `worklog 2026-08.md` —— 一条

> 08-08 V13:修 08-07 实机回归(主机点确定被拒)。复核推翻了转述里的一半根因 ——
> `run → run-lan` 不是成因,主机客户端**一直**是 `lan-device`。
> 改法是给业务调用加一条路由判据(主机 → 回环网关)。
> 补了一对**走真实连接路径**的断言(服务端不再 monkeypatch `classify_caller`;
> 客户端真开监听让生产代码打过去),两边都验过能为假。
> 新坑记进 `ASSERTION-PITFALLS.md` 第 12 条。

---

## 7. ★★★ 对抗式复核(2026-08-08,收工前)—— 抓到三条 A 级,逐条已修

复核办法:五个镜头各自独立审这次 diff(路由回归 / 断言恒绿与假红 / 安全扩权 /
**去证伪本包自己的根因论断** / 该改没改),**每条发现都要过两个独立怀疑者**
(一个查代码、一个查"这个失败场景在真实运行里到得了吗",默认反驳)。
16 条原始发现,去重后 verify 前 5 条 —— **3 条活下来,全是 A 级,而且两条是本次改动引入的回归**。

### A-1 · 主机上「改地址后立刻验一次」验的是回环网关 ⇒ **填错地址也报已连上**

**本次引入的回归。** V13 之后主机的 `BusinessRoute()` 恒为 `127.0.0.1:8080`,
于是 `DevicesView` 里那句「立刻验一次,免得人以为改完就好了」验的是**另一条路** ——
而这个框改的 `Profile.Dial` 仍然是**聊天 · 内网同步 · 90 天一次的设备证书续签**唯一的拨号目标。
⇒ 把地址改错一位,顶栏照样刷绿,三样东西在背后静默地打向一个不存在的地址。
**「失败与成功长得一模一样」,而且是这次亲手造的。**

**修法**:主机上 `ProbeAsync` **额外**验一次配对通道(`HubClient.CheckPairingChannelAsync`),
结果单独放 `PairingChannelError`,**不去盖 `State`**(两者可以一真一假,合并会互相盖住);
`DevicesView` 改完地址后如果那一格非空就弹出来说清楚。
★ 顺带补上另一条同根的缺口:主机上「我还是不是有效成员 / 证书还好不好 / 主机是不是重铸了身份」
这三件事,V13 之后本来**结构上再也问不到了** —— 现在由这次额外的探测答。

### A-2 · V13 的**唯一生产触发点**没有任何断言 —— 删掉它,全部新断言仍全绿

`App.xaml.cs` 里那一行 `Hub.NoteRole(decision.Role.IsHost, …)` 是全仓唯一把角色喂给
`HubClient` 的地方。而自检里的 `isHost` 是**自检自己喂进去的** ⇒
**把那一行删掉、或挪到 `Gpu.Start()` 之后,20 多条新断言一条都不红**,
而出厂客户端在主机上会 `IsHostMachine==false` ⇒ 走 Edge ⇒ 点确定照旧被拒。

★★ **这与 08-07 那天的形状一模一样**(判据全绿、功能开不起来),只是缝从
「档位怎么解出来」挪到了「角色怎么喂进去」。**修一个洞的时候,在旁边挖了一个同形状的。**

**修法**:补三条结构判据(如实标成结构判据 —— 要变成行为判据就得真跑一遍 `App.OnStartup`
的后台分流),并且**钉顺序**而不只是"那个词在不在"。**实测反证**:

| 反证 | 结果 |
|---|---|
| 删掉 `Hub.NoteRole(...)` 那一行 | **FAIL=3** |
| 把它挪到 `Gpu.Start()` 之后 | **FAIL=2** |

★ 写这条断言的时候**当场踩了 ASSERTION-PITFALLS 第 1 条的第 10 次**:
我在 `App.xaml.cs` 的注释里写了「必须在 `Gpu.Start()` 之前」,`IndexOf` 就先命中了那句注释,
断言判红而代码是对的。⇒ 改成先 `Body()` 去注释再比位置。**这条已记进第 1 条的账。**

### A-3 · 主机上**非机主 Windows 账户**会比 V13 之前**更差**

**本次引入的回归。** `DecideBusinessTarget` 只看 `isHostMachine` 这个**整机**事实,
而回环那一端的档位是按**登录的 Windows 账户**判的(allowlist)。两者粒度不一样:

| 账户 | V13 之前(经 Edge) | V13 之后(走回环,未修前) |
|---|---|---|
| 机主 `Zori Ma` | `lan-device` `{read, lease}` | `trusted-local` 全套 ✅ |
| 访客 `Alle`(该文件明文记着**有意排除**) | `lan-device` `{read, lease}` | `unregistered-local` **`{read}`** ❌ |

⇒ 访客账户在主机上登录时,「意图即起」与 `client_session` 租约会**一起没掉** —— 比不改还差。

**修法**:`DecideBusinessTarget` 增加 `demotedWhy` 参数;判据只来自**服务端如实回带的
`error.tier`**(客户端没有别的办法知道自己在网关眼里是谁 —— allowlist 是服务端配置)。
一旦回环回了 401/403 且 tier 不是 `trusted-local` ⇒ **本次运行内永久退回 Edge**,
并把**这一次**调用原样重发(不重发的话,降级后第一次仍以 403 收场,
用户看到的还是那句「这台设备不能做这个操作」,只是换了个原因)。
★ 只降不升;`denied_param` / `denied_quota` 带的 tier 仍是 `trusted-local` ⇒ **不会**被误判。
★ 没配过对时**无路可退**,那时如实说,不假装换了路。

### 顺手修掉的四条(复核列出、未逐条 verify,但成立且便宜)

| # | 问题 | 修法 |
|---|---|---|
| 1 | `NoteRole` 同步派发 `Changed` 且不在 try/catch 内 ⇒ 一个订阅者抛异常会让 `Gpu.Start()`/`Lease.Start()` **一条都起不来** | 包 try/catch(与 `App.RaiseBootChanged` 同款) |
| 2 | 退出那道闸是 `Hub.IsPaired` ⇒ **没配过对的主机**经回环持着 `client_session`,退出通知被整个挡掉,租约挂满 TTL | 闸改成 `Hub.BusinessRoute() is null`,并补一条断言 |
| 3 | `GatewayUpAsync` 的 `HttpClient` **没关代理**,而与它"成对"的业务通道关了 ⇒ 配了代理的机器上两者会一真一假 | 加 `UseProxy = false`,与 `LoopHttp` 逐字对齐 |
| 4 | 两句**自己写的过头话**:「网关端口只有一个来源」(lan-edge 与 start-stack.ps1 里还各有一个 8080 字面量,不跟环境变量走)、「误判成主机只是打一个没人听的口」(同一个 verdict 还是 `MayStartStack` 的前件,会**真起一套栈**) | 按事实改写 |
| 5 | `Assert(applied.Ok)` 声称测「应答读得懂」,而 `ParseOutcome` 对**任何** 200 都回 `Ok=true` ⇒ **恒真** | 判据换成 `Generation == 7`(只可能来自真把桩那份 JSON 解开了) |
| 6 | Python 侧「allowlist 非空」那条**恒真**(空表时 `import gateway` 就已经抛了) | 删掉,换成「**把机主从 allowlist 里拿掉,同一条连接立刻改不动**」——那才是可以为假的形式 |

### ★ 复核**没有**推翻的两条(如实记账)

* 根因那一镜头**没能证伪**「主机客户端从来就是 lan-device / 改动 A 不是成因」。
* 有两条发现被怀疑者**驳回**了,不进上表:
  ① 「`Hub.State` 被 `ProbeAsync` 抢在 `NoteRole` 之前定死后不再重探」——
     不成立,`ComponentPicker` 每次加载都会经 `FetchCatalogAsync → CallAsync` 重刷;
  ② 「§9 的 `_owner` 取自被测那张表 ⇒ 那条路的开关永不变红」——
     前提对、结论错(实跑复现过);但**它指出的恒绿是真的**,已按上表第 6 条改掉。

### ★★ 复核列出、而本包**有意不修**的一条

`principal_device` 对回环一律返回**固定名** `local` ⇒ 主机客户端的租约从此挂在一个
**共享名**下:同机任何 `trusted-local` 调用方(含服务账户 `ai-mem`)调一次 `/v1/session/end`
就能把它们一起放掉。
**这不是本包引入的**(那个固定名是网关既有设计),但 V13 确实把客户端的租约**搬进**了这个共享桶
—— V13 之前它们挂在 `device_id` 下。
**不修的理由**:要修就得动 `principal_device` 的身份语义(给回环调用方也分个名字),
那是网关侧的一次有名字的决定,不该顺手做在一条路由改动里。**如实记在这里,等裁定。**

---

## 8. 留给下一个人的三句话

1. **不要给 `lan-device` 加回 `change_resident`** —— 那会把副机的口子一起开回去。
   这次修的是「主机走哪条路」,不是「放宽副机」。两者长得很像。
2. **不要把 lan-edge 的业务口改回绑回环** —— 那既治不好档位(仍然带指纹),
   又会重新弄断 D107 刚修好的自配对链。
3. **回环这条路上永远不要带 `X-LocalAI-*` 头** —— 没有剥离者,自带一个指纹
   会把自己从最高档打到 401(§1.1 有实测)。
