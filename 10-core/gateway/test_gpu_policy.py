"""P4-S10 · 权限档位六元组(GPU 面)。跑:python test_gpu_policy.py

★★★ 这一套守的是一个**实测出来的洞**:2026-08-05 之前,GPU 面每个端点只有一行

    if classify_caller(request) == "remote-unauthenticated": return 401

于是除「远程未认证」外**所有档位权限完全相同** —— 包括 §6.8 明文写着
「**绝不放行**」的 `denied-account`(ai-asset / ai-exec)。实跑确认过:
它能读快照、能把驻留集合**清空**、能申请 ttl=10^9 秒的**独占**租约。

根因不是谁写漏了一行,是**两条路径各写各的档位判断**:
`chat_completions` 里那套(denied-account 403 + 证书指纹封顶 lan-device)
**只长在那一条路径上**,GPU 面从来没接过。
⇒ 修法是把能力做成**一张表**,并断言 GPU 面不得再有散落的比较。
"""
import inspect
import re
import sys

# ★★ 编码双保险(与 P4-S0 同源):干净的 cp936 控制台编不出 ⇒ / ✓ / ★ 之类的字符,
#   而 print 一抛异常会把整套脚本掀翻 —— 于是【一条断言变红】表现成【整套崩溃】,
#   运行器只看到"没有汇总行",看不出是哪条没守住。
#   S0 当年修的是 vram_gate 的生产路径,测试脚本这边一直没修 —— 2026-08-05 补上。
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass
import warnings

warnings.filterwarnings("ignore")

import assert_helpers
import gateway
import gpu_broker
import gpu_policy
from starlette.testclient import TestClient

_pass = _fail = 0


def check(name, cond, extra=""):
    global _pass, _fail
    if cond:
        _pass += 1
    else:
        _fail += 1
        print(f"  FAIL  {name} {extra}")


print("=== 1. ★★ 反向全表:六个维度逐维必须【答过】===")
check("DIMENSIONS 恰好是方案书 §6.2 的六个",
      gpu_policy.DIMENSIONS == ("user", "device", "agent", "tool", "param", "quota"),
      gpu_policy.DIMENSIONS)
check("★ 每一维都在 DIMENSION_IMPL 里写明落在哪儿(少答一维就红)",
      set(gpu_policy.DIMENSION_IMPL) == set(gpu_policy.DIMENSIONS),
      f"缺 {set(gpu_policy.DIMENSIONS) - set(gpu_policy.DIMENSION_IMPL)}")
check("★ 没有一维是空话",
      all(len(v) > 10 for v in gpu_policy.DIMENSION_IMPL.values()))
check("★ Agent 维的答案是【结构上不存在 agent 主体】(S1 已断言 gpu.* 永不进工具池),"
      "不是「还没做」",
      "工具池" in gpu_policy.DIMENSION_IMPL["agent"])
check("★★ 参数维明确点名了「空集合」被单列(§6.2:参数决定它是安全还是灾难)",
      "空集合" in gpu_policy.DIMENSION_IMPL["param"])

print("\n=== 2. ★★ 反向全表:档位表必须覆盖【所有能到达 handler 的档位】 ===")
# ★ 直接从 classify_caller 的源码里把返回值抠出来 —— 抄一份清单迟早跟真值分家。
_cc = assert_helpers.code_only(gateway.classify_caller)
_returned = set(re.findall(r'return\s+"([a-z-]+)"', _cc))
check(f"classify_caller 的返回值都在表里(实测 {sorted(_returned)})",
      _returned <= set(gpu_policy.TIER_CAPS),
      f"漏了 {sorted(_returned - set(gpu_policy.TIER_CAPS))}")
check("★ lan-device 也在表里 —— 它不是 classify_caller 的返回值,是证书指纹解出来的封顶档;"
      "漏了它,副机的每一次请求都会落进 DENY_ALL",
      "lan-device" in gpu_policy.TIER_CAPS)
