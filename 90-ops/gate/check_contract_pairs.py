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

 ① 客户端那半边**没有通用形状可数**(服务端半边有:`set(...keys()) ==`)。
    ⇒ 第 ④ 组的"债只许变短"棘轮**只覆盖服务端半边**。客户端半边靠 `CONTRACTS`
      里的锚点逐条钉,新增的客户端解析断言不会自动被发现。
 ② 只认锚点**在不在、唯不唯一**;锚点选的是**断言本身那一行**(不是它旁边的注释),
    所以"锚点在"就等于"那条断言在"。但它不判断那条断言**判得对不对** ——
    那是那条断言自己的事。
 ③ 请求体(客户端发、服务端收)方向**不在本文件范围内**。D92 的措辞是"响应契约"。
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
#  state : paired      两半都在(必须同时给 server 与 client 锚点)
#          server-only 只有服务端钉了顶层键集合
#          client-only 只有客户端钉了解析
#          none        两半都没有
#  anchor: (相对仓库根的文件, **断言本身那一行**里的一段 ASCII 子串)
#          ★ 锚点故意选断言那一行而不是旁边的注释 —— 注释可以留着而断言被删掉,
#            那正是本项目最恨的形状(写着有防护、实际没有)。
#          ★ 锚点必须是 ASCII:它要在 cp936 的钩子环境里被同一份代码读,
#            而中文子串在那里的行为取决于读文件时的编码猜测(ASSERTION-PITFALLS 8)。
#  lane  : 这条契约归哪条垂直切片(D92)。★ 这是**建议**,以第 0 条车道的裁定为准。
#  note  : 为什么它现在是这个状态 / 修它要动哪一行。
#
#  ★★★ 这张表**只许变短**(欠债那一栏)。第 ④ 组的棘轮盯着服务端半边:
#     有人新钉了一条顶层键集合断言却没把对应契约挪进 paired ⇒ 判红。
# ══════════════════════════════════════════════════════════════════════════
CONTRACTS: dict[tuple[str, str, str], dict] = {
    # ── 网关(Python / FastAPI)⇒ 客户端(C#) ─────────────────────────────
    ("gateway", "POST", "/v1/gpu/lease"): {
        "state": "paired",
        "server": ("10-core/gateway/test_gpu_broker.py", "set(_lzj.keys()) =="),
        "client": ("20-client-win/app/Selftest.cs", "TryParseGrant(wire"),
        "lane": "GPU/租约切片",
        "note": "A1 的病灶本身,已成对。本表的样板。",
    },
    ("gateway", "GET", "/v1/gpu/snapshot"): {
        "state": "none", "lane": "GPU/租约切片",
        "note": "消费者 Services/LeaseKeeper.cs:133 —— 它拿 generation 去发租约;"
                "generation 读错 = 每次 if_generation 都冲突,而那看起来像'中枢忙'",
    },
    ("gateway", "POST", "/v1/gpu/lease/renew"): {
        "state": "none", "lane": "GPU/租约切片",
        "note": "消费者 Services/LeaseKeeper.cs:264 —— 续不上租约会在 TTL 后静默消失,"
                "那时中枢以为没人在用。★ 与 /v1/gpu/lease 同族,漏的正是 A1 那种缝",
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
        "state": "none", "lane": "GPU/租约切片",
        "note": "消费者 Services/HubClient.cs:241 与 LeaseKeeper.cs:249。"
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
        "note": "消费者 transport/ClientTransport.cs:389 —— ★ D92 点名的 A5 就在这一族:"
                "服务端写了 CertLifecycle/RenewDeviceCertIfDue,客户端 HubClient.cs 那一行至今没人改",
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

# ── 服务端顶层键集合断言的**唯一**通用形状。第 ④ 组的棘轮数它。─────────────
#  ★ 针**拼出来**,不写成字面量 —— 否则本文件自己就成了它要找的东西
#    (ASSERTION-PITFALLS 第 1 条,已踩 9 次;第 8 次带出的正是这个写法)。
#    拼出来之后,这个正则可以**连本文件一起**扫而不必开"跳过自己"那个 fail-open 后门。
_KEYSET_RE = re.compile(r"set\(" + r"[^)]*\.keys\(\)\)\s*==")

#  反向全表:哪些文件里**允许**出现服务端顶层键集合断言,各几条。
#  新增一条而不更新这张表 ⇒ 判红(说明有人还了债却没改登记表)。
_KEYSET_PINS_EXPECTED = {
    "10-core/gateway/test_gpu_broker.py": 1,          # POST /v1/gpu/lease(A1)
}

#  欠债总数钉死 —— 印在覆盖账上的那个数字必须和实际对得上。
#  ★ 它不是重复登记表:它让"又欠了一条"变成 diff 里的**一行**,而不是表里多一项没人数。
_EXPECTED_DEBT = 25


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
    for side in ("server", "client"):
        want = st == "paired" or st == f"{side}-only"
        has = side in meta
        check(f"[{tag}] state={st} 与 {side} 锚点在不在一致",
              want == has, f"want={want} has={has}")
        if not has:
            continue
        rel, needle = meta[side]
        n = _anchor_count(rel, needle)
        check(f"[{tag}] {side} 锚点所在文件存在", n is not None, rel)
        if n is None:
            continue
        # 0 ⇒ 这一半被删了(而 state 还写着 paired,那就是"写着有防护、实际没有")
        # >1 ⇒ 说不清验的是哪一处;两处会各自漂移,而漂的那天只盯着其中一份
        check(f"[{tag}] {side} 锚点恰好出现 1 次 —— "
              f"0 次 = 这一半被删了而登记表还说它在;>1 次 = 说不清钉的是哪一处",
              n == 1, f"{rel} 里 {needle!r} 出现 {n} 次")


# ══════════════════════════════════════════════════════════════════════════
#  ④ 棘轮:债只许变短(★ 只覆盖服务端半边,明写)
# ══════════════════════════════════════════════════════════════════════════
print("\n=== 4. 棘轮:还了债必须改登记表(★ 只覆盖服务端半边) ===")

_py_tests = sorted(p for p in (REPO / "10-core").rglob("test_*.py")
                   if "__pycache__" not in str(p))
check("★★ 服务端测试**零命中判红**(扫不到测试文件时,下面整组会静默变成零断言)",
      len(_py_tests) > 0, f"实测 {len(_py_tests)} 个")

_keyset_actual: dict[str, int] = {}
for f in _py_tests:
    n = len(_KEYSET_RE.findall(f.read_text(encoding="utf-8", errors="replace")))
    if n:
        _keyset_actual[f.relative_to(REPO).as_posix()] = n

check("★★★ 服务端顶层键集合断言的分布**逐条对得上** —— "
      "多出一条 = 有人还了债却没把契约挪进 paired;少一条 = 有人把钉子拔了",
      _keyset_actual == _KEYSET_PINS_EXPECTED,
      f"实测 {_keyset_actual} 期望 {_KEYSET_PINS_EXPECTED}")

# ★ 期望表与登记表必须自洽:paired/server-only 的条数 == 期望的钉子总数。
#   没有这条,两张表可以各自"正确"而互相矛盾。
_server_halves = sum(1 for m in CONTRACTS.values()
                     if m.get("state") in ("paired", "server-only"))
check("★★ 期望表与登记表自洽(钉子总数 == 声明有服务端半边的契约数)",
      sum(_KEYSET_PINS_EXPECTED.values()) == _server_halves,
      f"钉子 {sum(_KEYSET_PINS_EXPECTED.values())} 契约 {_server_halves}")


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

print(f"\n  ★ 契约总数 {len(actual)} · 已成对 {len(actual) - len(_debt)} · "
      f"欠配对 {len(_debt)}")
print("  ★ 「欠配对」不是「已裁定没问题」—— 它是一张**只许变短**的欠债表。"
      "新增契约不登记会判红,还了债不改表也会判红。")
if _QUIET:
    print("  ★ 每条欠债的消费者与后果:python 90-ops\\gate\\check_contract_pairs.py(不带 --quiet)")

#  ★★ 给机器读的那一行**只用 ASCII**(ASSERTION-PITFALLS 第 8 条,已踩 3 次)。
#    run-tests.ps1 要把 DEBT 抬进覆盖账,而钩子是从 git bash 起的、控制台码页 cp936
#    ⇒ 中文与 `·` 全成乱码,正则匹配不上。`===` / 数字 / 大写字母乱码之后依然完好。
print(f"  === contract-pairs: TOTAL={len(actual)} "
      f"PAIRED={len(actual) - len(_debt)} DEBT={len(_debt)} ===")


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
