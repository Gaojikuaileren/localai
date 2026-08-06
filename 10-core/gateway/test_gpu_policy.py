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
check("lan-device 能改驻留集合(否则副机上的面板会变只读,是产品回退)",
      _c.status_code not in (401, 403), f"{_c.status_code}/{_dim(_c)}")
check("★★ lan-device 不能『卸掉全部』—— 拦在【工具】维",
      _w.status_code == 403 and _dim(_w) == "tool", f"{_w.status_code}/{_dim(_w)}")
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
_cap = gpu_policy.TIER_CAPS["lan-device"].changes_per_min
_res = [gpu_policy.check("lan-device", "change_resident", components=["x"], holder="h")
        for _ in range(_cap + 3)]
check(f"前 {_cap} 次放行", all(r.ok for r in _res[:_cap]))
check("★ 超出后被拒,且点名 quota 维", (not _res[_cap].ok) and _res[_cap].dimension == "quota")
check("★★ 拒绝文案说清「这不是权限不够,是太快了」—— 否则人会跑去申请提权",
      "不是权限不够" in _res[_cap].message, _res[_cap].message)
gpu_policy.reset_quota()
for _ in range(50):
    gpu_policy.check("lan-device", "read", holder="h")
check("★ read 不占变更额度(读状态不该被限流,那会让界面反而看不见发生了什么)",
      gpu_policy.check("lan-device", "change_resident", components=["x"], holder="h").ok)
gpu_policy.reset_quota()
check("★ 额度按 holder 分桶(一台副机刷爆自己的,不该把主机也拖下水)",
      all(gpu_policy.check("lan-device", "change_resident", components=["x"], holder=f"h{i}").ok
          for i in range(_cap + 2)))
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

print(f"\n=== 权限档位六元组:{_pass} PASS · {_fail} FAIL ===")
sys.exit(1 if _fail else 0)