check("★ 提取器没有静默失灵(抠到的返回值不为空)", len(_returned) >= 4, sorted(_returned))
for tier, caps in gpu_policy.TIER_CAPS.items():
    check(f"{tier} 的 actions 都是已登记动作",
          set(caps.actions) <= set(gpu_policy.ACTIONS), sorted(caps.actions))
    check(f"{tier} 写了为什么(留给下一个改它的人)", len(caps.why) > 20)
    check(f"{tier} 的 lease_kinds 都是真的 kind",
          set(caps.lease_kinds) <= set(gpu_broker.LEASE_KINDS), sorted(caps.lease_kinds))
check("★★ 表外档位 → DENY_ALL(加一个新档位,默认落在【什么都不能做】那一边)",
      gpu_policy.caps_for("brand-new-tier").actions == frozenset()
      and gpu_policy.caps_for("brand-new-tier").changes_per_min == 0)
check("★ DENY_ALL 不是恰好等于某个真档位(否则新档位会静默继承它的权限)",
      all(c.actions != frozenset() or t in ("lan-edge", "denied-account", "remote-unauthenticated")
          for t, c in gpu_policy.TIER_CAPS.items()))

print("\n=== 3. ★★★ §6.8「绝不放行」终于在 GPU 面生效 ===")


def _probe(tier, *, headers=None, patch_lan=False):
    """把一个档位打到真的 HTTP 面上,回四个动作的结果。"""
    _cc_orig, _lan_orig = gateway.classify_caller, gateway.resolve_lan_principal
    try:
        if patch_lan:
            gateway.classify_caller = lambda r: "lan-edge"
            gateway.resolve_lan_principal = lambda fp: {"tier": "lan-device", "device_id": "d1"}
        else:
            gateway.classify_caller = lambda r, t=tier: t
        gpu_policy.reset_quota()
        with TestClient(gateway.app, client=("127.0.0.1", 5555)) as c:
            h = headers or {}
            snap = c.get("/v1/gpu/snapshot", headers=h)
            g = snap.json().get("generation", 0) if snap.status_code == 200 else 0
            chg = c.post("/v1/gpu/intended",
                         json={"if_generation": g, "components": ["speech.lite"]}, headers=h)
            wipe = c.post("/v1/gpu/intended",
                          json={"if_generation": g, "components": []}, headers=h)
            lz = c.post("/v1/gpu/lease",
                        json={"if_generation": g, "kind": "recalibration", "holder": "x",
                              "components": [], "ttl_s": 10 ** 9}, headers=h)
            return snap, chg, wipe, lz
    finally:
        gateway.classify_caller, gateway.resolve_lan_principal = _cc_orig, _lan_orig


def _dim(r):
    try:
        return r.json().get("error", {}).get("dimension", "")
    except Exception:                                        # noqa: BLE001
        return ""


_s, _c, _w, _l = _probe("denied-account")
check("★★★ denied-account 读快照被拒(此前是 200)", _s.status_code == 403, _s.status_code)
check("★★★ denied-account 不能变更(此前放行)", _c.status_code == 403, _c.status_code)
check("★★★ denied-account 不能清空驻留集合(此前放行)", _w.status_code == 403)
check("★★★ denied-account 不能申请独占租约(此前放行)", _l.status_code == 403)
check("★ 拒绝时点名是【用户】维拦的 —— §6.8 是账户层面的规则",
      _dim(_c) == "user", _dim(_c))

