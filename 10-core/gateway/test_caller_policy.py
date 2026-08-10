"""网关调用方策略测试(D30)。用 stub request + monkeypatch 身份解析,
只测策略判定(身份解析本身由 test_caller_identity.py 对真实连接验)。
跑:python test_caller_policy.py
"""
import sys

# ★★ 编码双保险(与 P4-S0 同源):干净的 cp936 控制台编不出 ⇒ / ✓ / ★ 之类的字符,
#   而 print 一抛异常会把整套脚本掀翻 —— 于是【一条断言变红】表现成【整套崩溃】,
#   运行器只看到"没有汇总行",看不出是哪条没守住。
#   S0 当年修的是 vram_gate 的生产路径,测试脚本这边一直没修 —— 2026-08-05 补上。
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass
import gateway

_p = _f = 0


def check(name, cond):
    global _p, _f
    if cond:
        _p += 1
    else:
        _f += 1
        print(f"  FAIL: {name}")


class Req:
    def __init__(self, host, port=5000, headers=None):
        self.client = type("C", (), {"host": host, "port": port})()
        self.headers = headers or {}


def set_ident(acct):
    gateway.caller_identity.account_from_request = lambda req: acct


print("=== 隔离账户 ai-asset:必须拒 ===")
set_ident(("HONGKONGPINGPON\\ai-asset", "ai-asset"))
check("classify → denied-account", gateway.classify_caller(Req("127.0.0.1")) == "denied-account")
check("require_trusted_local → None", gateway.require_trusted_local(Req("127.0.0.1")) is None)

print("=== 隔离账户 ai-exec:必须拒 ===")
set_ident(("HONGKONGPINGPON\\ai-exec", "ai-exec"))
check("ai-exec → denied-account", gateway.classify_caller(Req("127.0.0.1")) == "denied-account")

print("=== 人类账户:放行 ===")
set_ident(("HONGKONGPINGPON\\Zori Ma", "Zori Ma"))
check("classify → trusted-local", gateway.classify_caller(Req("127.0.0.1")) == "trusted-local")
check("require_trusted_local → 返回身份", gateway.require_trusted_local(Req("127.0.0.1")) is not None)

print("=== ai-mem 自己(如 memory-service 调网关):放行 ===")
set_ident(("HONGKONGPINGPON\\ai-mem", "ai-mem"))
check("ai-mem → trusted-local", gateway.classify_caller(Req("127.0.0.1")) == "trusted-local")

print("=== 解析不到身份:两条路径都 fail-closed(2026-08-03 改)===")
# ★ 本节的旧断言是「classify fail-open → trusted-local」,记录的是 D30 的原始判据:
#   denylist —— 不在 LOCAL_DENY_ACCOUNTS 就给 trusted-local,解析不到也给。
#   实测推翻了它的前提:本机已存在 CodexSandboxOffline / CodexSandboxOnline
#   两个外部 AI 沙箱账户(Enabled · 在 Users 组 · 不在拒绝表),按旧判据它们就是最高档,
#   而 trusted-local 是 E1_OVERRIDE_ALLOWED_TIERS 与 _ALLOWED_CALLERS["S2"] 的唯一成员。
#   ⇒ 判据改为 allowlist(config/caller-accounts.toml),此断言随之改写。
#   ★ 降档【不断连】:unregistered-local 仍能用 chat(等价于直连 llama-server,不构成回归),
#     它失去的只是 E1 解除权与 S2 正文权。
#   ★★★★ V32b(用户裁定 2026-08-10):这一行**又改了一次** ——
#     「解析不到」不再落 `unregistered-local`,它有了自己的档位 `identity-unresolved`。
#     理由见 gateway.classify_caller 上方那段:那两件事的正确处置**方向相反**,
#     而共用一个档位意味着一次瞬时抖动会把**机主本人**静默关进"未登记"里。
set_ident(None)
check("classify 解析不到 → identity-unresolved(既不是 trusted-local,**也不再是 unregistered-local**)",
      gateway.classify_caller(Req("127.0.0.1")) == gateway.IDENTITY_UNRESOLVED_TIER)
check("★ 解析不到的调用方拿不到 E1 解除权",
      gateway.classify_caller(Req("127.0.0.1")) not in gateway.E1_OVERRIDE_ALLOWED_TIERS)
check("require_trusted_local fail-closed → None", gateway.require_trusted_local(Req("127.0.0.1")) is None)

