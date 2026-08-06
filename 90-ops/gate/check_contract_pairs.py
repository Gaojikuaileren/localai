r"""跨进程响应契约的【成对断言】元规则 —— D92 硬前置。

跑:  python 90-ops\gate\check_contract_pairs.py

════════════════════════════════════════════════════════════════════════════
 ★★★ 它要防的那个形状(审计 A1,A 级 6 条里有 4 条是它)

 服务端把 `lease_id` 放在 `body["lease"]["lease_id"]`,客户端在**顶层**找。
 服务端测「顶层有哪些键」,客户端测「拿这个形状能不能解析」——
 **各测各的,中间那条缝谁也没看**。两边都绿,而 AcquireAsync 恒返回 false,
 中枢那边 grant 却是真成功的 ⇒ 每次尝试留下一份没人认领的 client_session。

 ⇒ D92:每一个跨进程响应契约必须有一条**成对断言**(服务端钉顶层键集合 +
   客户端钉「拿这形状能解析出目标字段」),并配一条**元断言**枚举所有此类契约、
   **缺配对即判红**。没有这条元规则,垂直车道会各自再造一个 A1。

════════════════════════════════════════════════════════════════════════════
 ★★★ 为什么这个文件在 90-ops 而不在 10-core / 20-client-win

 **守卫必须待在它所检查的范围之外。** 它同时检查服务端(Python)与客户端(C#)
 两侧的测试,任何一侧都装不下它;放进任何一侧,那一侧就成了自己的裁判。
 (同款先例:`90-ops\debug\selfcheck.py` 不叫 test_*.py 也不放 10-core ——
  它要断言"生产代码里不得出现 90-ops\debug",放进被检查的目录里会绊倒自己。)

════════════════════════════════════════════════════════════════════════════
 ★★★ 反问过一遍:**新增一个契约而没登记,默认落哪边?**

 落**判红**侧。这是本文件唯一的承重设计,照 `ROUTE_TIERS` 反向全表那个形状:

   · 契约清单**靠枚举得出**,不手写 —— 网关侧 `import gateway` 直接读
     `ROUTE_TIERS`(它自己已被 `unclassified_routes()` 对着 `app.routes`
     反向全表过,本文件再复核一次);lan-edge 侧扫 `app.Map*` 调用。
   · 登记表 `CONTRACTS` 只当**期望值**用于反向全表(`set(实测) == set(登记)`),
     **绝不当作遍历源** —— 两者的区别就是"新增一项会红"和"新增一项被跳过"
     (ASSERTION-PITFALLS 3b)。
   · 于是:加了路由不登记 ⇒ 红;删了路由不撤登记 ⇒ 红。

════════════════════════════════════════════════════════════════════════════
 ★★ 本文件**不覆盖**什么 —— 明写,不许静默少盖

 ① 本文件只认「两半**在不在**」,**不看键集合对不对** —— 那是 GPU 车道那张
    `CROSS_PROCESS_CONTRACTS` 的职责(它逐条钉具体键集合)。**广度归这儿,深度归那儿**,
    第 ④ 组把两张表双向咬住,谁也漂不动。
 ② 判据是**契约号在不在**,不是"恰好出现一次" —— 契约号会同时出现在分节注释与
    断言消息里。⇒ 有人把断言体删空、只留下那行注释,本文件**看不出来**。
    防这一手的是对方那条元断言(它真的去打那个端点、比键集合)。
 ③ 请求体(客户端发、服务端收)方向**不在本文件范围内**。D92 的措辞是"响应契约"。
 ④ SSE 那几条(`/v1/gpu/events` · `/v1/sync/events` · `/v1/chat/completions`)的契约是
    **每一帧**的顶层键集合,不是整个响应体。本文件只登记它们欠着,**不定义帧该怎么钉**。
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:                                            # noqa: BLE001
    pass

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]

_p = _f = 0
_fails: list[str] = []


def check(name: str, cond: bool, extra: str = "") -> None:
    global _p, _f
    if cond:
        _p += 1
    else:
        _f += 1
        _fails.append(name)
        print(f"  X {name}" + (f"   {extra}" if extra else ""))


# ══════════════════════════════════════════════════════════════════════════
#  登记表 —— **期望值**,不是遍历源。
#
#  key   : (服务, METHOD, path)
#  state : paired      两半都在(**必须给 cid**)
#          server-only 只有服务端钉了顶层键集合
#          client-only 只有客户端钉了解析
#          none        两半都没有
#  cid   : 契约号,形如 `CONTRACT:gpu.intent`。**两半共用同一个锚点** ——
#          它同时是 GPU 车道 `CROSS_PROCESS_CONTRACTS` 的键、和客户端
#          `Selftest.cs` 里那条元断言的检索目标。
#          ★ 用同一个锚点,就不会出现"我以为钉的是这处、他钉的是那处"。
#          ★ 契约号是 ASCII:它要在 cp936 的钩子环境里被同一份代码读,
#            而中文子串在那里的行为取决于读文件时的编码猜测(ASSERTION-PITFALLS 8)。
#  lane  : 这条契约归哪条垂直切片(D92)。★ 这是**建议**,以第 0 条车道的裁定为准。
#  note  : 为什么它现在是这个状态 / 修它要动哪一行。
#
#  ★★★ 这张表**只许变短**(欠债那一栏)。第 ④ 组双向对拍盯着它:
#     对方新登记一条契约号而本表没跟上 ⇒ 红;本表标 paired 而对方那儿没有 ⇒ 红。
# ══════════════════════════════════════════════════════════════════════════
CONTRACTS: dict[tuple[str, str, str], dict] = {
    # ── 网关(Python / FastAPI)⇒ 客户端(C#) ─────────────────────────────
    ("gateway", "POST", "/v1/gpu/lease"): {
        "state": "paired", "cid": "CONTRACT:gpu.lease.grant",
        "lane": "GPU/租约切片",
        "note": "A1 的病灶本身。最早成对的一条,现已收进 GPU 车道的契约号登记表。",
    },
    ("gateway", "POST", "/v1/gpu/intent"): {
        "state": "paired", "cid": "CONTRACT:gpu.intent",
        "lane": "GPU/租约切片",
        "note": "D87① 「意图即起」。★ **本表落地后第一条被门禁当场抓住的新契约** —— "
                "它随 GPU 车道那次提交出现,合并当天 `未登记` 判红,登记时才发现两半都已写好。",
    },
    ("gateway", "GET", "/v1/gpu/snapshot"): {
        "state": "paired", "cid": "CONTRACT:gpu.snapshot",
        "lane": "GPU/租约切片",
        "note": "★ 2026-08-06 夜(V5)还清。消费者 Services/LeaseKeeper.cs —— 它拿 generation "
                "去发租约;generation 读错 = 每次 if_generation 都冲突,而那看起来像'中枢忙'。"
                "★★ 还债时**当场抓到那条缺陷**:客户端原来写 `? g.GetInt64() : 0` —— "
                "读不到就悄悄用 0 ⇒ 稳定 409 ⇒ 伪装成中枢并发。"
                "已改成无默认值的 TryParseGeneration + 两条**理由不同**的失败消息",
    },
    ("gateway", "POST", "/v1/gpu/lease/renew"): {
        "state": "paired", "cid": "CONTRACT:gpu.lease.renew",
        "lane": "GPU/租约切片",
        "note": "顶层键集合 {result, snapshot};客户端半边钉 409(条件写不匹配)⇒ 立刻自隐",
    },
    ("gateway", "GET", "/v1/gpu/events"): {
        "state": "paired", "cid": "CONTRACT:gpu.events.frame",
        "lane": "GPU/租约切片",
        "note": "★ 2026-08-06 夜(V5)还清。SSE:契约是**每一帧**的顶层键集合,不是响应体 —— "
                "那条流永不结束,它根本没有响应体。服务端半边直接驱动 gpu_events 的异步生成器"
                "取第一帧(用 TestClient 会挂死:退出 with 要关连接,而生成器正 await 在 "
                "wait_for_change 上,两边互相等);客户端半边钉「记住 event: 那一行」。"
                "★★ 还债时抓到:error 帧此前被当成快照去解析,失败后记成『帧读不懂』—— "
                "中枢把原因说了,客户端把它翻译成了一句指向别处的猜测",
    },
    ("gateway", "GET", "/v1/gpu/components"): {
        "state": "paired", "cid": "CONTRACT:gpu.components",
        "lane": "GPU/租约切片",
        "note": "★ 2026-08-06 夜(V5)还清。消费者 Services/HubGpu.cs —— 挑选面板的数据源。"
                "解析漂了就退回客户端自己编一份清单,而客户端**已经编过一份**(第三套词汇)。"
                "⇒ 断言钉的是「取不到就**什么都不列**」(_catalog=null + 清空列表 + "
                "ModelCatalog.All 不许长回来),不是兜底;并钉「少一个键整份返回 null,不保留半份目录」",
    },
    ("gateway", "POST", "/v1/gpu/intended"): {
        "state": "paired", "cid": "CONTRACT:gpu.intended",
        "lane": "GPU/租约切片",
        "note": "★ 2026-08-06 夜(V5)还清。消费者 Services/HubGpu.cs —— 「点确定」那一次事务。"
                "失败要回带**完整** snapshot,客户端读不出就无从重试(只回裸 409 = 又变成轮询)。"
                "★★ 注意它与子形状条目 CONTRACT:gpu.intended.blocking 是**前缀关系** —— "
                "见 _SUBSHAPE_CIDS 与 _anchor_count 上方那段",
    },
    ("gateway", "POST", "/v1/session/end"): {
        "state": "paired", "cid": "CONTRACT:session.end",
        "lane": "GPU/租约切片",
        "note": "顶层键集合 {status, released_leases, device, reason}。"
                "★ 这条路由曾经**根本不存在**而客户端每次退出都在调它、失败还被吞掉",
    },
    # ── 同步 + 对话切片(V6)· 2026-08-06 收盘登记 ─────────────────────────
    #  ★★ 这 5 条的两半**在 V6 那次提交里就写好了**,而本表直到收盘才登记上 ——
    #    中间那段时间门禁照报全绿。抓到它的是第 ⑦ 组(全仓契约号反向全表),
    #    而第 ⑦ 组本身是**当天补的**:在它之前,这个方向是空的。
    ("gateway", "GET", "/v1/models"): {
        "state": "paired", "cid": "CONTRACT:models.list",
        "lane": "同步/对话切片", "server_file": "10-core/gateway/test_sync.py",
        "note": "消费者 Services/HubClient.cs 与 transport/Program.cs。",
    },
    ("gateway", "POST", "/v1/chat/completions"): {
        "state": "paired", "cid": "CONTRACT:chat.stream.frame",
        "lane": "同步/对话切片", "server_file": "10-core/gateway/test_sync.py",
        "note": "SSE,**全项目最热的一条路径**。契约是**每一帧**的顶层键集合,不是响应体 —— "
                "那条流永不结束。消费者 Services/ChatClient.cs。",
    },
    ("gateway", "POST", "/v1/sync/push"): {
        "state": "paired", "cid": "CONTRACT:sync.push",
        "lane": "同步/对话切片", "server_file": "10-core/gateway/test_sync.py",
        "note": "消费者 Services/SyncClient.cs。",
    },
    ("gateway", "GET", "/v1/sync/events"): {
        "state": "paired", "cid": "CONTRACT:sync.events.frame",
        "lane": "同步/对话切片", "server_file": "10-core/gateway/test_sync.py",
        "note": "SSE,契约是每一帧的顶层键集合。与 sync.snapshot **共用同一个 Absorb** —— "
                "全量就是 since_rev=0 的那一帧,所以两条钉在一起。消费者 Services/SyncClient.cs。",
    },
    ("gateway", "GET", "/v1/sync/snapshot"): {
        "state": "paired", "cid": "CONTRACT:sync.snapshot",
        "lane": "同步/对话切片", "server_file": "10-core/gateway/test_sync.py",
        "note": "★★ 上一版这条写着「**客户端一个字都没读它**(全仓 grep 零命中)」并要求"
                "「要么接上要么撤掉」。**V6 接上了**:`SyncClient.PullFullAsync` 是它在客户端的唯一落点"
                "(`Services/SyncClient.cs`),用途**不是**重连对齐(重连那条路本来就没洞 —— "
                "`/v1/sync/events` 首帧就是全量),而是**丢一帧之后的补全量**。"
                "★ 处置理由见 `decision-packets/sync-snapshot-disposition-2026-08-06.md`"
                "(那份是**待用户裁定**的处置建议,**不取号**)。",
    },
    ("gateway", "GET", "/health"): {
        "state": "none", "lane": "未分配 —— 请第 0 条车道指派",
        "note": "★ 客户端不读它;90-ops\\start-stack.ps1:144 只看 curl 退出码、**不解析响应体**。"
                "⇒ 今天它的响应体没有任何消费者,顶层键集合改成什么都不会有人红",
    },

    # ── speech 后端(Python / 独立进程)⇒ 网关(Python) ────────────────────
    #  ★ 又一次"同语言不等于同进程":speech 是自己的 venv、自己的进程,
    #    网关跨进程去读它的应答 —— 那正是 A1 死掉的那一步。
    #  ★★ 两半**读同一份** 10-core/speech/contracts.json(期望值只有一份,没法跟自己分家)。
    ("speech", "GET", "/health"): {
        "state": "paired", "cid": "CONTRACT:speech.health",
        "lane": "语音切片(P5 v1)",
        "server_file": "10-core/speech/selftest.py",
        "client_file": "10-core/gateway/test_speech_contract.py",
        "note": "就绪判据。★ 未就绪回 503(与 llama-server 同形状:进程活着 != 能服务)——"
                "装载器的 _wait_ready 就靠这个 2xx。",
    },
    ("speech", "POST", "/v1/speech/asr"): {
        "state": "paired", "cid": "CONTRACT:speech.asr",
        "lane": "语音切片(P5 v1)",
        "server_file": "10-core/speech/selftest.py",
        "client_file": "10-core/gateway/test_speech_contract.py",
        "note": "★★ provenance 是**安全判据**:它决定这段转写能不能直通记忆写入,"
                "由**通道**(回环 / lan-edge 注入的已验证指纹头)在服务端算出,**不由调用方自报**。"
                "读错的后果不是少个字段,是记忆库的准入交给了调用方。",
    },
    ("speech", "POST", "/v1/speech/tts"): {
        "state": "paired", "cid": "CONTRACT:speech.tts",
        "lane": "语音切片(P5 v1)",
        "server_file": "10-core/speech/selftest.py",
        "client_file": "10-core/gateway/test_speech_contract.py",
        "note": "半双工按住说话的回放。v1 用 base64 装在 JSON 里 —— 一次按住说话的音频很短,"
                "换来的是形状可钉;将来要流式(SSE)时按帧重登记,不在本条。",
    },

    # ── lan-edge(C# / Kestrel)⇒ 客户端(C#) ────────────────────────────
    #  ★ 同语言不等于同进程。D92 的措辞是「跨**进程**响应契约」,
    #    而 A1 那条缝的成因是"两边各自都绿",与语言无关。
    ("lan-edge", "POST", "/pair/enroll"): {
        "state": "paired", "cid": "CONTRACT:cert.pair.enroll",
        "lane": "证书/配对切片",
        "server_file": "10-core/lan-edge/Program.cs", "client_file": "20-client-win/transport/Program.cs",
        "note": "消费者 transport/ClientTransport.cs:164。"
                "★ 应答里带主机 SAS 词表版本 —— transport/Program.cs:163 钉了**那一个字段**,"
                "不是顶层键集合(单字段断言挡不住别的键漂移)"
                " **已还(V4)**:顶层键集合钉死,单字段那条保留但不再是唯一依据。",
    },
    ("lan-edge", "POST", "/pair/status"): {
        "state": "paired", "cid": "CONTRACT:cert.pair.status",
        "lane": "证书/配对切片",
        "server_file": "10-core/lan-edge/Program.cs", "client_file": "20-client-win/transport/Program.cs",
        "note": "消费者 transport/ClientTransport.cs:224 —— 握手失败会让设备**永远停在 provisioning**"
                " **已还(V4)**:两侧都覆盖【失败分支】—— 三个键在每种状态下都在,但批准前后两个的**值是 null**;客户端在 pending 时跳出就会对 null 调 GetString()!,而主机侧那条记录会永远停在 provisioning。",
    },
    ("lan-edge", "POST", "/pair/claim"): {
        "state": "paired", "cid": "CONTRACT:cert.pair.claim",
        "lane": "证书/配对切片",
        "server_file": "10-core/lan-edge/Program.cs", "client_file": "20-client-win/transport/Program.cs",
        "note": "消费者 transport/ClientTransport.cs:235"
                " **已还(V4)**。",
    },
    ("lan-edge", "POST", "/pair/complete"): {
        "state": "paired", "cid": "CONTRACT:cert.pair.complete",
        "lane": "证书/配对切片",
        "server_file": "10-core/lan-edge/Program.cs", "client_file": "20-client-win/transport/Program.cs",
        "note": "消费者 transport/ClientTransport.cs:241"
                " **已还(V4)**:它的应答是**文本** `active`,如实按文本契约钉 —— 编一个空键集合会让判据恒真。",
    },
    ("lan-edge", "POST", "/identity/renew/enroll"): {
        "state": "paired", "cid": "CONTRACT:cert.renew.enroll",
        "lane": "证书/配对切片",
        "server_file": "10-core/lan-edge/Program.cs", "client_file": "20-client-win/transport/Program.cs",
        "note": "消费者 transport/ClientTransport.cs:389 —— ★ 审计 A5 曾在这一族:"
                "服务端写了 CertLifecycle/RenewDeviceCertIfDue 而客户端零调用点。"
                "**A5 已于 2026-08-06 随 V1(D93)闭环**(HubClient.cs 实测 4 处调用),"
                "但**这条契约的欠债没还** —— 接上了调用点 ≠ 有了成对断言,两件事别混。"
                " **已还(V4)**。",
    },
    ("lan-edge", "POST", "/identity/renew/complete"): {
        "state": "paired", "cid": "CONTRACT:cert.renew.complete",
        "lane": "证书/配对切片",
        "server_file": "10-core/lan-edge/Program.cs", "client_file": "20-client-win/transport/Program.cs",
        "note": "消费者 transport/ClientTransport.cs:426。"
                "lan-edge/Program.cs:807 钉了 `changed` 一个字段,不是顶层键集合"
                " **已还(V4)**:从只钉 `changed` 一个字段升级成顶层键集合。",
    },
    ("lan-edge", "GET", "/admin/ping"): {
        "state": "paired", "cid": "CONTRACT:cert.admin.ping",
        "lane": "证书/配对切片",
        "server_file": "10-core/lan-edge/Program.cs", "client_file": "20-client-win/app/Selftest.cs",
        "note": "消费者 Services/HubAdmin.cs:125 —— pairingWindowOpen 从这里来;"
                "读错就退回'本地布尔替中枢记配对窗口开没开'(Selftest.cs:5923 明令禁止的那件事)"
                " **已还(V4)**。",
    },
    ("lan-edge", "GET", "/admin/pairing/pending"): {
        "state": "paired", "cid": "CONTRACT:cert.admin.pending",
        "lane": "证书/配对切片",
        "server_file": "10-core/lan-edge/Program.cs", "client_file": "20-client-win/app/Selftest.cs",
        "note": "消费者 Services/HubAdmin.cs:197"
                " **已还(V4)**:顶层 + 元素两层都钉(六个词在元素那一层)。",
    },
    ("lan-edge", "POST", "/admin/pairing/approve"): {
        "state": "paired", "cid": "CONTRACT:cert.admin.approve",
        "lane": "证书/配对切片",
        "server_file": "10-core/lan-edge/Program.cs", "client_file": "20-client-win/app/Selftest.cs",
        "note": "消费者 Services/HubAdmin.cs:220"
                " **已还(V4)**:200 与 **409 失败分支**都钉。",
    },
    ("lan-edge", "POST", "/admin/pairing/deny"): {
        "state": "paired", "cid": "CONTRACT:cert.admin.deny",
        "lane": "证书/配对切片",
        "server_file": "10-core/lan-edge/Program.cs", "client_file": "20-client-win/app/Selftest.cs",
        "note": "消费者 Services/HubAdmin.cs:223"
                " **已还(V4)**。",
    },
    ("lan-edge", "POST", "/admin/pairing/window"): {
        "state": "paired", "cid": "CONTRACT:cert.admin.window",
        "lane": "证书/配对切片",
        "server_file": "10-core/lan-edge/Program.cs", "client_file": "20-client-win/app/Selftest.cs",
        "note": "消费者 Services/HubAdmin.cs:230"
                " **已还(V4)**:应答里中枢自报的窗口状态现在真的被读了(此前只能拿本地布尔猜)。",
    },
    ("lan-edge", "GET", "/admin/devices"): {
        "state": "paired", "cid": "CONTRACT:cert.admin.devices",
        "lane": "证书/配对切片",
        "server_file": "10-core/lan-edge/Program.cs", "client_file": "20-client-win/app/Selftest.cs",
        "note": "**两个**消费者:Services/HubAdmin.cs:238 与 Services/HubClient.cs:310 —— "
                "★ 两处各自解析同一个形状,漂移时只会有一处被发现"
                " **已还(V4)**,并且那个**双解析器缺陷已收**:HubClient.ParseDevices 现在委派给 HubAdmin.ParseDevices,全客户端只剩一处解析;顶层 + 元素两层都钉。",
    },
    ("lan-edge", "POST", "/admin/devices/revoke"): {
        "state": "paired", "cid": "CONTRACT:cert.admin.revoke",
        "lane": "证书/配对切片",
        "server_file": "10-core/lan-edge/Program.cs", "client_file": "20-client-win/app/Selftest.cs",
        "note": "**两个**消费者:Services/HubAdmin.cs:255 与 Services/HubClient.cs:328(同上)"
                " **已还(V4)**,并且那个**两个调用方都不看应答体的缺陷已收**:两处共用 HubAdmin.ParseRevokeBody,ok=false 或 generation 不是正数都会记进 LastError(此前失败的吊销与成功的长得一模一样)。",
    },
}

# ── 客户端源码根:算"这条契约有没有消费者"用的。零命中判红(见第 ⑤ 组)──────
CLIENT_ROOTS = ["20-client-win/app", "20-client-win/transport"]

# ══════════════════════════════════════════════════════════════════════════
#  ★★★ 与 GPU 车道那张契约号登记表的**双向**反向全表
#
#  `10-core/gateway/test_gpu_broker.py` 里有一张 `CROSS_PROCESS_CONTRACTS`,
#  逐条钉**顶层键集合的具体内容**,并去 `Selftest.cs` 找同名 `CONTRACT:` 标记。
#  那是**深度**;本文件是**广度**。两者不重复,但**必须互相咬住**:
#
#   · 他们那张表是**手写的遍历源** —— 27 条契约里它只覆盖 5 条,
#     而剩下 22 条的存在**它自己说不出来**(ASSERTION-PITFALLS 3b:
#     判词说"每一个",判据是一份手写名单)。补上这一半正是本文件的职责。
#   · 反过来,本文件只认"两半在不在",**不看键集合对不对** —— 那是他们的职责。
#
#  ⇒ 双向对拍:他们新登记一条而本表没跟上 ⇒ 红;本表标了 paired 而他们那儿没有 ⇒ 红。
#
#  ★ 这条**取代了**第一版那个按语法数 `set(...keys()) ==` 的棘轮。
#    那个棘轮当天就被证伪:GPU 车道的新断言写成 `set(_rl.json())`(没有 `.keys()`),
#    正则一条都没数到 —— **一个按语法形状计数的护栏,换个等价写法就瞎了**,
#    而它瞎掉的方向是"少数了" ⇒ 静默放过。改成对拍**声明出来的契约号**,
#    形状怎么写都不影响。
# ══════════════════════════════════════════════════════════════════════════
_PEER_FILE = "10-core/gateway/test_gpu_broker.py"
_CLIENT_PIN_FILE = "20-client-win/app/Selftest.cs"
#  ★ 针拼出来,理由同上(本文件也可能被别的扫描器扫)。
#  ★★ **两个正则,不是一个** —— 它们问的不是同一件事:
#    · `_CID_KEY` 带引号 ⇒ 只认**登记表的键**。用它去数对方那张表,
#      就不会把散落在注释/断言消息里的偶然提及也算成"登记了一条契约"。
#    · `_CID_BARE` 不带引号 ⇒ 认**任何出现**。客户端那半边的契约号写在
#      `// ── CONTRACT:gpu.intent ──` 与断言消息里,引号并不紧贴着它
#      —— 第一版两边共用带引号那个,于是客户端**零命中**、判红。
#      ★ 那次红是对的:一个"零命中"的判据本来就该红。错的是判据,不是被判的东西。
_CID_KEY = re.compile(r'"(CONTRACT' + r':[a-z0-9_.]+)"')
_CID_BARE = re.compile(r"CONTRACT" + r":[a-z0-9_.]+")

#  他们表里**不是顶层响应契约**的条目:登记在这里并写明**为什么它不抵消那条路由的欠债**。
#  ★ 没有这一栏,`gpu.intended.blocking` 看起来就像"/v1/gpu/intended 已经成对了" ——
#    而它钉的是 409 响应里 `result.blocking[i]` 那个**子对象**的形状,
#    该路由 200 的**顶层键集合仍然没人钉**。把"钉了一部分"读成"钉完了",
#    正是本项目反复吃亏的那种四舍五入。
#  ★★ 值从「一句理由」改成 {why, home}(2026-08-06 收盘,第 0 条车道):
#    原来的校验写死「必须还在 `test_gpu_broker.py` 里」,而证书/配对那族的子形状
#    住在 **C#**(`WireContracts.cs`)—— 写死会把它们**误红**,
#    而"为了让护栏别误红就不登记"正是这条护栏要防的事。
#    ⇒ 逐条说明它住哪(与 V4 给路由条目加的 `server_file` 同款手法)。
_SUBSHAPE_CIDS = {
    "CONTRACT:gpu.intended.blocking": {
        "home": "10-core/gateway/test_gpu_broker.py",
        "why": "钉的是 POST /v1/gpu/intended **409** 响应里 result.blocking[i] 的子对象形状"
               "(即 Lease.to_json()),**不是**该路由 200 的顶层键集合 ⇒ 不抵消它的欠债",
    },
    # ── 证书/配对切片(V4)带进来的四个子形状 ──────────────────────────────
    #  ★ 它们此前**一条都没登记**,而门禁照报全绿 —— 那正是第 ⑦ 组补上的那个方向抓到的。
    "CONTRACT:cert.admin.ping.servercert": {
        "home": "10-core/identity/WireContracts.cs",
        "why": "钉的是 GET /admin/ping 响应里 **.serverCert 子对象**的形状,"
               "不是该路由的顶层键集合 ⇒ 不抵消 cert.admin.ping 那条",
    },
    "CONTRACT:cert.admin.devices.item": {
        "home": "10-core/identity/WireContracts.cs",
        "why": "钉的是 GET /admin/devices 响应里 **.devices[i] 数组元素**的形状 ⇒ 不抵消路由本身",
    },
    "CONTRACT:cert.admin.pending.item": {
        "home": "10-core/identity/WireContracts.cs",
        "why": "钉的是 GET /admin/pairing/pending 响应里 **.pending[i] 数组元素**的形状 ⇒ 不抵消路由本身",
    },
    "CONTRACT:cert.admin.approvedeny.409": {
        "home": "10-core/identity/WireContracts.cs",
        "why": "钉的是批准/拒绝两条路由的 **409** 响应形状,不是它们 200 的顶层键集合 ⇒ 不抵消路由本身",
    },
}

#  欠债总数钉死 —— 印在覆盖账上的那个数字必须和实际对得上。
#  ★ 它不是重复登记表:它让"又欠了一条"变成 diff 里的**一行**,而不是表里多一项没人数。
#  ★ 2026-08-06 夜(V5):23 → 19,[GPU/租约切片] 那 4 条还清。
#    棘轮只许往下走 —— 这个数字变大必须是一次**有名字的决定**,而不是表里多了一项没人数。
#  ★★ 2026-08-06 收盘(V4 并入时解冲突):两条车道各自减自己那批,而**这一行是共用的**
#    ⇒ 必然冲突。V5 写 19(23−4),V4 写 10(23−13);合起来是 **23−4−13 = 6**。
#    ★ 没有靠这句算术定案 —— 下面那条断言拿**实测**的 DEBT 与本值对拍,
#      算错了它当场红。**这一行是期望值,不是事实来源。**
#  ★★★ 2026-08-06 收盘:6 → **1**,[同步/对话切片] 那 5 条登记上(V6 早就写好了两半,
#    只是本表没跟上 —— 见第 ⑦ 组)。**只剩 `GET /health` 一条**,
#    而它今天的响应体确实没有任何消费者(`start-stack.ps1` 只看 curl 退出码)。
#    ⇒ 那一条**不要为了把数字清零而随手配一条断言** —— 给一条没人走的路配断言,
#      断言是绿的,而它什么都没守。它该被裁定为"接上"或"撤掉",不是被凑掉。
_EXPECTED_DEBT = 1


#  ── lan-edge 端点提取器 ────────────────────────────────────────────────
#  ★ 抽成函数是为了**能被反过来验**:第 ⑥ 组拿合成输入两个方向各问一遍。
#    提取器静默失灵会让整张契约表变空,而空表在终端上和"全清白"长得一模一样
#    (ASSERTION-PITFALLS 第 10 条同族:scan_fake 盘符写死 D: 而项目在 E:,
#     零命中输出「未发现问题」)。
_MAP_RE = re.compile(r"\bapp\.Map(Get|Post|Put|Delete)\(\s*\"([^\"]+)\"")


def lan_edge_endpoints(src: str) -> set[tuple[str, str]]:
    """从 lan-edge 的 Program.cs 里取出 (METHOD, path)。

    ★ 只认 `app.Map*` —— `upstream.Map*` 是自检里的**上游替身**,不是生产端点。
    """
    return {(m.upper(), p) for (m, p) in _MAP_RE.findall(src)}


# ══════════════════════════════════════════════════════════════════════════
#  ① 枚举 —— 结构性,不手写
# ══════════════════════════════════════════════════════════════════════════
print("=== 1. 契约清单靠枚举得出(不手写) ===")

actual: set[tuple[str, str, str]] = set()

# 网关:import 真模块读 ROUTE_TIERS。
#  ★ 导不进来 ⇒ **判红**,不是"跳过" —— 跑不了和跑过了必须长得不一样。
sys.path.insert(0, str(REPO / "10-core" / "gateway"))
gateway = None
try:
    import gateway                                            # noqa: E402
except Exception as e:                                        # noqa: BLE001
    check("★★★ 网关模块能导入(导不进来就一条契约都枚举不出来,而那**不算通过**)",
          False, f"{type(e).__name__}: {e}")

if gateway is not None:
    check("★★★ 网关模块能导入(导不进来就一条契约都枚举不出来,而那**不算通过**)", True)
    _gw = {("gateway", m, p) for (m, p) in gateway.ROUTE_TIERS}
    actual |= _gw
    check(f"★★ 网关路由**零命中判红**(实测 {len(_gw)} 条)", len(_gw) > 0)
    # ★ 复核一次上游那条反向全表:ROUTE_TIERS 必须等于 app.routes,
    #   否则本文件的遍历源本身就是漏的 —— 而它自己不会说。
    _unc = gateway.unclassified_routes()
    check("★★★ 上游反向全表仍然成立(ROUTE_TIERS ≡ app.routes)—— "
          "它一旦破,本文件的枚举源就是漏的,而漏掉的永远是新加的那条",
          _unc == [], f"未归类:{_unc}")

# lan-edge:扫 app.Map* 调用。
#  ★ 只扫 `10-core/lan-edge/Program.cs` 一个文件,**不扫全仓** ——
#    `20-client-win/transport/Program.cs:442` 也有 `app.MapPost("/pair/enroll"…)`,
#    那是自检里的**测试替身**,不是生产端点。把它算进来会凭空多出一批假契约。
_LE_FILE = REPO / "10-core" / "lan-edge" / "Program.cs"
if not _LE_FILE.exists():
    check("★★★ lan-edge 源码根存在(不存在就枚举不出它那 13 条契约)", False, str(_LE_FILE))
else:
    check("★★★ lan-edge 源码根存在(不存在就枚举不出它那 13 条契约)", True)
    _le_src = _LE_FILE.read_text(encoding="utf-8", errors="replace")
    _le = {("lan-edge", m, p) for (m, p) in lan_edge_endpoints(_le_src)}
    actual |= _le
    check(f"★★ lan-edge 端点**零命中判红**(实测 {len(_le)} 条)", len(_le) > 0)

#  ── speech 后端(P5 v1 / D?)—— 第三个跨进程服务,同样要被枚举 ──────────
#  ★★ 为什么必须加这一段:枚举不到的服务,它的契约会被第 ② 组判成
#    「登记了一条已经不存在的契约」(过期登记)⇒ **登记它反而判红**,
#    而唯一能变绿的做法是不登记 —— 那正好把一个新服务放在了账外。
#    枚举源必须跟着"今天有几个跨进程服务"走,不能只有两个写死的。
#  ★ 枚举源取 `10-core/speech/contracts.json` 的 `what` 字段,而不是再写一个源码提取器:
#    那份文件**服务端与消费者都读**,拿它当枚举源比再写一份正则更难说谎;
#    并且下面配了元断言,逐条核对它写的路径在服务端源码里**真的存在**。
_SP_FILE = REPO / "10-core" / "speech" / "server.py"
_SP_REG_FILE = REPO / "10-core" / "speech" / "contracts.json"
if _SP_FILE.exists() and _SP_REG_FILE.exists():
    import json as _json

    _sp_src = _SP_FILE.read_text(encoding="utf-8", errors="replace")
    _sp_reg = _json.loads(_SP_REG_FILE.read_text(encoding="utf-8"))["contracts"]
    _sp = set()
    for _cid, _meta in _sp_reg.items():
        # ★ 变量名避开 _p/_f —— 那是本文件的**全局计数器**(check() 里 global _p)。
        #   第一版这里写了 `_m, _p = ...`,把计数器覆盖成字符串,整个脚本当场崩。
        _sp_m, _sp_p = _meta["what"].split(" ", 1)
        _sp.add(("speech", _sp_m.strip(), _sp_p.strip()))
    actual |= _sp
    check(f"★★ speech 端点**零命中判红**(实测 {len(_sp)} 条)", len(_sp) > 0)
    for _cid, _meta in _sp_reg.items():
        _pth = _meta["what"].split(" ", 1)[1].strip()
        check(f"★★ contracts.json 写的 {_pth} 在 speech 服务端源码里真的存在 —— "
              f"枚举源不许自说自话(否则它成了一份可以随便写、却在决定账面的清单)",
              f'"{_pth}"' in _sp_src, _pth)


# ══════════════════════════════════════════════════════════════════════════
#  ② ★★★ 反向全表 —— 本文件的承重断言
# ══════════════════════════════════════════════════════════════════════════
print("\n=== 2. ★★★ 反向全表:每一个契约都必须登记(新增未登记 ⇒ 判红) ===")

_missing = sorted(actual - set(CONTRACTS))
_stale = sorted(set(CONTRACTS) - actual)

check("★★★ 没有**未登记**的跨进程响应契约 —— "
      "新增一条端点却不登记,默认落【判红】侧,这是本文件唯一的承重设计",
      not _missing, f"未登记:{_missing}")
check("★★ 没有**过期**的登记(登记了一条已经不存在的契约)—— "
      "过期登记会让欠债账虚高,而虚高的账和虚低的账一样不能信",
      not _stale, f"已消失却仍在表里:{_stale}")


# ══════════════════════════════════════════════════════════════════════════
#  ③ 已声明 paired 的,两半都得真的在
# ══════════════════════════════════════════════════════════════════════════
print("\n=== 3. paired 的两半都在(锚点=断言那一行,不是它旁边的注释) ===")

_VALID_STATES = {"paired", "server-only", "client-only", "none"}


def _anchor_count(rel: str, needle: str) -> int | None:
    f = REPO / rel
    if not f.exists():
        return None
    return f.read_text(encoding="utf-8", errors="replace").count(needle)


for key, meta in sorted(CONTRACTS.items()):
    tag = f"{key[0]} {key[1]} {key[2]}"
    st = meta.get("state")
    check(f"[{tag}] state 是四种之一", st in _VALID_STATES, f"实得 {st!r}")
    check(f"[{tag}] 有 lane 与 note(修它的人要知道找谁、改哪一行)",
          bool(meta.get("lane")) and bool(meta.get("note")))
    cid = meta.get("cid")
    check(f"[{tag}] state={st} 与契约号在不在一致(paired 必须给契约号)",
          (st == "paired") == bool(cid), f"state={st} cid={cid!r}")
    if not cid:
        continue
    # ★ 两半都靠**契约号**定位 —— 那正是 GPU 车道那条元断言用的检索目标,
    #   两边用同一个锚点,就不会出现"我以为钉的是这处、他钉的是那处"。
    # ★★ 两半各自住在哪个文件,可以逐条覆盖(V4 加)。默认仍是 GPU 车道那两个 ——
    #   不写 server_file/client_file 的条目行为**一个字节都没变**。
    for side, rel in (("server", meta.get("server_file", _PEER_FILE)),
                      ("client", meta.get("client_file", _CLIENT_PIN_FILE))):
        n = _anchor_count(rel, cid)
        check(f"[{tag}] {side} 半边的文件存在", n is not None, rel)
        if n is None:
            continue
        # ★ 判据是**在不在**,不是"恰好一次":契约号会同时出现在分节注释与断言消息里,
        #   那是正常的(实测 gpu.lease.grant 在 Selftest.cs 里出现 2 次)。
        #   要"恰好一次"会造出一条**必然误红**的判据 —— 而误红的护栏很快就没人看。
        #   真正防漂移的是下面第 ④ 组那条双向对拍。
        check(f"[{tag}] {side} 半边有 {cid} —— 0 次 = 这一半被删了而登记表还说它在",
              n >= 1, f"{rel} 里找不到 {cid}")


# ══════════════════════════════════════════════════════════════════════════
#  ④ 棘轮:债只许变短(★ 只覆盖服务端半边,明写)
# ══════════════════════════════════════════════════════════════════════════
print("\n=== 4. ★★★ 与 GPU 车道那张契约号登记表【双向】对拍 ===")

_peer_src = None
_pf = REPO / _PEER_FILE
if _pf.exists():
    _peer_src = _pf.read_text(encoding="utf-8", errors="replace")
check(f"★★★ 能读到 {_PEER_FILE}(读不到 ⇒ 对拍无从做起 ⇒ 判红,不当作没问题)",
      _peer_src is not None, _PEER_FILE)

if _peer_src is not None:
    _peer_cids = set(_CID_KEY.findall(_peer_src))
    # ★ 零命中判红:他们那张表今天明明有 5 条;解析出 0 条只可能是正则或写法变了,
    #   而"0 条"与"两边完全一致"在下面那条集合相等里长得一模一样(空集 == 空集)。
    check(f"★★ 对方契约号**零命中判红**(实测 {len(_peer_cids)} 条)", len(_peer_cids) > 0,
          f"{sorted(_peer_cids)}")

    # ★★★ 双向对拍**只覆盖服务端半边落在 GPU 那个 peer 文件里的契约**(V4 加)。
    #   为什么必须收窄:证书/配对那一族的服务端半边是 **C#(lan-edge 自检)**,
    #   而 test_gpu_broker.py 是 Python、打的是 FastAPI 网关 —— 它**结构上**测不了 lan-edge。
    #   不收窄的话,那些契约会被要求"契约号也出现在 GPU 那个文件里",
    #   而唯一能满足它的做法是往那张表里塞一个**没有断言体的字符串** ——
    #   那正是 D95 明写要防的「写着有防护、实际没有」。
    #   ⇒ 收窄的是**范围**,不是**判据**:GPU 那一族的双向对拍一个字节没松;
    #     别的车道由它们自己的 server_file 那一半去钉(第 ③ 组已按条目取文件)。
    _peer_scoped = {k: m for k, m in CONTRACTS.items()
                    if m.get("cid") and m.get("server_file", _PEER_FILE) == _PEER_FILE}
    _mine_cids = {m["cid"] for m in _peer_scoped.values()}
    _theirs_only = sorted(_peer_cids - _mine_cids - set(_SUBSHAPE_CIDS))
    _mine_only = sorted(_mine_cids - _peer_cids)

    # ★ 收窄本身也要被钉住,否则它可能悄悄把**所有**条目都排除掉,
    #   而"空集 == 空集"在下面两条集合判据里长得和"完全一致"一模一样(零命中判红的同款教训)。
    check("★★ 对拍范围**非空**:仍有契约的服务端半边落在 GPU peer 文件里(收窄不等于清空)",
          len(_mine_cids) > 0, f"scoped={len(_mine_cids)}")
    _elsewhere = sorted({m["cid"] for m in CONTRACTS.values()
                         if m.get("cid") and m.get("server_file", _PEER_FILE) != _PEER_FILE})
    check("★★ 被收窄排除掉的那些**逐个交代得出去处**(server_file 指向哪个文件由第 ③ 组逐条验)",
          all(CONTRACTS[k].get("server_file") for k in CONTRACTS
              if CONTRACTS[k].get("cid") in _elsewhere),
          f"另有 {len(_elsewhere)} 条在别的服务端文件里")

    check("★★★ 他们新登记的契约号,本表**都跟上了** —— "
          "跟不上说明有人还了债而广度表还把它算在欠债里(账虚高)",
          not _theirs_only, f"他们有而本表没有:{_theirs_only}")
    check("★★★ 本表标 paired 的契约号,他们那儿**都还在** —— "
          "不在说明那半钉子被拔了,而本表还在说它成对(写着有防护、实际没有)",
          not _mine_only, f"本表有而他们没有:{_mine_only}")

    # ★ 子形状条目必须**逐条写明理由**,并且确实还在他们表里(否则是过期登记)。
    # ★ 逐条按它**自己声明的 home** 去找,而不是一律去 GPU 那个 peer 文件里找。
    #   收窄的是"去哪儿找",不是"要不要找" —— 找不到照样红。
    for _sc, _meta in _SUBSHAPE_CIDS.items():
        _home = _meta["home"]
        _n = _anchor_count(_home, _sc)
        check(f"★★ 子形状条目 {_sc} 的 home 文件存在({_home})", _n is not None, _home)
        check(f"★★ 子形状条目 {_sc} 仍在 {_home} 里(不在 = 过期登记)",
              _n is not None and _n >= 1, f"{_sc} 已从 {_home} 消失")
        check(f"★★ 子形状条目 {_sc} 写明了**为什么不抵消欠债**", bool(_meta.get("why")))

    # ★ 客户端那半边也零命中判红 —— 他们的元断言靠 `cid in Selftest.cs`,
    #   而"读到的是一个空串"和"每条都找得到"在那种判据下同样绿。
    _cf = REPO / _CLIENT_PIN_FILE
    _client_cids = set(_CID_BARE.findall(_cf.read_text(encoding="utf-8", errors="replace"))) \
        if _cf.exists() else set()
    check(f"★★ 客户端半边的契约号**零命中判红**(实测 {len(_client_cids)} 条)",
          len(_client_cids) > 0, f"{sorted(_client_cids)}")


# ══════════════════════════════════════════════════════════════════════════
#  ⑤ 欠债账 —— 逐条列出,绝不静默省略
# ══════════════════════════════════════════════════════════════════════════
print("\n=== 5. 欠债账(缺配对的逐条列出,并算出它有没有消费者) ===")

_client_src = ""
_client_files = 0
for root in CLIENT_ROOTS:
    d = REPO / root
    if not d.exists():
        continue
    for f in d.rglob("*.cs"):
        if re.search(r"[\\/](bin|obj)[\\/]", str(f)):
            continue
        _client_src += f.read_text(encoding="utf-8", errors="replace")
        _client_files += 1
check("★★ 客户端源码根**零命中判红**(读不到 .cs 时,'有没有消费者'会一律算成"
      "'没有',那是一个看起来很有信息量的假答案)",
      _client_files > 0, f"实测 {_client_files} 个 .cs")

_debt = sorted(k for k, m in CONTRACTS.items() if m.get("state") != "paired")
_by_lane: dict[str, list] = {}
for k in _debt:
    _by_lane.setdefault(CONTRACTS[k].get("lane", "?"), []).append(k)

#  ★ --quiet:门禁里只印**每条车道欠几条**,不印 26 段说明。
#    理由写在 run-tests.ps1 自己的注释里:「整屏噪音把真信号淹掉」。
#    ★ 但**数字一条都不许省** —— 省掉的是 note,不是账。
_QUIET = "--quiet" in sys.argv

for lane in sorted(_by_lane):
    print(f"\n  [{lane}] 欠 {len(_by_lane[lane])} 条")
    if _QUIET:
        for k in _by_lane[lane]:
            print(f"    - {k[1]:5} {k[2]}")
        continue
    for k in _by_lane[lane]:
        m = CONTRACTS[k]
        # ★ 判据要**恰好**这条路径,不能是它的前缀:`/v1/gpu/lease` 是
        #   `/v1/gpu/lease/renew` 的前缀,裸 `in` 会把后者算成前者的消费者
        #   (ASSERTION-PITFALLS 第 4 条第 1 例:前者是后者的前缀)。
        # ★ 收尾允许 `?`:客户端真的写着 `"/v1/sync/events?device=" + …`,
        #   只认收尾引号会把一个**真有**消费者的契约报成"无消费者" ——
        #   那是个看起来很有信息量的假答案,比不报更坏。
        consumed = _client_files > 0 and bool(
            re.search('"' + re.escape(k[2]) + '["?]', _client_src))
        mark = "  " if consumed else "  ! 无消费者 "
        print(f"    - {k[1]:5} {k[2]:26} state={m['state']}{mark}")
        print(f"        {m['note']}")

check(f"★★ 欠债总数与登记表对得上(实测 {len(_debt)},期望 {_EXPECTED_DEBT})—— "
      "对不上说明有人动了表却没动这个数字,而覆盖账印的正是这个数字",
      len(_debt) == _EXPECTED_DEBT, f"实测 {len(_debt)} 期望 {_EXPECTED_DEBT}")

# ★★ 成对数从**登记表**数出来,**不用 `总数 - 欠债` 去减**。
#   第一版是减出来的,而 `总数` 来自实测枚举、`欠债` 来自登记表 ——
#   两者一分家(也就是**恰好在有未登记契约、本工具正在报问题的那一刻**),
#   减出来的数字就是错的。实测撞见过:27 条契约、登记表只有 1 条 paired,
#   而它印出 `PAIRED=2`。**一个只在出问题时才出错的计数器**,
#   偏偏在最需要它准的那一刻说谎 —— 那正是这个工具存在的理由。
_paired = sum(1 for m in CONTRACTS.values() if m.get("state") == "paired")
print(f"\n  ★ 契约总数 {len(actual)} · 已成对 {_paired} · 欠配对 {len(_debt)}"
      + ("" if len(actual) == len(CONTRACTS)
         else f"  ★ 注意:实测 {len(actual)} 条 ≠ 登记 {len(CONTRACTS)} 条,上面已判红"))
print("  ★ 「欠配对」不是「已裁定没问题」—— 它是一张**只许变短**的欠债表。"
      "新增契约不登记会判红,还了债不改表也会判红。")
if _QUIET:
    print("  ★ 每条欠债的消费者与后果:python 90-ops\\gate\\check_contract_pairs.py(不带 --quiet)")

#  ★★ 给机器读的那一行**只用 ASCII**(ASSERTION-PITFALLS 第 8 条,已踩 3 次)。
#    run-tests.ps1 要把 DEBT 抬进覆盖账,而钩子是从 git bash 起的、控制台码页 cp936
#    ⇒ 中文与 `·` 全成乱码,正则匹配不上。`===` / 数字 / 大写字母乱码之后依然完好。
print(f"  === contract-pairs: TOTAL={len(actual)} "
      f"PAIRED={_paired} DEBT={len(_debt)} ===")


# ══════════════════════════════════════════════════════════════════════════
#  ⑥ 提取器自己被反过来验 —— **两个方向都要钉,只钉一边等于没钉**
#
#  上面每一组都建立在"提取器没有静默失灵"之上。而一个匹配不到任何东西的正则
#  会让契约表变空、让所有断言退化成零断言,**而输出看起来一切正常**
#  (ASSERTION-PITFALLS 第 4 条推论:判据太宽会静默变成【零断言】)。
#  ⇒ 拿合成输入两个方向各问一遍:该抓的必须抓到,不该抓的必须放过。
# ══════════════════════════════════════════════════════════════════════════
print("\n=== 6. 提取器两个方向都被钉住(能抓 + 不误抓) ===")

_EXTRACTOR_CASES = [
    # (合成源码, 期望取出的集合, 说明)
    ('app.MapPost("/pair/enroll", async (HttpContext ctx) =>',
     {("POST", "/pair/enroll")}, "★ 正向:真端点必须抓到"),
    ('app.MapGet( "/admin/ping", (HttpContext c) =>',
     {("GET", "/admin/ping")}, "★ 正向:括号后有空格也要抓到"),
    ('upstream.MapGet("/__sse_probe", async (HttpContext c) =>',
     set(), "★★ 反向:`upstream.` 是自检里的上游替身,不是生产端点"),
    ('myapp.MapPost("/not/a/route", x)',
     set(), "★★ 反向:`myapp.` 不是 `app.` —— \\b 词边界必须挡住它"),
    ('// app.MapPost("/pair/enroll", …) 这行是注释里讲解用的',
     {("POST", "/pair/enroll")}, "! 已知不足:注释里的调用会被算成端点(见下条)"),
]
for _src, _want, _why in _EXTRACTOR_CASES:
    check(f"提取器:{_why}", lan_edge_endpoints(_src) == _want,
          f"实得 {sorted(lan_edge_endpoints(_src))} 期望 {sorted(_want)}")

# ★ 最后那条是**如实登记的一处不足**,不是护栏:提取器不去注释。
#   为什么今天不修:去注释器要么用第三方 C# 解析,要么再手写一份 ——
#   而"没有测试文件自己再写一份去注释器"是本仓已有的一条反向全表断言。
#   ⇒ 代价方向也是安全的:注释里写一条假端点,只会让契约表**多**一条要登记的,
#     判红方向是"多要一条配对",不是"少盖一条"。**少盖才是不能忍的那个方向。**
check("★ 上面那处不足的**代价方向**是安全的(多算 ⇒ 多要一条登记;不会少盖)",
      lan_edge_endpoints('// app.MapGet("/x", y)') == {("GET", "/x")})

# ══════════════════════════════════════════════════════════════════════════
#  ⑦ ★★★ 全仓契约号反向全表 —— 补上第 ④ 组**空着的那个方向**
#
#  本文件顶部(以及 ④ 组的抬头)白纸黑字写着两个方向:
#      「对方新登记一条契约号而本表没跟上 ⇒ 红;本表标 paired 而对方那儿没有 ⇒ 红。」
#  ★★★ **第一个方向此前只对着一个文件**(`test_gpu_broker.py`)。
#     于是在**别处**声明的契约号 —— `WireContracts.cs`、`test_sync.py`、`Selftest.cs` ——
#     它一个都看不见:代码里躺着 10 个本表不认识的契约号,门禁照报 189 PASS · 0 FAIL。
#
#  ⇒ 一张**专门用来抓「看着有防护、实际没有」的表,自己有一个方向是空的**。
#    与 2026-08-05 抓到的「一条为了修谎言而写的断言,自己是恒真的」同形状,
#    而这次它长在 **D95** 上 —— 当天刚立的那条决议。
#    ★★ 更要紧的是它**怎么被发现的**:靠人工核对,**不是它自己报的**。
#
#  修法:扫**全仓代码**(不是某一个 peer 文件),任何 `CONTRACT:<id>` 只要本表不认识
#  ⇒ 判红,并说清是哪个 id、在哪些文件里。
#
#  ★ 反问过一遍:**新增一个契约号而不登记,默认落哪边?** —— 落**判红**侧。
#    这与本文件顶部那条「加了路由不登记 ⇒ 红」是同一条纪律的另一半:
#    那条管**路由**,这条管**契约号**。少了这一半,一条契约可以有断言、有契约号,
#    却**从来不进欠债账** —— 账面上它不存在,而它确实在被人依赖。
#
#  ★ 只扫 `.py` / `.cs`(代码与测试),**不扫 `.md`**:决议包会在散文里提到契约号,
#    包括**建议中的、尚未存在的**那种。把散文纳入判据会逼人去登记一个还没有的东西。
#    ⇒ 判据是「**代码里出现的**契约号必须被登记」,这一条边界明写在此。
# ══════════════════════════════════════════════════════════════════════════
print("\n=== 7. ★★★ 全仓契约号反向全表(补上 ④ 组空着的那个方向) ===")

_SCAN_SUFFIXES = (".py", ".cs")
_SCAN_ROOTS = ["10-core", "20-client-win", "90-ops"]
_SELF_REL = "90-ops/gate/check_contract_pairs.py"

#  已登记 = 路由条目的 cid ∪ 子形状条目
_known_cids = {m["cid"] for m in CONTRACTS.values() if m.get("cid")} | set(_SUBSHAPE_CIDS)

#  ★ 带尾点的是**前缀表达式**(例如 Selftest.cs 里的 StartsWith("CONTRACT:cert.admin.")),
#    不是一个契约号。判据相应不同:必须**至少有一个已登记的 id 以它开头** ——
#    否则那句 StartsWith 会静默匹配到零条,而"筛出零条"和"筛出全部"在断言里长得一样。
_seen: dict[str, set[str]] = {}
for _root in _SCAN_ROOTS:
    _rd = REPO / _root
    if not _rd.exists():
        continue
    for _fp in _rd.rglob("*"):
        if _fp.suffix.lower() not in _SCAN_SUFFIXES or not _fp.is_file():
            continue
        _rel = _fp.relative_to(REPO).as_posix()
        if _rel == _SELF_REL or "/bin/" in _rel or "/obj/" in _rel or "__pycache__" in _rel:
            continue
        try:
            _txt = _fp.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        for _hit in set(_CID_BARE.findall(_txt)):
            _seen.setdefault(_hit, set()).add(_rel)

#  ★★ 零命中判红:今天代码里明明有几十处;解析出 0 只可能是正则/扫描根坏了,
#     而"0 个未登记"与"全部已登记"在下面那条判据里长得一模一样(空集 ⊆ 任何集合)。
check(f"★★ 全仓契约号**零命中判红**(实测扫到 {len(_seen)} 个不同契约号)",
      len(_seen) > 0, f"扫描根={_SCAN_ROOTS}")

_unregistered: list[str] = []
_dangling_prefix: list[str] = []
for _cid, _files in sorted(_seen.items()):
    if _cid.endswith("."):
        # 前缀表达式:必须真的能匹配到已登记的 id
        if not any(k.startswith(_cid) for k in _known_cids):
            _dangling_prefix.append(f"{_cid} ({', '.join(sorted(_files))})")
        continue
    if _cid not in _known_cids:
        _unregistered.append(f"{_cid} ({', '.join(sorted(_files))})")

check("★★★ 代码里出现的契约号,本表**全都认识** —— "
      "不认识的说明有人写了成对断言却没进欠债账:账面上它不存在,而它确实在被依赖",
      not _unregistered,
      "未登记 %d 个:\n      %s" % (len(_unregistered), "\n      ".join(_unregistered)))

check("★★ 前缀表达式(以 `.` 结尾)必须真能匹配到已登记的 id —— "
      "匹配到零条的 StartsWith 会静默筛出空集,而空集在断言里长得像'全过了'",
      not _dangling_prefix,
      "悬空前缀:\n      %s" % "\n      ".join(_dangling_prefix))

#  ★ 反过来钉(能报 + 不误报,只钉一边等于没钉 —— selfcheck.py 已踩出这条经验):
#    喂一段**确定该报**的、和一段**确定不该报**的,各问一次。
def _unknown_in(text: str) -> list[str]:
    """把上面那套判据抽成纯函数,用真实的 _known_cids 问一遍合成输入。"""
    out = []
    for c in set(_CID_BARE.findall(text)):
        if c.endswith("."):
            continue
        if c not in _known_cids:
            out.append(c)
    return out


#  ★ 探针字符串**运行期拼接**:本文件自己被扫描时会跳过(_SELF_REL),
#    但别的扫描器不一定跳 —— 拼接后 `CONTRACT:` 在**值**里是连续的,而在**源码文本**里不是。
#    ★★ 第一版把它写成 `"CONTRACT" ":zzz…"`(字面量里夹了引号和空格),
#      于是值里 `CONTRACT:` 也不连续 ⇒ 正则匹配不到 ⇒ 这条反向断言**当场红**。
#      它红得对:一个"该报却报不出来"的判据本来就该红。错的是探针,不是被判的东西。
_probe_bad = 'Assert(x, "' + "CONTRACT" + ":zzz.definitely.not.registered" + '");'
_probe_good = '// ── ' + "CONTRACT" + ":gpu.lease.grant ──"
check("★★ 反向:喂一个表里没有的契约号 ⇒ **报得出来**(判据不是恒真的)",
      _unknown_in(_probe_bad) == ["CONTRACT" ":zzz.definitely.not.registered"],
      f"{_unknown_in(_probe_bad)}")
check("★★ 反向:喂一个已登记的契约号 ⇒ **不误报**(判据不是恒假的)",
      _unknown_in(_probe_good) == [], f"{_unknown_in(_probe_good)}")

print("-" * 78)
print(f"  === 契约配对元规则:{_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