print("\n=== 4. 逐档实跑 ===")
_s, _c, _w, _l = _probe("trusted-local")
check("trusted-local 读得到", _s.status_code == 200)
# ★★★ 2026-08-05(S14):装载器接上了,这两条随之改守新事实。
#   它们原本守「过了权限层 → 落到业务层的 loader_absent」;而 S14 之后
#   speech.lite 走到的是另一条 fail-closed(它的 kind 启动方式尚未验证),
#   lan-device 那条则可能撞上世代号(前一次请求已经把号推高了)。
#   ★ 承重的性质没变:**权限层放行了**(不是 401/403),失败发生在【业务层】。
#     ⇒ 判据改成盯这一点,而不是盯某个具体的业务错误码 ——
#       盯具体错误码等于把"业务层今天怎么失败"焊进权限测试里。
check("trusted-local 变更**过了权限层**(失败发生在业务层,不是 401/403)",
      _c.status_code not in (401, 403), f"{_c.status_code}/{_dim(_c)}")
check("★ trusted-local 是唯一能『卸掉全部』的档位(权限层放行)", _w.status_code in (409, 422))
check("★ 但 ttl=10^9 仍被参数维拦住 —— 权限高不等于参数不封顶",
      _l.status_code == 403 and _dim(_l) == "param", f"{_l.status_code}/{_dim(_l)}")

_s, _c, _w, _l = _probe(None, headers={"x-localai-cert-sha256": "aa"}, patch_lan=True)
check("lan-device 读得到", _s.status_code == 200)
# ══════════════════════════════════════════════════════════════════════
#  ★★★ 2026-08-07(D?):副机**不得写 `intended_resident_set`**。
#
#  方案书四集合表(行 1550)「谁能写」= **只有主机变更面**;08-06 审计 B6 那道闸
#  只补了 `permitted_on_demand`(行 1553,同一句规格)那一半,写 `intended` 本身
#  一直放行 —— 实测 `lan-device` 带非空 components POST 一次,**过**。
#
#  ★★ 判据必须测【行为】,不是「源码里有没有那个词」(ASSERTION-PITFALLS 第 9 条,
#    gpu_policy.py:75-83 那段教训)。所以这里走的是 `_probe` 的**真 HTTP**:
#    用 lan-device 身份真发一次 POST /v1/gpu/intended,读它真的回了什么。
#  ★ 这条**可以为假**:同一段代码里 trusted-local 那次(上面)必须**不是** 403;
#    两条一起看才排除掉「反正全都 403」那种恒绿。
# ══════════════════════════════════════════════════════════════════════
check("★★★ lan-device **不能**改驻留集合 —— 方案书四集合表:intended 只有主机变更面能写",
      _c.status_code == 403 and _dim(_c) == "tool", f"{_c.status_code}/{_dim(_c)}")
check("★ 而且拒的是【工具】维、不是额度/参数 —— 说明它是「这一档没有这个动作」,"
      "不是「今天太快了」(后者会让人以为等一分钟就能做)",
      _dim(_c) == "tool", _dim(_c))
check("★★ lan-device 不能『卸掉全部』—— 拦在【工具】维",
      _w.status_code == 403 and _dim(_w) == "tool", f"{_w.status_code}/{_dim(_w)}")
check("★ 副机不是「什么都不能动」:lease 档还在 ⇒ D87①「意图即起」照常 —— "
      "变成只读的只有【替机主改写常驻清单】这一件",
      "lease" in gpu_policy.TIER_CAPS["lan-device"].actions)
check("★ lan-device 不能拿独占租约(它会让整台中枢拒发一切新租约,而副机看不到主机屏幕)",
      _l.status_code == 403 and _dim(_l) == "param")

_s, _c, _w, _l = _probe("unregistered-local")
check("★ unregistered-local 读得到(D30 降档【不断连】)", _s.status_code == 200)
check("★ 但改不动 —— 改 GPU 状态比聊天高一个量级,不该跟着「不断连」一起放行",
      _c.status_code == 403 and _dim(_c) == "tool")

_s, _c, _w, _l = _probe("lan-edge")
check("★ lan-edge 是代理进程档、非业务档:GPU 面全拒",
      _s.status_code == 403 and _c.status_code == 403)

_s, _c, _w, _l = _probe("remote-unauthenticated")
check("remote-unauthenticated 全 401(不是 403 —— 你是谁还没确定)",
      _s.status_code == 401 and _c.status_code == 401)