print("=== 未登记的本机账户(如外部 AI 沙箱):降档,不放行到最高档 ===")
set_ident(("HONGKONGPINGPON\\CodexSandboxOnline", "CodexSandboxOnline"))
check("CodexSandboxOnline → unregistered-local",
      gateway.classify_caller(Req("127.0.0.1")) == "unregistered-local")
check("★ 它拿不到 E1 解除权",
      gateway.classify_caller(Req("127.0.0.1")) not in gateway.E1_OVERRIDE_ALLOWED_TIERS)
check("★ 它进不了记忆敏感路径", gateway.require_trusted_local(Req("127.0.0.1")) is None)
set_ident(("X\\某个将来新建的账户", "某个将来新建的账户"))
check("★★ 任意新账户默认落【降档】侧(allowlist 形状)",
      gateway.classify_caller(Req("127.0.0.1")) == "unregistered-local")

print("=== 大小写不敏感 ===")
set_ident(("X\\AI-Asset", "AI-Asset"))
check("AI-Asset(大写)也拒", gateway.classify_caller(Req("127.0.0.1")) == "denied-account")

print("=== 远程(非回环):走 WebAuthn ===")
set_ident(("X\\whoever", "whoever"))
check("非回环 → remote-unauthenticated", gateway.classify_caller(Req("100.64.0.5")) == "remote-unauthenticated")
check("远程 require_trusted_local → None", gateway.require_trusted_local(Req("100.64.0.5")) is None)

# ★ IPv6 回环旁路回归(2026-07-28 审查发现):曾把 ::1 当可信回环,而身份解析只查 IPv4 表
#   → 对 ::1 恒解析不到 → fail-open 成 trusted-local,等于对 IPv6 整体关掉 D30 且无日志。
print("=== ::1 不得被当作可信回环(身份不可解析 → 必须 fail-closed)===")
set_ident(None)                                   # 模拟:IPv6 调用方解析不到身份
check("::1 不是 trusted-local", gateway.classify_caller(Req("::1")) != "trusted-local")
check("::1 → remote-unauthenticated", gateway.classify_caller(Req("::1")) == "remote-unauthenticated")
check("::1 require_trusted_local → None", gateway.require_trusted_local(Req("::1")) is None)
set_ident(("X\\ai-asset", "ai-asset"))            # 即使能解析出隔离账户也不该走 ::1 放行路径
check("::1 + ai-asset 仍不放行", gateway.classify_caller(Req("::1")) != "trusted-local")

# ══════════════════════════════════════════════════════════════════════════
#  ★★★★★ V32b · 认人失败要说得出**断在哪一环**,并且**留得下痕**(用户裁定 2026-08-10)
#
#  ★★★ 这组判据能不能为假是本节的成败点。一条「降档时有日志」的断言,
#    若它测的是"那个函数存在",测的就是别的东西。
#  ⇒ 下面**真的制造一次认人失败**,断言那条日志**真的出现**、**真的写清了断在哪一环**。
#
#  ★ `caller_identity.py` 的**每一条**失败出口都要有判据,不是只测一条 ——
#    判词:**一个修法漏改一处,缺陷就完整地留在那一处。**
# ══════════════════════════════════════════════════════════════════════════
print("=== V32b:认人失败的四条出口 + 降档留痕 ===")

import json as _json                                          # noqa: E402
import pathlib as _pl                                         # noqa: E402
import tempfile as _tf                                        # noqa: E402

import caller_identity as _ci                                 # noqa: E402

# ── ① 逐条出口:每一个失败出口都要**说得出自己的名字** ────────────────────
#   ★★ 遍历源是 `_ci.FAILURE_STEPS`(登记表本身),**不是**下面这份手写清单 ——
#     手写清单当遍历源的话,新增一条出口而忘了测,这里**一声不吭**
#     (ASSERTION-PITFALLS 3b:判词说"每一条"时,遍历源必须是表本身)。
_seen_steps = {}


def _probe_step(label, fn):
    """跑一次探针,记下它报的是哪一环。"""
    r = fn()
    _seen_steps[r.step] = (label, r.detail)
    check(f"★ 出口「{label}」报出的断点是 {r.step}(不是 None、不是空字符串)",
          isinstance(r.step, str) and r.step in _ci.FAILURE_STEPS)
    check(f"★★ 出口「{label}」带了 detail —— 只有断点没有细节的话,"
          "运维知道「是端口表那一环」,却不知道是哪个端口、报的什么错",
          isinstance(r.detail, str) and len(r.detail) > 0)
    return r


