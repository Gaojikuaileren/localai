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
        "state": "none", "lane": "GPU/租约切片",
        "note": "消费者 Services/LeaseKeeper.cs:133 —— 它拿 generation 去发租约;"
                "generation 读错 = 每次 if_generation 都冲突,而那看起来像'中枢忙'",
    },
    ("gateway", "POST", "/v1/gpu/lease/renew"): {
        "state": "paired", "cid": "CONTRACT:gpu.lease.renew",
        "lane": "GPU/租约切片",
        "note": "顶层键集合 {result, snapshot};客户端半边钉 409(条件写不匹配)⇒ 立刻自隐",
    },
    ("gateway", "GET", "/v1/gpu/events"): {
        "state": "none", "lane": "GPU/租约切片",
        "note": "SSE。消费者 Services/HubGpu.cs:181。★ SSE 的契约是**每一帧**的顶层键集合,"
                "不是整个响应体 —— 服务端那半要钉帧的形状",
    },
    ("gateway", "GET", "/v1/gpu/components"): {
        "state": "none", "lane": "GPU/租约切片",
        "note": "消费者 Services/HubGpu.cs:309 —— 挑选面板的数据源。"
                "解析漂了就退回客户端自己编一份清单,而客户端**已经编过一份**(第三套词汇)",
    },
    ("gateway", "POST", "/v1/gpu/intended"): {
        "state": "none", "lane": "GPU/租约切片",
        "note": "消费者 Services/HubGpu.cs:367 —— 「点确定」那一次事务。"
                "失败要回带 snapshot,客户端读不出 snapshot 就无从重试",
    },
    ("gateway", "POST", "/v1/session/end"): {
        "state": "paired", "cid": "CONTRACT:session.end",
        "lane": "GPU/租约切片",
        "note": "顶层键集合 {status, released_leases, device, reason}。"
                "★ 这条路由曾经**根本不存在**而客户端每次退出都在调它、失败还被吞掉",
    },
    ("gateway", "GET", "/v1/models"): {
        "state": "none", "lane": "未分配 —— 请第 0 条车道指派",
        "note": "消费者 Services/HubClient.cs:290 与 transport/Program.cs:93",
    },
    ("gateway", "POST", "/v1/chat/completions"): {
        "state": "none", "lane": "未分配 —— 请第 0 条车道指派",
        "note": "SSE。消费者 Services/ChatClient.cs:87 —— 全项目最热的一条路径",
    },
    ("gateway", "POST", "/v1/sync/push"): {
        "state": "none", "lane": "未分配 —— 请第 0 条车道指派",
        "note": "消费者 Services/SyncClient.cs:249",
    },
    ("gateway", "GET", "/v1/sync/events"): {
        "state": "none", "lane": "未分配 —— 请第 0 条车道指派",
        "note": "SSE。消费者 Services/SyncClient.cs:327",
    },
    ("gateway", "GET", "/v1/sync/snapshot"): {
        "state": "none", "lane": "未分配 —— 请第 0 条车道指派",
        "note": "★★ **客户端一个字都没读它**(全仓 grep 零命中)。"
                "推/订阅都在,唯独没有拉全量 ⇒ 重连后拿什么对齐?这条要么接上要么撤掉",
    },
    ("gateway", "GET", "/health"): {
        "state": "none", "lane": "未分配 —— 请第 0 条车道指派",
        "note": "★ 客户端不读它;90-ops\\start-stack.ps1:144 只看 curl 退出码、**不解析响应体**。"
                "⇒ 今天它的响应体没有任何消费者,顶层键集合改成什么都不会有人红",
    },

    # ── lan-edge(C# / Kestrel)⇒ 客户端(C#) ────────────────────────────
    #  ★ 同语言不等于同进程。D92 的措辞是「跨**进程**响应契约」,
    #    而 A1 那条缝的成因是"两边各自都绿",与语言无关。
    ("lan-edge", "POST", "/pair/enroll"): {
        "state": "none", "lane": "证书/配对切片",
        "note": "消费者 transport/ClientTransport.cs:164。"
                "★ 应答里带主机 SAS 词表版本 —— transport/Program.cs:163 钉了**那一个字段**,"
                "不是顶层键集合(单字段断言挡不住别的键漂移)",
    },
    ("lan-edge", "POST", "/pair/status"): {
        "state": "none", "lane": "证书/配对切片",
        "note": "消费者 transport/ClientTransport.cs:224 —— 握手失败会让设备**永远停在 provisioning**",
    },
    ("lan-edge", "POST", "/pair/claim"): {
        "state": "none", "lane": "证书/配对切片",
        "note": "消费者 transport/ClientTransport.cs:235",
    },
    ("lan-edge", "POST", "/pair/complete"): {
        "state": "none", "lane": "证书/配对切片",
        "note": "消费者 transport/ClientTransport.cs:241",
    },
    ("lan-edge", "POST", "/identity/renew/enroll"): {
        "state": "none", "lane": "证书/配对切片",
        "note": "消费者 transport/ClientTransport.cs:389 —— ★ 审计 A5 曾在这一族:"
                "服务端写了 CertLifecycle/RenewDeviceCertIfDue 而客户端零调用点。"
                "**A5 已于 2026-08-06 随 V1(D93)闭环**(HubClient.cs 实测 4 处调用),"
                "但**这条契约的欠债没还** —— 接上了调用点 ≠ 有了成对断言,两件事别混。",
    },
    ("lan-edge", "POST", "/identity/renew/complete"): {
        "state": "none", "lane": "证书/配对切片",
        "note": "消费者 transport/ClientTransport.cs:426。"
                "lan-edge/Program.cs:807 钉了 `changed` 一个字段,不是顶层键集合",
    },
    ("lan-edge", "GET", "/admin/ping"): {
        "state": "none", "lane": "证书/配对切片",
        "note": "消费者 Services/HubAdmin.cs:125 —— pairingWindowOpen 从这里来;"
                "读错就退回'本地布尔替中枢记配对窗口开没开'(Selftest.cs:5923 明令禁止的那件事)",
    },
    ("lan-edge", "GET", "/admin/pairing/pending"): {
        "state": "none", "lane": "证书/配对切片",
        "note": "消费者 Services/HubAdmin.cs:197",
    },
    ("lan-edge", "POST", "/admin/pairing/approve"): {
        "state": "none", "lane": "证书/配对切片",
        "note": "消费者 Services/HubAdmin.cs:220",
    },
    ("lan-edge", "POST", "/admin/pairing/deny"): {
        "state": "none", "lane": "证书/配对切片",
        "note": "消费者 Services/HubAdmin.cs:223",
    },
    ("lan-edge", "POST", "/admin/pairing/window"): {
        "state": "none", "lane": "证书/配对切片",
        "note": "消费者 Services/HubAdmin.cs:230",
    },
    ("lan-edge", "GET", "/admin/devices"): {
        "state": "none", "lane": "证书/配对切片",
        "note": "**两个**消费者:Services/HubAdmin.cs:238 与 Services/HubClient.cs:310 —— "
                "★ 两处各自解析同一个形状,漂移时只会有一处被发现",
    },
    ("lan-edge", "POST", "/admin/devices/revoke"): {
        "state": "none", "lane": "证书/配对切片",
        "note": "**两个**消费者:Services/HubAdmin.cs:255 与 Services/HubClient.cs:328(同上)",
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
_SUBSHAPE_CIDS = {
    "CONTRACT:gpu.intended.blocking":
        "钉的是 POST /v1/gpu/intended **409** 响应里 result.blocking[i] 的子对象形状"
        "(即 Lease.to_json()),**不是**该路由 200 的顶层键集合 ⇒ 不抵消它的欠债",
}

#  欠债总数钉死 —— 印在覆盖账上的那个数字必须和实际对得上。
#  ★ 它不是重复登记表:它让"又欠了一条"变成 diff 里的**一行**,而不是表里多一项没人数。
_EXPECTED_DEBT = 23


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
    for side, rel in (("server", _PEER_FILE), ("client", _CLIENT_PIN_FILE)):
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

    _mine_cids = {m["cid"] for m in CONTRACTS.values() if m.get("cid")}
    _theirs_only = sorted(_peer_cids - _mine_cids - set(_SUBSHAPE_CIDS))
    _mine_only = sorted(_mine_cids - _peer_cids)

    check("★★★ 他们新登记的契约号,本表**都跟上了** —— "
          "跟不上说明有人还了债而广度表还把它算在欠债里(账虚高)",
          not _theirs_only, f"他们有而本表没有:{_theirs_only}")
    check("★★★ 本表标 paired 的契约号,他们那儿**都还在** —— "
          "不在说明那半钉子被拔了,而本表还在说它成对(写着有防护、实际没有)",
          not _mine_only, f"本表有而他们没有:{_mine_only}")

    # ★ 子形状条目必须**逐条写明理由**,并且确实还在他们表里(否则是过期登记)。
    for _sc, _why in _SUBSHAPE_CIDS.items():
        check(f"★★ 子形状条目 {_sc} 仍在对方表里(不在 = 过期登记)",
              _sc in _peer_cids, f"{_sc} 已从 {_PEER_FILE} 消失")
        check(f"★★ 子形状条目 {_sc} 写明了**为什么不抵消欠债**", bool(_why))

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

print("-" * 78)
print(f"  === 契约配对元规则:{_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