print("\n=== 5. ★★★ 参数维:空集合是【另一个动作】,不是同一个动作的一个取值 ===")
check("空列表 → unload_all", gpu_policy.resolve_action([], is_change=True) == "unload_all")
check("非空 → change_resident", gpu_policy.resolve_action(["x"], is_change=True) == "change_resident")
check("不是变更 → read", gpu_policy.resolve_action(["x"], is_change=False) == "read")
_ra = assert_helpers.code_only(gpu_policy.resolve_action)
check("★ 映射只看 components 是否为空,不看别的(判据不能被别的字段绕开)",
      "components" in _ra and "unload_all" in _ra)
_d = gpu_policy.check("trusted-local", "lease", lease_kind="client_session", ttl_s=10 ** 9)
check("★ ttl 超上限被拒且点名 param 维", (not _d.ok) and _d.dimension == "param", _d.to_json())
check("★ 拒绝消息说清「不封顶等于永不过期」(而租约的全部意义就是会过期)",
      "永不过期" in _d.message, _d.message)
_d2 = gpu_policy.check("lan-device", "lease", lease_kind="recalibration", ttl_s=10)
check("★ 独占租约不给 lan-device,且消息解释了为什么(会冻住整台中枢)",
      (not _d2.ok) and "独占" in _d2.message, _d2.to_json())
_d3 = gpu_policy.check("trusted-local", "change_resident", components=["c"] * 99)
check("★ 组件数有上限(一次点名 99 个不是正常请求)",
      (not _d3.ok) and _d3.dimension == "param")

print("\n=== 6. ★ 额度维 ===")
gpu_policy.reset_quota()
# ★ 2026-08-07:变更桶这几条从 lan-device 改用 trusted-local ——
#   副机已经**没有任何** change 桶动作了(见上一节),拿它跑变更额度会在【工具】维
#   就被拦掉,测出来的将不再是额度维。承重的性质没变,只是换了一个还有这个动作的档位。
_cap = gpu_policy.TIER_CAPS["trusted-local"].changes_per_min
_res = [gpu_policy.check("trusted-local", "change_resident", components=["x"], holder="h")
        for _ in range(_cap + 3)]
check(f"前 {_cap} 次放行", all(r.ok for r in _res[:_cap]))
check("★ 超出后被拒,且点名 quota 维", (not _res[_cap].ok) and _res[_cap].dimension == "quota")
check("★★ 拒绝文案说清「这不是权限不够,是太快了」—— 否则人会跑去申请提权",
      "不是权限不够" in _res[_cap].message, _res[_cap].message)
gpu_policy.reset_quota()
for _ in range(50):
    gpu_policy.check("trusted-local", "read", holder="h")
check("★ read 不占变更额度(读状态不该被限流,那会让界面反而看不见发生了什么)",
      gpu_policy.check("trusted-local", "change_resident", components=["x"], holder="h").ok)
gpu_policy.reset_quota()
check("★ 额度按 holder 分桶(一台机器刷爆自己的,不该把别人也拖下水)",
      all(gpu_policy.check("trusted-local", "change_resident", components=["x"], holder=f"h{i}").ok
          for i in range(_cap + 2)))
# ★ 副机那一侧的分桶仍然要测,只是落在它**还有**的那个桶(租约)上:
#   多台副机各自心跳,一台刷爆不该把另一台连坐。
gpu_policy.reset_quota()
_lcap = gpu_policy.TIER_CAPS["lan-device"].leases_per_min
_burn = [gpu_policy.check("lan-device", "lease", lease_kind="client_session",
                          ttl_s=10, holder="noisy") for _ in range(_lcap + 2)]
check("★ 副机把自己的租约桶刷爆了(点名 quota 维)",
      (not _burn[_lcap].ok) and _burn[_lcap].dimension == "quota", _burn[_lcap].to_json())