# 出口①-a:非 Windows(这条链结构上不存在)
_win_saved = _ci._WIN
try:
    _ci._WIN = False
    _probe_step("非 Windows", lambda: _ci.resolve_account_detailed("127.0.0.1", 5000))
finally:
    _ci._WIN = _win_saved

# 出口①-b:拿不到对端地址
_probe_step("拿不到对端地址", lambda: _ci.resolve_account_detailed("127.0.0.1", None))

# 出口②:端口表里没有那条 ESTABLISHED 行(端口 1 上不可能有我们的连接)
_probe_step("端口表查不到 PID", lambda: _ci.resolve_account_detailed("127.0.0.1", 1))

# 出口②(异常形态):端口表那一步**抛**了 —— 必须归到同一环,并带上异常类型
_pid_saved = _ci.resolve_peer_pid
try:
    def _boom(*a, **k):
        raise OSError("模拟:GetExtendedTcpTable 炸了")
    _ci.resolve_peer_pid = _boom
    _r_exc = _probe_step("端口表抛异常", lambda: _ci.resolve_account_detailed("127.0.0.1", 5000))
    check("★★★ 端口表抛异常时,detail 里有**异常类型**(裸 except 吞掉的正是这个)",
          "OSError" in _r_exc.detail)
finally:
    _ci.resolve_peer_pid = _pid_saved

# 出口③:PID 有了,但读不到 owner
_own_saved = _ci.pid_to_account
try:
    _ci.resolve_peer_pid = lambda ip, port: 4321
    _ci.pid_to_account = lambda pid, **k: None
    _r_own = _probe_step("读不到 owner", lambda: _ci.resolve_account_detailed("127.0.0.1", 5000))
    check("★★ 读不到 owner 时 detail 里带着 PID(没有它就没法去查是哪个进程)",
          "4321" in _r_own.detail)
finally:
    _ci.resolve_peer_pid = _pid_saved
    _ci.pid_to_account = _own_saved

# 出口④:**那条裸 except 的去处** —— 诊断这一趟自己炸了,也必须说出来
_det_saved = _ci.resolve_account_detailed
try:
    def _boom2(*a, **k):
        raise RuntimeError("模拟:诊断那一趟自己炸了")
    _ci.resolve_account_detailed = _boom2
    _r4 = _ci.diagnose_from_request(Req("127.0.0.1"))
    _seen_steps[_r4.step] = ("诊断自身异常", _r4.detail)
    check("★★★★ 出口④:诊断自身抛异常 ⇒ 报 STEP_EXCEPTION 且带异常类型 —— "
          "这就是原来那条**裸 `except: return None`** 的去处。"
          "它此前把一次故障压成「这个人没登记」,而两者的下一步完全不同",
          _r4.step == _ci.STEP_EXCEPTION and "RuntimeError" in _r4.detail)
finally:
    _ci.resolve_account_detailed = _det_saved

# ★★★★ 反向全表:登记表里的**每一条**出口都被上面探到过。
#   新增一条出口而忘了给判据 ⇒ 这里当场红,而不是静默少测一条。
_missed = [s for s in _ci.FAILURE_STEPS if s not in _seen_steps]
check(f"★★★★ `FAILURE_STEPS` 登记的每一条失败出口都有判据(缺:{_missed})—— "
      "判词:一个修法漏改一处,缺陷就完整地留在那一处",
      not _missed)
check(f"★★ 探针**零命中判红**(实测探到 {len(_seen_steps)} 条)—— "
      "探到 0 条与「全部覆盖」在上面那条集合判据里长得一模一样(空集 ⊆ 任何集合)",
      len(_seen_steps) > 0)