check("★★ 另一台副机不受连坐 —— 租约桶同样按 holder 分",
      gpu_policy.check("lan-device", "lease", lease_kind="client_session",
                       ttl_s=10, holder="quiet").ok)
gpu_policy.reset_quota()
_probe_src = assert_helpers.code_only(gateway)
check("★★ reset_quota 在生产代码里【没有任何调用点】——"
      "一个能被业务调用的『清空额度』就等于额度维没有实现",
      "reset_quota" not in _probe_src, "gateway.py 里出现了 reset_quota")

print("\n=== 7. ★★ 结构:能力来自【表】,不是散落的 if ===")
_gw = assert_helpers.code_only(gateway)
_gpu_fns = ("gpu_snapshot", "gpu_events", "gpu_components", "gpu_lease",
            "gpu_lease_renew",          # S16b 新增 —— 由下面那条元断言抓出来要求登记的
            "gpu_intent",               # S16b 接动作:「意图即起」—— 同样是元断言抓出来的
            "gpu_intended", "session_end")
# ★★★ 2026-08-05 审计补的**元断言**:上面这个元组是手写的,而下面整圈检查
#   (「不自己比档位」「走 gpu_guard」)只作用于列进来的名字。
#   ⇒ 新增一个 GPU 路由却忘了加进来时,它会被**静默跳过** —— 测试照样全绿,
#     而那个新端点可能正自己比着档位。漏掉的恰恰是最需要检查的那一个。
#   ★ 判据取【包含】而非相等:session_end 不在 /v1/gpu 前缀下,是有意多出来的一个。
_gpu_route_fns = {getattr(r, "endpoint").__name__ for r in gateway.app.routes
                  if getattr(r, "path", "").startswith("/v1/gpu")
                  and hasattr(r, "endpoint")}
check("★★ 元断言:_gpu_fns 覆盖【全部】GPU 路由处理函数(漏一个 = 下面那圈静默跳过它)",
      _gpu_route_fns <= set(_gpu_fns),
      f"漏了 {sorted(_gpu_route_fns - set(_gpu_fns))} —— 加进 _gpu_fns,别改这条断言")
check("★ 且元断言本身不是空转(确实数到了路由)", len(_gpu_route_fns) >= 5, f"只数到 {_gpu_route_fns}")
for _n in _gpu_fns:
    _src = assert_helpers.code_only(getattr(gateway, _n))
    check(f"{_n} 不再自己比档位(能力来自表)",
          "classify_caller" not in _src, _src[:120])
    check(f"{_n} 走 gpu_guard", "gpu_guard" in _src)
_gp = assert_helpers.code_only(gateway.gpu_principal)
check("★★ 指纹解析不出成员 → remote-unauthenticated(fail-closed),【不退回】caller 档 ——"
      "退回会让伪造指纹的本机进程拿到比 lan-device 更多的权限",
      "remote-unauthenticated" in _gp and "resolve_lan_principal" in _gp)
check("★ denied-account 在指纹解析【之前】就被封死(顺序与 chat 那条路径一致)",
      _gp.index("denied-account") < _gp.index("x-localai-cert-sha256"))
_gg = assert_helpers.code_only(gateway.gpu_guard)
check("★ 拒绝响应里带 dimension(哪一维拦的决定用户下一步做什么)", '"dimension"' in _gg)
check("★ 额度拒绝用 429,不是 403 —— 两者的下一步完全不同", "429" in _gg)
check("★ 身份未定用 401、已知但不给用 403", "401" in _gg and "403" in _gg)

print("\n=== 8. ★ session_end 不占变更额度 ===")
_se = assert_helpers.code_only(gateway.session_end)
check('★ session_end 归 read 档 —— 它只释放自己的租约,不改驻留集合;'
      '算成变更的话,一次正常退出就会吃掉用户的配额,而退出是每次都做的事',
      'gpu_guard(request, "read")' in _se, _se[:200])

print("\n=== 9. ★★★★ V13(D?):主机客户端【真实连接路径】拿到的是哪个档位 ===")
# ══════════════════════════════════════════════════════════════════════════
#  ★★★ 这一节存在的全部理由 —— 2026-08-07 实机那一课
#
#  那天 4197 条断言全绿,而主机上点「确定」直接吃「这台设备不能做这个操作」。
#  原因就在上面第 3/4 节的 `_probe`:它**把 `classify_caller` 整个换掉**,
#  tier 是【直接构造】的。于是"档位表判得对不对"被测得很透,
#  而**"主机客户端实际会走到哪一档"从头到尾没有一条断言问过**。
#
#  ⇒ 本节反过来:**不碰 `classify_caller`,也不碰 `gpu_principal`**。
#    唯一替身是 `caller_identity.account_from_request` —— 那是"操作系统说这个 socket
#    属于谁",合成请求没有真 socket,拿不到它;除此之外
#    (回环判据 → allowlist 查表 → gpu_principal → 档位表 → HTTP 层)**全部真跑**。
#
#  ★ 两个方向都测,而且两边**只差一个证书指纹头**:
#      · 无指纹(= 主机客户端直连回环网关)⇒ trusted-local ⇒ 改驻留集合**过权限层**;
#      · 带指纹(= 经 lan-edge,今天主机与副机走的都是这条)⇒ 封顶 lan-device ⇒ 403/tool。
#    只测一个方向等于没测:一个"永远放行"或"永远拒绝"的实现都能让单向断言恒绿。
# ══════════════════════════════════════════════════════════════════════════
_ident_orig = gateway.caller_identity.account_from_request
_lan_orig2 = gateway.resolve_lan_principal
#: 机主账户从**配置文件真读**(config/caller-accounts.toml 的 allowlist),不写死在测试里 ——
#  写死的话,把机主从 allowlist 里删掉这件事在这里不会变红,而它恰恰是这条路的开关。
_owner = sorted(gateway.TRUSTED_LOCAL_ACCOUNTS)[0]


def _probe_real(account, *, with_fp=False):
    """按【真实连接路径】打一次 GPU 面。★ 只替换 OS 身份解析,其余全真跑。"""
    try:
        gateway.caller_identity.account_from_request = \
            lambda req, a=account: None if a is None else (f"HOST\\{a}", a)
        if with_fp:
            gateway.resolve_lan_principal = lambda fp: {"tier": "lan-device", "device_id": "d1"}
        gpu_policy.reset_quota()
        h = {"x-localai-cert-sha256": "aa"} if with_fp else {}
        with TestClient(gateway.app, client=("127.0.0.1", 5555)) as c:
            snap = c.get("/v1/gpu/snapshot", headers=h)
            g = snap.json().get("generation", 0) if snap.status_code == 200 else 0
            chg = c.post("/v1/gpu/intended",
                         json={"if_generation": g, "components": ["speech.lite"]}, headers=h)
            return snap, chg
    finally:
        gateway.caller_identity.account_from_request = _ident_orig
        gateway.resolve_lan_principal = _lan_orig2


class _ReqLike:
    """给 classify_caller / principal_device 用的最小请求(与本文件其它 stub 同款)。"""

    def __init__(self, host="127.0.0.1", headers=None):
        self.client = type("C", (), {"host": host, "port": 5555})()
        self.headers = headers or {}


# ── ① 主机这条路(回环 + 机主账户 + **无指纹**)──
# ★★★ 这里**不写**「allowlist 非空」那种前提 —— 它恒真:`load_caller_accounts`
#   对空表直接 raise,`import gateway` 那一刻就炸了,断言根本跑不到
#   (2026-08-08 对抗式复核指出的恒绿,已删)。要证明「判据真的是那张表」,
#   得**把机主从表里拿掉再打一次**,见下面 ⑤。
gateway.caller_identity.account_from_request = lambda req: (f"HOST\\{_owner}", _owner)
try:
    _tier_host = gateway.gpu_principal(_ReqLike())
    _dev_host = gateway.principal_device(_ReqLike())