# ── ② ★★★★ 真的制造一次认人失败,断言那条日志**真的出现** ────────────────
#   ★ 日志写进临时目录:测试不该往生产日志里塞条目(那会让审计里出现测试造的事件)。
_logs_saved = gateway._logs_dir
_tmp_logs = _pl.Path(_tf.mkdtemp(prefix="localai-idlog-"))
_cnt_saved = dict(gateway._identity_unresolved_counts)
try:
    gateway._logs_dir = lambda: _tmp_logs
    gateway._identity_unresolved_counts.clear()

    set_ident(None)                      # ← 认人失败(account_from_request 返回 None)
    _before = gateway.identity_health()["unresolved_total"]
    _tier = gateway.classify_caller(Req("127.0.0.1"))
    _after = gateway.identity_health()["unresolved_total"]

    check("★★★ 降档确实发生了(前置:没有它,下面每一条都是零断言)",
          _tier == gateway.IDENTITY_UNRESOLVED_TIER)
    check(f"★★★★ 降档被**计数**了({_before} → {_after})—— "
          "全仓在这之前**没有任何一处**记录这个事件(2026-08-10 grep 核过)",
          _after == _before + 1)

    _logf = _tmp_logs / "identity_unresolved.jsonl"
    check("★★★★ 降档**真的写进了日志文件** —— 判据不是「那个函数存在」,"
          "是这一次真的落了一行(文件:identity_unresolved.jsonl)",
          _logf.exists())
    if _logf.exists():
        _rec = _json.loads(_logf.read_text(encoding="utf-8").strip().split("\n")[-1])
        check(f"★★★★ 那一行**写清了断在哪一环**(step={_rec.get('step')!r})—— "
              "只记「今天降了 N 次」的话,下一步该查端口表还是查 WMI 完全说不出来,"
              "那是一条看起来有信息量、实际更费人的日志",
              _rec.get("step") in _ci.FAILURE_STEPS)
        check("★★★ 那一行带 detail(具体断在哪个端口 / 报的什么错)",
              isinstance(_rec.get("detail"), str) and len(_rec["detail"]) > 0)
        check("★★ 那一行带时刻与档位名(没有时刻就没法把它和用户说的『刚才不好用』对上)",
              bool(_rec.get("ts")) and _rec.get("tier") == gateway.IDENTITY_UNRESOLVED_TIER)
        check("★★★ 日志里**不含账户名字段** —— 降档时我们恰恰没有账户名,"
              "编一个占位账户名进审计就是往里写一个假事实",
              "account" not in _rec)

    # ── ③ 反向:认得出人的调用方**不许**留这条痕 ─────────────────────────
    #   ★ 少了这一条,一个"每次请求都记一笔"的实现也会让上面全部变绿 ——
    #     而那样的日志等于没有日志(全是噪音,真事件淹在里面)。
    _n_before = gateway.identity_health()["unresolved_total"]
    set_ident(("HONGKONGPINGPON\\Zori Ma", "Zori Ma"))
    check("★ 前置:这个账户确实被认成 trusted-local",
          gateway.classify_caller(Req("127.0.0.1")) == "trusted-local")
    set_ident(("HONGKONGPINGPON\\CodexSandboxOnline", "CodexSandboxOnline"))
    check("★★★ 前置:**查得出来但不在册**仍然是 unregistered-local(这一档没被改掉)",
          gateway.classify_caller(Req("127.0.0.1")) == "unregistered-local")
    check("★★★★ 反向:认得出人(含未登记账户)**一次痕都不留** —— "
          "「查出来了但不在册」是**稳定事实**,不是故障;"
          "把它也记成降档事件,真正的故障就淹在噪音里了",
          gateway.identity_health()["unresolved_total"] == _n_before)

    # ── ④ 计数**按环分**,不是一个总数 ──────────────────────────────────
    gateway._identity_unresolved_counts.clear()
    gateway.note_identity_unresolved(_ci.STEP_PID_LOOKUP, "x")
    gateway.note_identity_unresolved(_ci.STEP_PID_LOOKUP, "x")
    gateway.note_identity_unresolved(_ci.STEP_OWNER_LOOKUP, "y")
    _h = gateway.identity_health()
    check("★★★ 计数**按环分**(端口表 2 次 / owner 1 次)—— "
          "只有总数的话,「端口表偶发抖」与「WMI 一直超时」长得一模一样,"
          "而它们要修的是完全不同的东西",
          _h["by_step"] == {_ci.STEP_PID_LOOKUP: 2, _ci.STEP_OWNER_LOOKUP: 1}, )
    check("★ 总数是各环之和", _h["unresolved_total"] == 3)
    check("★★ `last` 是**最近一次**那一条(界面要说『刚刚断在哪』)",
          _h["last"].get("step") == _ci.STEP_OWNER_LOOKUP)
finally:
    gateway._logs_dir = _logs_saved
    gateway._identity_unresolved_counts.clear()
    gateway._identity_unresolved_counts.update(_cnt_saved)
    try:
        for _q in _tmp_logs.iterdir():
            _q.unlink()
        _tmp_logs.rmdir()
    except Exception:                                         # noqa: BLE001
        pass