finally:
    gateway.caller_identity.account_from_request = _ident_orig
check("★★★★ **回环 + allowlist 账户 + 无证书指纹 ⇒ trusted-local**(整条链真跑,没有构造 tier)—— "
      "这正是主机客户端 V13 之后走的那条路",
      _tier_host == "trusted-local", _tier_host)
check("★★★ 检查①:trusted-local **不需要证书指纹也解得出设备身份**(= LOCAL_DEVICE)—— "
      "绕开 lan-edge 就没有那个注入者,若还有哪条路径要指纹才认人,退出时就放不掉租约",
      _dev_host == gateway.LOCAL_DEVICE, _dev_host)

_s9, _c9 = _probe_real(_owner)
check("★★★★ 主机这条路上「点确定」**过了权限层**(不是 401/403)—— "
      "2026-08-07 实机那句「这台设备不能做这个操作」就是这里回的 403",
      _c9.status_code not in (401, 403), f"{_c9.status_code}/{_dim(_c9)}")
check("★ 而且读得到快照", _s9.status_code == 200, _s9.status_code)

# ── ② 同一账户、只多一个证书指纹(= 经 lan-edge)⇒ 封顶 lan-device ──
#   ★★ 这一条同时是【检查②:副机不受影响】的行为判据:副机的每一次请求都带指纹。
_s9b, _c9b = _probe_real(_owner, with_fp=True)
check("★★★★ **只多一个证书指纹就封顶 lan-device ⇒ 改驻留集合 403 / 工具维** —— "
      "副机走的正是这条(它永远带指纹),所以副机**仍然改不动驻留集合**;"
      "同时这条也证明上面那条不是恒绿",
      _c9b.status_code == 403 and _dim(_c9b) == "tool", f"{_c9b.status_code}/{_dim(_c9b)}")
check("★ 副机仍然读得到快照(改不动 ≠ 断连)", _s9b.status_code == 200, _s9b.status_code)

# ── ③ 反向:回环但**不在 allowlist 里**的账户 ⇒ 拿不到主机档 ──
#   ★ 没有这一条,「回环就是主机」会是一条谁都能走的路 —— 本机上真实存在
#     两个外部 AI 沙箱账户(见 config/caller-accounts.toml 的账目那一节)。
_s9c, _c9c = _probe_real("CodexSandboxOffline")
check("★★★ 回环但账户不在 allowlist ⇒ **改不动**(unregistered-local)—— "
      "「走回环」不等于「是机主」,判据始终是那张 allowlist",
      _c9c.status_code == 403 and _dim(_c9c) == "tool", f"{_c9c.status_code}/{_dim(_c9c)}")
_s9d, _c9d = _probe_real("ai-asset")
check("★★★ 隔离服务账户即使走回环也 403(§6.8『绝不放行』在这条路上照样成立)",
      _c9d.status_code == 403 and _dim(_c9d) == "user", f"{_c9d.status_code}/{_dim(_c9d)}")
_s9e, _c9e = _probe_real(None)
check("★★ 解析不出 OS 身份 ⇒ 降档改不动(fail-closed,不是『解析不到就当机主』)",
      _c9e.status_code == 403, f"{_c9e.status_code}/{_dim(_c9e)}")

# ── ④ 结构:`lan-device` 那一档**没有**被偷偷加回 change_resident ──
#   ★ 这次修的是客户端走哪条路,**不是**放宽副机的权限。两者长得很像,
#     而后者会把副机的口子一起开回去(用户在 V13 里明令禁止的那一条)。
check("★★★ `lan-device` 依旧没有 change_resident / unload_all / permit_on_demand —— "
      "V13 修的是【主机走哪条路】,不是【放宽副机】",
      gpu_policy.TIER_CAPS["lan-device"].actions == frozenset({"read", "lease"}),
      sorted(gpu_policy.TIER_CAPS["lan-device"].actions))
check("★ 而 trusted-local 仍然是唯一拿满全套动作的档位(主机变更面就是它)",
      gpu_policy.TIER_CAPS["trusted-local"].actions == frozenset(gpu_policy.ACTIONS))

# ── ⑤ ★★★ 判据真的是那张 allowlist —— 把机主从表里拿掉,同一条连接立刻改不动 ──
#   ★ 没有这一条,上面 ① 的 `_owner` 取自被测的那张表本身,「这条路的开关是 allowlist」
#     这句话在这里**不可能为假**(2026-08-08 对抗式复核指出)。
_saved_allow = gateway.TRUSTED_LOCAL_ACCOUNTS
try:
    gateway.TRUSTED_LOCAL_ACCOUNTS = frozenset({"nobody-by-this-name"})
    _s9f, _c9f = _probe_real(_owner)
finally:
    gateway.TRUSTED_LOCAL_ACCOUNTS = _saved_allow
check("★★★★ 把机主从 allowlist 里拿掉 ⇒ **同一个账户、同一条回环连接**立刻改不动 —— "
      "这条路的开关确实是 config/caller-accounts.toml,不是「走回环就是机主」",
      _c9f.status_code == 403, f"{_c9f.status_code}/{_dim(_c9f)}")
#  ★ V32b:`_ReqLike` 没有真 socket ⇒ 认人**必然失败** ⇒ 它现在落 `identity-unresolved`
#    (在这之前"没查出来"和"查出来了但不在册"共用 `unregistered-local`,已按用户裁定拆开)。
#    ★★ 这条断言问的是「全局状态没被改坏」,所以三个档位都算正常 —— 但**不能**写成
#      「随便什么值都行」:那样它就恒真了。
check("★ 而且拿掉之后再放回去,上面那条仍然成立(没有把全局状态改坏)",
      gateway.classify_caller(_ReqLike())
      in ("trusted-local", "unregistered-local", gateway.IDENTITY_UNRESOLVED_TIER))

# ── ⑥ ★★★★ 主机上【非机主 Windows 账户】比它原来经 Edge 拿到的**还少** ──
#   这是 V13 客户端那一侧必须有「档位不对就退回 Edge」的**理由**:
#   DecideBusinessTarget 只看 isHostMachine 这个【整机】事实,而回环那头的档位按
#   【登录账户】判 —— 两者粒度不一样。config/caller-accounts.toml 里明文记着
#   访客账户 Alle 被有意排除,还给「第二位家庭成员」预留了位置。
_unreg = gpu_policy.TIER_CAPS["unregistered-local"]
_lan = gpu_policy.TIER_CAPS["lan-device"]
check("★★★★ `unregistered-local`(主机上非机主账户走回环拿到的)**比 `lan-device` 还少一个 lease** —— "
      "所以客户端不能只凭『这台是主机』就把业务口改到回环:那会让访客账户上的"
      "「意图即起」与 client_session 租约一起没掉(比 V13 之前更差)",
      "lease" in _lan.actions and "lease" not in _unreg.actions,
      f"lan-device={sorted(_lan.actions)} unregistered-local={sorted(_unreg.actions)}")
_s9g, _c9g = _probe_real("Alle")
check("★★★ 行为面同一条:回环 + 非 allowlist 账户 ⇒ 连**申请租约**都不行 "
      "(客户端据此降级回 Edge —— 服务端在 error.tier 里如实回带了档位名)",
      _c9g.status_code == 403 and "tier" in (_c9g.json().get("error") or {}),
      f"{_c9g.status_code}/{(_c9g.json().get('error') or {}).get('tier')}")

print(f"\n=== 权限档位六元组:{_pass} PASS · {_fail} FAIL ===")
sys.exit(1 if _fail else 0)