check("★★ 桩已还回去(_logs_dir 恢复原样,后面的断言不会写进临时目录)",
      gateway._logs_dir is _logs_saved)

# ── ⑤ ★★★ 界面那一行的**数据来源**:响应头 ────────────────────────────────
#   用户裁定:「界面上要看得见,但**别做成弹窗**」⇒ 一行状态、可点开看详情。
#   ⇒ 网关这一侧把事实**跟着这一轮的响应**带回去(与 `X-LocalAI-E1` 同一条既有做法),
#     不另开一条要轮询的路。界面那一行由客户端渲染(20-client-win,不在本车道边界内)。
gateway.note_identity_unresolved(_ci.STEP_PID_LOOKUP, "为了下面这条断言")
_h_unres = gateway.identity_headers(gateway.IDENTITY_UNRESOLVED_TIER)
check("★★★ 降档时响应头带 `X-LocalAI-Identity: unresolved`(界面据此显示那一行)",
      _h_unres.get(gateway.IDENTITY_HEADER) == "unresolved")
check("★★★ 头里带**断在哪一环** —— 只说「认人失败了」而不说哪一环,"
      "界面能显示的就只有一句没有下一步的话",
      _h_unres.get(gateway.IDENTITY_STEP_HEADER) in _ci.FAILURE_STEPS)
check("★★ 头里带累计次数(区分『偶尔抖一下』与『一直认不出人』)",
      _h_unres.get(gateway.IDENTITY_TOTAL_HEADER, "").isdigit())
check("★★★★ 头的值**全是 latin-1 编得出的**(HTTP 头走 latin-1)—— "
      "把中文 detail 塞进头会在编码那一步炸掉整个响应,"
      "那就把一个『提示』变成了一次『故障』,比不提示坏得多",
      all(str(v).encode("latin-1") for v in _h_unres.values()))
check("★★★★ 反向:**没降档就不发这些头** —— 恒发的头等于没有信息,"
      "界面会一直显示那一行,而用户很快就不再看它",
      gateway.identity_headers("trusted-local") == {}
      and gateway.identity_headers("lan-device") == {})

# ── ⑥ ★★★★ 新档位必须在**两张能力表**里都登记(否则 fail-closed 会咬到机主)──
#   ★ `caps_for` 表外一律 DENY_ALL。漏登记一边 ⇒ 一次认人抖动会让机主**什么都做不了**,
#     而那比今天的「静默降档」更坏 —— 所以这条断言必须存在。
import gpu_policy as _gp                                      # noqa: E402
import sync_policy as _sp                                     # noqa: E402

check("★★★★ `identity-unresolved` 在 GPU 面能力表里(漏了 ⇒ DENY_ALL ⇒ 抖一下机主就读不到状态)",
      gateway.IDENTITY_UNRESOLVED_TIER in _gp.TIER_CAPS)
check("★★★★ `identity-unresolved` 在同步面能力表里(同上)",
      gateway.IDENTITY_UNRESOLVED_TIER in _sp.TIER_CAPS)
check("★★★ 两张表的档位集合仍然一致(tiers_match_gpu)",
      _sp.tiers_match_gpu()[0], )
#   ★★★ 本车道**没有改任何权限** —— 拆分与留痕是一件事,改权限是另一件。
#     「解析失败算出境 sink 还是本地 sink」是 D81 待裁 1,
#     `sink-axis-change-list-2026-08-06.md` 明写不得先于它动手。
check("★★★★ `identity-unresolved` 的 GPU 能力与 `unregistered-local` **逐字相同** —— "
      "本车道只拆分与留痕,**不改权限**(那一刀是 D81 待裁 1 的)",
      _gp.TIER_CAPS[gateway.IDENTITY_UNRESOLVED_TIER].actions
      == _gp.TIER_CAPS["unregistered-local"].actions)
check("★★★★ 同步面同样逐字相同",
      _sp.TIER_CAPS[gateway.IDENTITY_UNRESOLVED_TIER].actions
      == _sp.TIER_CAPS["unregistered-local"].actions)
check("★★★ 而它**不在** E1 解除档位里(认不出人绝不等于认得出机主)",
      gateway.IDENTITY_UNRESOLVED_TIER not in gateway.E1_OVERRIDE_ALLOWED_TIERS)

print(f"\n=== {_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
