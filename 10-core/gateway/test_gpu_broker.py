"""P4-S3 · GPU Broker 骨架测试。纯 assert:python test_gpu_broker.py

本片是**只读**的,所以这里钉的不是"算得对不对"(算术在 test_vram_gate.py 的 83 条里),
而是几条**将来最容易被违反**的结构性质:

  · 世代号与状态必须在**同一把锁下**一起改 —— 分开改会出现「号涨了状态没改」,
    而客户端正是靠号判断要不要重取;
  · 采样器死掉必须**看得见** —— 一个被吞掉异常的采样器 = 快照永远停在旧值,
    而调用方以为它是新的。这是本项目最恨的静默失效;
  · 快照是**副本**,拿到它的人改不动权威状态(D37「单一权威 + 副本」);
  · 非 AI 占用必须标注**推算**:本机实测 nvidia-smi --query-compute-apps 对全部进程的
    used_memory 都是 [N/A](WDDM 不暴露逐进程显存),说不出占用者名字是结构性的;
  · ★ 反向全表:本片**不得**引入任何变更端点(那属于 S4)。
"""

import asyncio
import dataclasses
import inspect
import socket
import os              # ★ D92 元断言要去客户端源码里找配对标记
import re
import sys
import time            # ★ A1②:排空窗口那条断言要**真的计一次时**,不能只看常量

# ★★ 编码双保险(与 P4-S0 同源):干净的 cp936 控制台编不出 ⇒ / ✓ / ★ 之类的字符,
#   而 print 一抛异常会把整套脚本掀翻 —— 于是【一条断言变红】表现成【整套崩溃】,
#   运行器只看到"没有汇总行",看不出是哪条没守住。
#   S0 当年修的是 vram_gate 的生产路径,测试脚本这边一直没修 —— 2026-08-05 补上。
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

import assert_helpers
import gateway
import gpu_broker
import gpu_policy      # ★ B4/B6:额度桶与动作维的判据在这里,行为断言要真的调它

_pass = _fail = 0


def check(name, cond, extra=""):
    global _pass, _fail
    if cond:
        _pass += 1
    else:
        _fail += 1
        print(f"  FAIL  {name} {extra}")


print("=== 1. 路由已归类,且本片【只增只读端点】 ===")
check("没有未归类路由(新增路由必须进 ROUTE_TIERS)",
      gateway.unclassified_routes() == [], f"{gateway.unclassified_routes()}")
check("/v1/gpu/snapshot 已登记且是 authenticated",
      gateway.ROUTE_TIERS.get(("GET", "/v1/gpu/snapshot")) == "authenticated")
# ★★ 反向全表:GPU 面的路由必须**逐条登记**在这张表里。
#   S3 时这条写的是"只能有 GET";S5 加了第一个变更端点(POST /v1/gpu/lease),
#   于是它被**有意地**改成显式方法表 —— 那是一次语义变更,应当在 diff 里看得见,
#   而不是把断言删掉了事。新增任何 GPU 路由若不改这里,必红。
_EXPECTED_GPU_ROUTES = {
    ("GET", "/v1/gpu/snapshot"),     # S3:只读快照
    ("GET", "/v1/gpu/events"),       # S5:推送流(SSE)
    ("POST", "/v1/gpu/lease"),       # S5:★ 第一个变更端点
    ("GET", "/v1/gpu/components"),   # S9:组件目录 —— 挑选面板的数据源(只读)
    ("POST", "/v1/gpu/intended"),    # S9:★ 第二个变更端点 = 「点确定」那一次事务
    # S16b:续租。★ 它**不是**变更端点 —— 不改驻留集合,只延长一份已有租约的寿命。
    #   归 lease 档;若哪天有人把它算进 change_resident,下面那条"变更端点逐条列名"会红。
    ("POST", "/v1/gpu/lease/renew"),
    # S16b:★ 「意图即起」(D87①)。它**动显存**,但**动不到 committed** ——
    #   走 Broker 的 transient 平面(D90 裁定③:结构上够不着 committed 的独立路径)。
    #   ⇒ 归 lease 档。若哪天有人让它写 committed,下面那条反向断言(扫源码里的赋值)会红。
    ("POST", "/v1/gpu/intent"),
}
_gpu_routes = {(m, p) for (m, p) in gateway.ROUTE_TIERS if p.startswith("/v1/gpu")}
check(f"GPU 路由逐条登记(实测 {sorted(_gpu_routes)})",
      _gpu_routes == _EXPECTED_GPU_ROUTES,
      f"多出 {sorted(_gpu_routes - _EXPECTED_GPU_ROUTES)} 少了 {sorted(_EXPECTED_GPU_ROUTES - _gpu_routes)}")
# ★ S5 时这条写的是"变更端点只有一个";S9 加了 POST /v1/gpu/intended,于是它被**有意地**
#   改成逐条列名 —— 数量断言只拦得住"多了几个",拦不住"换了一个"。改成集合相等更严,不更松。
check("★ 变更端点逐条列名(每多一个都该是一次有意的决定)",
      {p for m, p in _gpu_routes if m != "GET"}
      == {"/v1/gpu/lease", "/v1/gpu/intended", "/v1/gpu/lease/renew", "/v1/gpu/intent"},
      f'{sorted(p for m, p in _gpu_routes if m != "GET")}')
# ══════════════════════════════════════════════════════════════════════
#  ★★★ 2026-08-06 审计 B4:这条断言**换了判据**,不是换了措辞。
#
#  原文是:
#      _ren = assert_helpers.code_only(gateway.gpu_lease_renew)
#      check("★★★ 续租归 lease 档(不吃变更配额)",
#            '"lease"' in _ren and "change_resident" not in _ren)
#  它问的是「源码里有没有那个词」,而它想守的是「续租**做不做得到**不吃变更配额」。
#  这正是 ASSERTION-PITFALLS 第 9 条的形状 —— 而且它**当时是绿的、事实是假的**:
#  `lease` 与 `change_resident` 共用同一个令牌桶、同一个上限,
#  实测 lan-device 连打 20 次续租之后,用户点一次确定就吃 denied_quota(第 21 次判红)。
#
#  ⇒ 新判据:**把续租桶打满,再点一次确定,它必须还能过。**
#    ★ 归档那条(源码里归 lease 不归 change_resident)**留着**,但降级为辅助 ——
#      它仍然拦得住"手滑改了档位",只是不再单独承担这句话的真伪。
# ══════════════════════════════════════════════════════════════════════
_ren = assert_helpers.code_only(gateway.gpu_lease_renew)
check("(辅助)续租的档位标记仍是 lease,不是 change_resident",
      '"lease"' in _ren and "change_resident" not in _ren)
gpu_policy.reset_quota()
# ★ 2026-08-07(D?):这一对改用 trusted-local 跑。副机已经**没有** change_resident 了
#   (方案书四集合表:intended 只有主机变更面能写),拿 lan-device 跑「点确定」会在
#   【工具】维就被拦掉 —— 测出来的将不再是额度维。承重的性质一个字没变:
#   **同一个档位**里,把租约桶打满,变更桶必须还能过。
_lease_cap = gpu_policy.TIER_CAPS["trusted-local"].leases_per_min
for _i in range(_lease_cap):
    gpu_policy.check("trusted-local", "lease", lease_kind="client_session", ttl_s=90, holder="PC-A")
_after_renews = gpu_policy.check("trusted-local", "change_resident", components=["x"], holder="PC-A")
check(f"★★★ 续租【真的】不吃变更配额:打满 {_lease_cap} 次续租之后,"
      "用户点确定仍然过得去(这才是那句话的判据)",
      _after_renews.ok, _after_renews.to_json())
gpu_policy.reset_quota()
# ★ 反向:两个桶各自仍然封顶 —— 拆桶不等于把额度维关掉。
#   ★ 这一条仍用 lan-device:租约桶是副机**今天还有**的那个桶,它才是心跳的真实来源。
_lan_lease_cap = gpu_policy.TIER_CAPS["lan-device"].leases_per_min
for _i in range(_lan_lease_cap):
    gpu_policy.check("lan-device", "lease", lease_kind="client_session", ttl_s=90, holder="PC-A")
_over = gpu_policy.check("lan-device", "lease", lease_kind="client_session", ttl_s=90, holder="PC-A")
check("★★ 反向:租约桶自己仍然会满(拆桶 ≠ 给续租开一条免额度通道)",
      (not _over.ok) and _over.dimension == "quota"
      and _over.detail.get("bucket") == gpu_policy.QUOTA_BUCKET_LEASE, _over.to_json())
check("★ 拒绝时回带**桶名** —— 不带的话,撞了租约桶的人会照着变更桶的上限去等",
      "bucket" in _over.detail, _over.detail)
gpu_policy.reset_quota()
# ★★ 反向全表:ACTIONS 里每一个都必须在 QUOTA_BUCKETS 里登记 ——
#   漏一个就会落进 `bucket_of` 的兜底(变更桶),那是**安全**的方向,
#   但"安全地错着"仍然是错着:它会把一个本该免额度的动作静默算进用户的配额。
check("★★ 反向全表:每个动作都登记了额度桶归属",
      set(gpu_policy.ACTIONS) == set(gpu_policy.QUOTA_BUCKETS),
      f"漏 {sorted(set(gpu_policy.ACTIONS) - set(gpu_policy.QUOTA_BUCKETS))} "
      f"多 {sorted(set(gpu_policy.QUOTA_BUCKETS) - set(gpu_policy.ACTIONS))}")
check("★ 表外动作落【变更桶】(最严的那个),不是【不计额度】—— 加新动作默认落在拒的那边",
      gpu_policy.bucket_of("brand-new-action") == gpu_policy.QUOTA_BUCKET_CHANGE)
check("★★ 续租的条件是 fence_token,**不叠 if_generation** —— "
      "世代号是全局的,别人申请一份不相干的租约不该把你的续租打回",
      "fence_token" in _ren and "if_generation" not in _ren)
# ★ 查**文案**要用原始源码,不能用 code_only —— 它会把字符串字面量整个剥掉,
#   拿它查文案的方向是【恒真】(见 ASSERTION-PITFALLS 第 3c 条)。
#   第一版我拿 code_only 查,查不到,于是拼了个 docstring 凑数 —— 那是在迁就判据。
_ren_raw = inspect.getsource(gateway.gpu_lease_renew)
check("★★★ 两种失败分开回,且各自给出**相反**的下一步:"
      "NOT_HOLDER 立刻自隐(重试就是双持有)· EXPIRED 重新申请一份",
      "LEASE_NOT_HOLDER" in _ren and "不要重试" in _ren_raw and "重新申请" in _ren_raw)
check("★ 且 HTTP 状态也分开(409 条件写不匹配 / 410 那份已经不在了)",
      "409" in _ren and "410" in _ren)

print("=== 2. 快照形状:该有的字段一个不少 ===")
snap = gpu_broker.BROKER.snapshot()
j = snap.to_json()
for k in ("generation", "committed", "vram", "sampled_at", "age_s", "stale", "sampler_error"):
    check(f"快照含 {k}", k in j)
for k in ("free_gib", "total_gib", "vram_budget", "desktop_floor",
          "non_ai_used_gib_inferred", "non_ai_is_inferred", "non_ai_note"):
    check(f"vram 段含 {k}", k in j["vram"])
check("总量与预算来自 vram_gate 的同一份配置(不另抄一遍数字)",
      abs(j["vram"]["total_gib"] - 15.92) < 1e-9 and abs(j["vram"]["vram_budget"] - 8.52) < 1e-9,
      f'{j["vram"]}')

print("=== 3. ★ 非 AI 占用必须标注为【推算】,不得伪装成实测 ===")
check("non_ai_is_inferred 恒为 True", j["vram"]["non_ai_is_inferred"] is True)
check("字段名自带 _inferred 后缀", "non_ai_used_gib_inferred" in j["vram"])
check("note 说清了为什么点不出占用者名字",
      "WDDM" in j["vram"]["non_ai_note"] and "说不出" in j["vram"]["non_ai_note"])
check("Snapshot 数据类有 inferred 字段且默认 True",
      any(f.name == "inferred" and f.default is True
          for f in dataclasses.fields(gpu_broker.Snapshot)))

print("=== 4. ★ 快照是副本:拿到它改不动权威状态 ===")
check("Snapshot 是 frozen dataclass",
      dataclasses.fields(gpu_broker.Snapshot) and gpu_broker.Snapshot.__dataclass_params__.frozen)
_before = gpu_broker.BROKER.snapshot().committed
snap.committed.append("llm.bogus")          # 改副本
check("改副本不影响权威状态", gpu_broker.BROKER.snapshot().committed == _before,
      f"{gpu_broker.BROKER.snapshot().committed}")
try:
    object.__setattr__  # noqa: B018
    snap2 = gpu_broker.BROKER.snapshot()
    _frozen_ok = False
    try:
        snap2.generation = 999            # type: ignore[misc]
    except Exception:
        _frozen_ok = True
    check("快照字段不可直接赋值(frozen 生效)", _frozen_ok)
except Exception:
    check("快照字段不可直接赋值(frozen 生效)", False)

print("=== 5. ★★ 世代号与状态在同一把锁下一起改 ===")


async def _gen_test():
    b = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
    g0 = b.snapshot().generation
    g1 = await b._set_committed(["llm.assistant.8b@16k"])
    s1 = b.snapshot()
    g2 = await b._set_committed([])
    s2 = b.snapshot()
    return g0, g1, s1, g2, s2


g0, g1, s1, g2, s2 = asyncio.run(_gen_test())
check("初始世代号 0", g0 == 0, str(g0))
check("每次变更世代号 +1 且单调", g1 == 1 and g2 == 2, f"{g1},{g2}")
check("世代号涨的同时状态也变了(不会号涨状态没改)",
      s1.committed == ["llm.assistant.8b@16k"] and s2.committed == [], f"{s1.committed} / {s2.committed}")
# ★ 源码级:_set_committed 里状态与世代号必须在同一个 async with 块内
_src = inspect.getsource(gpu_broker.Broker._set_committed)
_body = _src.split("async with self._lock:", 1)
check("_set_committed 用锁", len(_body) == 2)
check("★ 世代号 +1 在锁内(不在锁外单独涨)",
      len(_body) == 2 and "self._generation += 1" in _body[1] and
      "self._committed" in _body[1],
      "世代号与状态分开改 = 客户端会看到一个号对应两种状态")

print("=== 6. ★★ 采样器死掉必须看得见(不能静默停在旧值)===")


async def _sampler_fail_test():
    b = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
    # 模拟 nvidia-smi 挂了(返回 None)
    _saved = gpu_broker.vram_gate.nvml_free_gib
    try:
        gpu_broker.vram_gate.nvml_free_gib = lambda: None
        b._sample_once()
        s_none = b.snapshot()
        # 模拟采样器整个抛异常
        def _boom():
            raise RuntimeError("nvml 炸了")
        gpu_broker.vram_gate.nvml_free_gib = _boom
        b._sample_once()
        s_boom = b.snapshot()
        return s_none, s_boom
    finally:
        gpu_broker.vram_gate.nvml_free_gib = _saved


s_none, s_boom = asyncio.run(_sampler_fail_test())
check("NVML 读不到 → free 置 None(不保留上一次的值)", s_none.free_gib is None)
check("NVML 读不到 → sampler_error 说清原因", s_none.sampler_error and "None" in s_none.sampler_error,
      str(s_none.sampler_error))
check("采样器抛异常 → 也进 sampler_error(不被吞掉)",
      s_boom.sampler_error and "RuntimeError" in s_boom.sampler_error, str(s_boom.sampler_error))
check("采样失败时 non_ai 推算值不给(不能拿 None 去算)", s_boom.non_ai_used_gib_inferred is None)

print("=== 7. ★ 从未采样 / 采样过旧 → stale 必须为真 ===")
b_fresh = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
check("从未采样过 → stale=True", b_fresh.snapshot().stale is True)
check("从未采样过 → sampled_at 为 None", b_fresh.snapshot().sampled_at is None)
check("STALE_AFTER_S 有上限且大于采样间隔",
      gpu_broker.STALE_AFTER_S > gpu_broker.SAMPLE_INTERVAL_S)

print("=== 8. ★ 硬约束:锁内不得跨 await 网络 I/O ===")
#   本进程同时持有 300 秒的流式聊天连接;在锁内 await 网络 = 把聊天卡死。
#   这条今天成立,但极易在 S4 加租约时被破坏 —— 所以用源码结构钉住。
_lock_src = inspect.getsource(gpu_broker.Broker)


# ★ 实现见 assert_helpers.lock_bodies —— 判据按【缩进】取块,不是捕到下一个 def。
#   见 00-docs/ASSERTION-PITFALLS.md 第 4 条。
_lock_bodies = assert_helpers.lock_bodies


_bodies = _lock_bodies(_lock_src)
# ★★ 元断言:提取器一旦匹配不上,下面那个 for 就一次都不跑,三条检查【静默消失】——
#   那正是本项目最恨的假断言。所以先钉住"提取到的块数 == 源码里 async with 的出现次数"。
check("★★ 锁块提取器没有静默失灵(块数与源码中 async with self._lock 的次数相等)",
      len(_bodies) == _lock_src.count("async with self._lock:") and len(_bodies) > 0,
      f"提取 {len(_bodies)} / 源码 {_lock_src.count('async with self._lock:')}")
for seg in _bodies:
    check("锁内没有 run_in_executor(那是 I/O)", "run_in_executor" not in seg, seg[:120])
    check("锁内没有 nvml 采样调用", "nvml_free_gib" not in seg and "_sample_once" not in seg, seg[:120])
    check("锁内没有 asyncio.sleep", "asyncio.sleep" not in seg, seg[:120])
check("采样确实发生在锁【外】(_sampler_loop 用 executor)",
      "run_in_executor" in inspect.getsource(gpu_broker.Broker._sampler_loop))

print("=== 9. 判定内核只有一份(§8.1 规则 18)===")
check("Broker 复用 vram_gate,不自带第二套算术",
      "import vram_gate" in inspect.getsource(gpu_broker) or gpu_broker.vram_gate is not None)
_bsrc = inspect.getsource(gpu_broker)
check("Broker 里没有自己重算 vram_budget 的算式",
      "total_vram -" not in _bsrc.replace("cfg.budget.total_vram - free", ""),
      "预算是导出值,只能来自 vram_gate.Config")

##########################################################################
#  P4-S4b · 租约账本
##########################################################################

print("=== 10. ★ kind 是两根正交的轴,不是一套枚举 ===")
_k = gpu_broker.LEASE_KINDS
check("kind 表非空且是闭集", len(_k) >= 5)
for name, kd in _k.items():
    check(f"{name} 同时声明了两根轴", isinstance(kd.evictable, bool) and isinstance(kd.blocking, str))
check("生命周期轴与阻塞性轴确实不是同一根(存在同 evictable 不同 blocking 的组合)",
      len({(kd.evictable, kd.blocking) for kd in _k.values()}) > len({kd.evictable for kd in _k.values()}),
      "若两轴一一对应,就该合成一个枚举 —— 那时这条断言会红,提示重新裁定")
check("五种用途齐备",
      set(_k) >= {"client_session", "model_ref", "pet_presence", "agent_task", "recalibration"})
check("★ 只有 model_ref 可驱逐(§8.1.7 显式开的口子),其余一律不可",
      {n for n, kd in _k.items() if kd.evictable} == {"model_ref"},
      f'{ {n for n, kd in _k.items() if kd.evictable} }')
check("★ 重标定是独占的(B10⑥:期间测出来的数才作数)", _k["recalibration"].exclusive)
check("独占只有重标定一种", {n for n, kd in _k.items() if kd.exclusive} == {"recalibration"})

print("=== 11. ★★ 未登记 kind fail-closed ===")


async def _kind_test():
    b = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
    st_bad, l_bad = await b.grant("bogus_kind", "X", [])
    st_ok, l_ok = await b.grant("client_session", "PC-A", ["llm.assistant.8b@16k"])
    return st_bad, l_bad, st_ok, l_ok, b


_stb, _lb, _sto, _lo, _b = asyncio.run(_kind_test())
check("未登记 kind 被拒", _stb == gpu_broker.LEASE_UNKNOWN_KIND, _stb)
check("未登记 kind 不发租约", _lb is None)
check("已登记 kind 正常发放", _sto == gpu_broker.LEASE_OK and _lo is not None)

print("=== 12. ★★ fence_token:全新 · 不复用 · 不由 holder 推导 ===")


async def _token_test():
    b = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
    toks, ids = set(), set()
    for _ in range(20):
        st, l = await b.grant("model_ref", "same-holder", ["speech.lite"])
        toks.add(l.fence_token)
        ids.add(l.lease_id)
    return toks, ids


_toks, _ids = asyncio.run(_token_test())
check("20 次发放得到 20 个不同 token(不复用)", len(_toks) == 20, str(len(_toks)))
check("lease_id 也各不相同", len(_ids) == 20)
check("★ 同一个 holder 反复申请,token 也不相同(不可由 holder 推导)", len(_toks) == 20)
check("token 长度足够(不是可枚举的小整数)", all(len(t) >= 16 for t in _toks))

print("=== 13. ★★ 续租是条件写:对不上返回 NOT_HOLDER,与 EXPIRED 可区分 ===")


async def _renew_test():
    b = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
    _, l = await b.grant("client_session", "PC-A", [], ttl_s=60)
    ok = await b.renew(l.lease_id, l.fence_token)
    bad = await b.renew(l.lease_id, "0" * 32)
    gone = await b.renew("nonexistent", "0" * 32)
    # 过期路径:发一份 TTL 极短的
    _, l2 = await b.grant("model_ref", "PC-B", [], ttl_s=0.001)
    await asyncio.sleep(0.02)
    exp = await b.renew(l2.lease_id, l2.fence_token)
    return ok, bad, gone, exp


_ok, _bad, _gone, _exp = asyncio.run(_renew_test())
check("正确 token 续租成功", _ok == gpu_broker.LEASE_OK, _ok)
check("★ 错误 token → NOT_HOLDER(不是 False,不是 EXPIRED)",
      _bad == gpu_broker.LEASE_NOT_HOLDER, _bad)
check("不存在的 lease → EXPIRED", _gone == gpu_broker.LEASE_EXPIRED, _gone)
check("★ 真过期 → EXPIRED(与 NOT_HOLDER 分开)", _exp == gpu_broker.LEASE_EXPIRED, _exp)
check("★ 两种失败【可区分】—— 混成一个 False,调用方只能猜,而猜错的方向是重试 = 双持有",
      gpu_broker.LEASE_NOT_HOLDER != gpu_broker.LEASE_EXPIRED)

print("=== 14. ★★ 惰性过期,且【不设收割线程】 ===")
_src_all = inspect.getsource(gpu_broker)
check("★ 没有收割线程/定时清理 task(收割线程 = 第二个写者 = 双持有从侧门回来)",
      "reaper" not in _src_all and "_sweep_loop" not in _src_all and
      _src_all.count("create_task") == 1,   # 只有采样器那一个
      "create_task 出现次数 = " + str(_src_all.count("create_task")))
check("清扫函数名明确要求在锁内调用", "_sweep_expired_locked" in _src_all)
_sweep_src = inspect.getsource(gpu_broker.Broker._sweep_expired_locked)
check("清扫在函数内现取时间(不接受外面传进来的时间)",
      "time.monotonic()" in _sweep_src and "def _sweep_expired_locked(self)" in _sweep_src)


async def _expiry_test():
    b = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
    _, l = await b.grant("model_ref", "PC-A", ["speech.lite"], ttl_s=0.001)
    before = b.reserved_components()
    await asyncio.sleep(0.02)
    after_reserved = b.reserved_components()
    after_active = await b.active_leases()
    return before, after_reserved, after_active


_bef, _aft_r, _aft_a = asyncio.run(_expiry_test())
check("未过期时组件计入 reserved", _bef == ["speech.lite"], str(_bef))
check("★ 过期后立刻不再计入 reserved(只读路径也认过期)", _aft_r == [], str(_aft_r))
check("过期条目在下次进锁时被真正删掉", _aft_a == [], str(_aft_a))

print("=== 15. ★ 时间纪律:不得在拿到锁之前捕获时间(clock_timestamp 的进程内等价物)===")
for _m in ("grant", "renew", "release"):
    _s = inspect.getsource(getattr(gpu_broker.Broker, _m))
    _head, _, _tail = _s.partition("async with self._lock:")
    check(f"{_m}:锁【之前】没有取时间", "time.monotonic()" not in _head, _head[-120:])
    if "time.monotonic()" in _s:
        check(f"{_m}:时间在锁【之内】取", "time.monotonic()" in _tail)

print("=== 16. ★ 独占租约:在场时拒发一切新租约(B10⑥)===")


async def _excl_test():
    b = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
    st1, l1 = await b.grant("client_session", "PC-A", [])
    st2, _ = await b.grant("recalibration", "host", [])      # 已有租约 → 应拒
    await b.release(l1.lease_id, l1.fence_token)
    st3, l3 = await b.grant("recalibration", "host", [])      # 无租约 → 应成
    st4, _ = await b.grant("client_session", "PC-B", [])      # 独占在场 → 应拒
    return st1, st2, st3, st4


_s1, _s2, _s3, _s4 = asyncio.run(_excl_test())
check("普通租约正常发", _s1 == gpu_broker.LEASE_OK)
check("★ 已有租约时发独占被拒", _s2 == gpu_broker.LEASE_EXCLUSIVE_HELD, _s2)
check("清空后独占可发", _s3 == gpu_broker.LEASE_OK, _s3)
check("★ 独占在场时一切新租约被拒", _s4 == gpu_broker.LEASE_EXCLUSIVE_HELD, _s4)

print("=== 17. 快照带上租约与 reserved(拒绝信息要含【占用者】)===")


async def _snap_test():
    b = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
    await b.grant("client_session", "PC-A", ["llm.assistant.8b@16k"])
    return b.snapshot().to_json()


_sj = asyncio.run(_snap_test())
check("快照含 leases", "leases" in _sj and len(_sj["leases"]) == 1)
check("快照含 reserved", _sj.get("reserved") == ["llm.assistant.8b@16k"], str(_sj.get("reserved")))
for _f in ("holder", "granted_at", "kind", "evictable", "blocking"):
    check(f"租约条目含 {_f}(P4-4「拒绝信息含占用者」的原料)", _f in _sj["leases"][0])

print("=== 18. ★★ /v1/session/end 终于存在了 ===")
#   客户端 HubClient.cs:230 每次退出都调它,而网关里此前【没有这条路由】,失败还被吞掉 ——
#   一次伪装成成功的静默失败。
check("路由已登记", ("POST", "/v1/session/end") in gateway.ROUTE_TIERS)
check("是 authenticated", gateway.ROUTE_TIERS[("POST", "/v1/session/end")] == "authenticated")
check("仍无未归类路由", gateway.unclassified_routes() == [], f"{gateway.unclassified_routes()}")
_ss_src = inspect.getsource(gateway.session_end)
check("★ 回话里说清释放了几条(不是一律 200 空体让调用方无从分辨)",
      "released_leases" in _ss_src)
check("★ 释放走的是条件写(带 fence_token),不是按 holder 无条件删",
      "fence_token" in _ss_src)

##########################################################################
#  P4-S5 · 推送非轮询 + 世代号冲突
##########################################################################

print("=== 19. ★★ 变更通知:订阅者【等事件】,不是轮询 ===")


async def _notify_test():
    b = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
    g = b.snapshot().generation
    # ① 正常唤醒
    t = asyncio.create_task(b.wait_for_change(g, timeout=5))
    await asyncio.sleep(0.03)
    await b.grant("client_session", "PC-A", ["speech.lite"])
    woke = await t
    # ② 无变更 → 超时(该发心跳了)
    g2 = b.snapshot().generation
    timed_out = not await b.wait_for_change(g2, timeout=0.15)
    leaked = len(b._waiters)
    # ③ ★ 丢失唤醒:变更发生在"读快照之后、开始等待之前"
    g3 = b.snapshot().generation
    await b.grant("model_ref", "PC-B", [])
    immediate = await b.wait_for_change(g3, timeout=0.15)
    return woke, timed_out, leaked, immediate


_woke, _timedout, _leaked, _immediate = asyncio.run(_notify_test())
check("状态变更会唤醒等待者", _woke)
check("无变更时按时超时(调用方据此发心跳)", _timedout)
check("★ 超时后把自己从等待队列摘掉(否则 _waiters 无界增长)", _leaked == 0, str(_leaked))
check("★★ 不丢唤醒:变更发生在读快照与等待之间时立即返回",
      _immediate, "先在锁内比一次世代号 —— 不比就会漏掉一整个世代")

print("=== 20. ★ 通知必须与世代号 +1 在同一把锁里 ===")
#   分开的话会出现「通知发出去了但世代号还没涨」,订阅者取到的快照与它以为的不符。
for _m in ("_set_committed", "grant", "release"):
    _s = inspect.getsource(getattr(gpu_broker.Broker, _m))
    if "self._generation += 1" in _s:
        _, _, _tail = _s.partition("async with self._lock:")
        check(f"{_m}:世代号 +1 与 _notify_locked 都在锁内",
              "self._generation += 1" in _tail and "_notify_locked()" in _tail, _m)
_nsrc = inspect.getsource(gpu_broker.Broker._notify_locked)
# ★ 必须先摘掉文档字符串再查:它的注释里正好在解释"为什么不违反锁内不得 await"——
#   直接 in 判断会因为**注释里的那个词**而误红。第一版就栽在这儿(与本项目
#   Body() 去注释、e4_egress 去 docstring 是同一条纪律:断言只看会执行的代码)。
_ncode = re.sub(r'"""(?:.|\n)*?"""', "", _nsrc)
_ncode = "\n".join(l for l in _ncode.splitlines() if not l.lstrip().startswith("#"))
check("_notify_locked 只做 set(非阻塞),代码里不含 await", "await" not in _ncode, _ncode[:160])
check("★ 通知后清空等待队列(一次性 Event,不复用)", "self._waiters = []" in _nsrc)

print("=== 21. 推送流:先全量、后增量、每帧盖世代号、有心跳 ===")
_ev = inspect.getsource(gateway.gpu_events)
check("连上先发全量快照", "event: snapshot" in _ev)
check("之后发变更帧", "event: update" in _ev)
check("★ 有心跳(静默长连接与死掉的长连接必须长得不一样)", "heartbeat" in _ev)
check("★ 心跳也带世代号(客户端可发现自己错过了一帧)", "gen=" in _ev)
check("★ 推送流崩了要说出来,不静默断开", "event: error" in _ev)
check("媒体类型是 text/event-stream", "text/event-stream" in _ev)
check("★ 如实标注不做字段级 diff(不假装做了增量)", "不假装做了增量" in _ev or "仍发全量" in _ev)
check("订阅走 wait_for_change,不是 sleep 轮询",
      "wait_for_change" in _ev and "asyncio.sleep" not in _ev)

print("=== 22. ★★ if_generation 必填 —— 省略不等于『我不在乎』 ===")
_lz = inspect.getsource(gateway.gpu_lease)
check("缺 if_generation → 400(不是默认放行)",
      'if "if_generation" not in body' in _lz and "missing_if_generation" in _lz)
check("★ 对不上 → 409", "generation_conflict" in _lz and "status_code=409" in _lz)
check("★★ 409 回带最新快照(裸 409 会逼客户端再问一次 = 又变回轮询)",
      _lz.count('"snapshot"') >= 2, "被拒时也要回带 —— 拒绝信息含占用者靠的就是它")
check("租约未发放时也回 409 + 快照", "租约未发放" in _lz)

print("=== 23. 世代号冲突的真实行为(端到端,不只是源码级)===")


async def _conflict_test():
    b = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
    g0 = b.snapshot().generation
    await b.grant("client_session", "PC-A", [])      # 世代号涨了
    g1 = b.snapshot().generation
    return g0, g1


_g0, _g1 = asyncio.run(_conflict_test())
check("每次发租约世代号都变(客户端手上的旧号会失效)", _g1 != _g0, f"{_g0} -> {_g1}")

##########################################################################
#  P4-S7 · 四个集合 · I2/I3/I4 · RECONCILE_WATCH
##########################################################################

print("=== 24. 四个集合齐备,且 permitted_on_demand 与 intended 【不合并】 ===")
_sj = gpu_broker.BROKER.snapshot().to_json()
for _k in ("intended_resident", "committed_resident", "actual_resident", "permitted_on_demand"):
    check(f"快照含 {_k}", _k in _sj["sets"])
check("★ permitted_on_demand 是独立字段,不是塞进 intended",
      hasattr(gpu_broker.BROKER, "_permitted_on_demand") and
      gpu_broker.BROKER._permitted_on_demand is not gpu_broker.BROKER._intended,
      "合并会让 intended 里出现永远不参与 I2 判定的成员,三元组语义就脏了")
check("状态机七态齐备", len(gpu_broker.ALL_STATES) == 7)
check("初始态是 STARTING(不是直接 READY)",
      gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg).snapshot().state == gpu_broker.STATE_STARTING)

print("=== 25. ★★ I2 必须保留【蕴含】形态 ===")
#   state == READY ⟹ actual == committed。DEGRADED_SAFE 不是 READY ⇒ 前件为假 ⇒ 自动成立。
#   写成双条件会造出「永久违反、告警无法消解」的状态,逼系统去自动改写锚点 ——
#   而那恰好违反「不做自动触发」。


def _inv(b, name):
    return next(r for r in b.check_invariants() if r.invariant == name)


_b7 = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
_b7._state = gpu_broker.STATE_READY
_b7._committed = ["llm.assistant.8b@16k"]
check("READY 且相等 → I2 成立", _inv(_b7, "I2").holds)
_b7._committed = ["llm.assistant.8b@16k", "speech.lite"]   # actual 跟着 committed(见 actual_resident 说明)
check("READY 且相等(两项)→ I2 仍成立", _inv(_b7, "I2").holds)
_b7._state = gpu_broker.STATE_DEGRADED_SAFE
_r2 = _inv(_b7, "I2")
check("★★ DEGRADED_SAFE 下 I2 自动成立(前件为假)", _r2.holds)
check("★ 而且明说这是设计不是放水", "前件为假" in _r2.detail and "不是放水" in _r2.detail)
_i2src = inspect.getsource(gpu_broker.Broker.check_invariants)
check("★ I2 判据带状态前件(不是无条件相等)", 'self._state == STATE_READY' in _i2src)
check("★ 源码写明了为什么不能写成双条件", "双条件" in _i2src)
check("不等时列出容忍状态集", "I2_TOLERATED_STATES" in _i2src)
check("I2_TOLERATED_STATES 恰为三态(§8.1 行 1566)",
      set(gpu_broker.I2_TOLERATED_STATES) ==
      {gpu_broker.STATE_STARTING, gpu_broker.STATE_RECONCILING, gpu_broker.STATE_DEGRADED_SAFE})

print("=== 26. ★★ I3 【无状态前件】——任何状态下恒成立 ===")
#   这条才是接住「某个 bug 装了不该装的」的那一条,是准入白名单的【运行期】版本
#   ——白名单只在申请那一刻把关。
_b8 = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
_b8._committed = ["llm.assistant.8b@16k"]
for _st in gpu_broker.ALL_STATES:
    _b8._state = _st
    check(f"I3 在 {_st} 下也被求值且成立", _inv(_b8, "I3").holds)
check("★ I3 判据里【没有】状态前件",
      "I3" in _i2src and "stray = sorted(actual - (committed | permitted))" in _i2src)
#   构造违反:actual 里有既不在 committed 也不在 permitted 的
_b9 = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
_b9._committed = ["llm.assistant.8b@16k"]


class _FakeActual(gpu_broker.Broker):
    @property
    def actual_resident(self):
        return ["llm.assistant.8b@16k", "comfyui.sdxl"]      # 多出一个没人授权的


_bf = _FakeActual(cfg=gpu_broker.BROKER.cfg)
_bf._committed = ["llm.assistant.8b@16k"]
_r3 = _inv(_bf, "I3")
check("★★ 出现未授权驻留 → I3 判违反", not _r3.holds, _r3.detail[:100])
check("点名是哪个组件", "comfyui.sdxl" in _r3.detail)
check("指向 §9.3 告警", "9.3" in _r3.detail or "告警" in _r3.detail)
#   permitted_on_demand 里的不算违反 —— 这正是两个字段不能合并的理由
_bf._permitted_on_demand = ["comfyui.sdxl"]
check("★ 在 permitted_on_demand 里的不算违反(按需槽是被授权的)", _inv(_bf, "I3").holds)

print("=== 27. ★★ I4 电源轴与意图轴分离:关电【不吞掉】用户的勾选 ===")


async def _power_test():
    b = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
    b._intended = ["llm.assistant.8b@16k", "speech.lite"]
    b._committed = ["llm.assistant.8b@16k", "speech.lite"]
    before = list(b._intended)
    await b.set_power(False)
    return before, list(b._intended), list(b._committed), b.snapshot()


_before, _after_intended, _after_committed, _snap4 = asyncio.run(_power_test())
check("★★ 关电后 intended 一个字没动(否则等于系统改写了用户配置)",
      _after_intended == _before, f"{_before} -> {_after_intended}")
check("关电后 committed 清空", _after_committed == [])
check("I4 成立", next(r for r in _snap4.invariants if r["invariant"] == "I4")["holds"])
_p4src = inspect.getsource(gpu_broker.Broker.set_power)
# ★ 只看会执行的代码:去 docstring、去注释。第一版栽在注释里那句
#   「self._intended 一个字都不动」上 —— 它正是在说明"不碰",却被当成了"碰了"。
#   与本文件第 20 组、e4_egress、Body() 是同一条纪律。
_p4code = re.sub(r'"""(?:.|\n)*?"""', "", _p4src)
_p4code = "\n".join(l for l in _p4code.splitlines() if not l.lstrip().startswith("#"))
check("★ set_power 的【代码】里绝不给 _intended 赋值",
      not re.search(r"self\._intended\s*=[^=]", _p4code), _p4code[:160])
check("源码写明 ON 回来按 intended 重装、不需重勾",
      "不该吞掉" in _p4src or "重新装载" in inspect.getsource(gpu_broker.Broker.check_invariants))

print("=== 28. ★★ RECONCILE_WATCH:只报告,【不修复】 ===")
_loop = inspect.getsource(gpu_broker.Broker._sampler_loop)
check("watch 挂在采样循环里", "check_invariants" in _loop)
check("★★ 只把结果记下来(赋值给 _last_watch),不做任何修正动作",
      "_last_watch = self.check_invariants()" in _loop)
check("★ watch 里没有任何写状态集合的动作(修复即『自动触发』,D10 明令禁止)",
      not any(w in _loop for w in ("_committed =", "_intended =", "_permitted_on_demand =",
                                   "set_power", "grant(", "release(")))
_ci = inspect.getsource(gpu_broker.Broker.check_invariants)
# ★ 判据要排除【比较】:`self._state == STATE_READY` 里含有 `self._state =` 这个前缀,
#   第一版因此误红。用 =[^=] 把赋值与比较分开。
check("★ 检测器本身是纯读的(不许有副作用)",
      not re.search(r"self\.(_committed|_intended|_permitted_on_demand|_state|_generation)\s*=[^=]", _ci),
      "检测器有副作用就不再是检测器")
check("源码写明「只报告不修复」的理由", "不做自动触发" in _loop or "只报告" in _loop)

print("=== 29. ★★ 置信度必须如实:actual 今天不是独立观测 ===")
#   没有装载器 + WDDM 不暴露逐进程显存 ⇒ actual 只能是 Broker 自己的账本。
#   用自己的账本跟自己的账本比,永远相等 —— 不标 confidence 就是个假检测器。
_reports = {r.invariant: r for r in gpu_broker.BROKER.check_invariants()}
check("I2 标为 self_reported", _reports["I2"].confidence == "self_reported", _reports["I2"].confidence)
check("I3 标为 self_reported", _reports["I3"].confidence == "self_reported")
check("★ I4 是 structural(电源轴是我们自己的状态,确实可观测)",
      _reports["I4"].confidence == "structural")
_asrc = inspect.getsource(type(gpu_broker.BROKER).actual_resident.fget)
check("★ actual_resident 的文档写明它今天不是独立观测", "不是独立观测" in _asrc)
check("★ 并写明两条结构性原因(无装载器 + WDDM)",
      "装载器" in _asrc and "WDDM" in _asrc)
check("★ 并写明不标 confidence 就是假检测器", "假检测器" in _asrc)
check("快照里每条不变式都带 confidence",
      all("confidence" in i for i in _sj["invariants"]))

print("\n=== 12. P4-S8 · 状态机白名单(反向全表)===")
_AT = gpu_broker.ALLOWED_TRANSITIONS
check("★ 反向全表:ALL_STATES 每个状态都在转换表里登记(加新状态不登记必红)",
      sorted(_AT) == sorted(gpu_broker.ALL_STATES),
      f"缺 {set(gpu_broker.ALL_STATES) - set(_AT)}")
check("★ 转换表里没有指向未登记状态的孤儿边",
      all(t in gpu_broker.ALL_STATES for tos in _AT.values() for t in tos))
check("SERVING_STATES 是 ALL_STATES 的子集,没有「凭空多出来」的状态",
      set(gpu_broker.SERVING_STATES) <= set(gpu_broker.ALL_STATES))
check("★ DEGRADED_SAFE 是终态:唯一出口是 STARTING(不能自动回 READY)",
      _AT[gpu_broker.STATE_DEGRADED_SAFE] == frozenset({gpu_broker.STATE_STARTING}))
check("★ RECONCILING 仍然提供服务(一个 worker 掉线不该把还活着的也判死)",
      gpu_broker.STATE_RECONCILING in gpu_broker.SERVING_STATES)
check("★ DEGRADED_SAFE 不提供服务(等价 Off)",
      gpu_broker.STATE_DEGRADED_SAFE not in gpu_broker.SERVING_STATES)
check("预检的去处只有两个:通过进 APPLYING,不过回 STAGING 编辑态",
      _AT[gpu_broker.STATE_PRECHECK] == frozenset({gpu_broker.STATE_APPLYING,
                                                   gpu_broker.STATE_STAGING}))


async def _trans_test():
    b = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
    out = {}
    async with b._lock:
        await b._transition(gpu_broker.STATE_READY, "t")
        out["ok_path"] = b._state
        try:
            await b._transition(gpu_broker.STATE_DEGRADED_SAFE, "跳级")
            out["illegal"] = "未拒绝"
        except gpu_broker.IllegalTransition:
            out["illegal"] = "拒绝"
        out["state_after_illegal"] = b._state
        b._state = "MADE_UP_STATE"          # 未登记源状态 → 必须失败关闭
        try:
            await b._transition(gpu_broker.STATE_READY, "t")
            out["unknown_src"] = "放行"
        except gpu_broker.IllegalTransition:
            out["unknown_src"] = "拒绝"
        b._state = gpu_broker.STATE_DEGRADED_SAFE
    await b.set_power(False)
    out["degraded_after_off"] = b._state
    await b.set_power(True)
    out["degraded_exit"] = b._state
    return out


_t = asyncio.run(_trans_test())
check("合法转换生效(STARTING → READY)", _t["ok_path"] == gpu_broker.STATE_READY)
check("★ 非法转换【抛异常】,不是静默忽略(忽略=看着有约束实际没有)",
      _t["illegal"] == "拒绝")
check("非法转换后状态没被改坏", _t["state_after_illegal"] == gpu_broker.STATE_READY)
check("★ 失败关闭:未登记的源状态一律拒绝,而不是默认放行",
      _t["unknown_src"] == "拒绝")
check("★ DEGRADED_SAFE 只能靠人重开电源轴离开(没有自动恢复,D10)",
      _t["degraded_after_off"] == gpu_broker.STATE_DEGRADED_SAFE
      and _t["degraded_exit"] == gpu_broker.STATE_STARTING,
      f'{_t["degraded_after_off"]} → {_t["degraded_exit"]}')

_src_nodoc = re.sub(r"#.*", "", re.sub(r'"""(?:.|\n)*?"""', "", inspect.getsource(gpu_broker)))
_bare = re.findall(r"self\._state\s*=[^=]", _src_nodoc)
check("★ _transition 是状态改写的唯一入口(裸赋值只允许两处)",
      len(_bare) == 2,
      f"裸赋值 {len(_bare)} 处,应为 2(_transition 一处 + set_power 的 DEGRADED_SAFE 出口一处)")

print("\n=== 13. P4-S8 · 「确定 = 一次事务」===")


class _FakeLoader:
    """★ 只有测试里才有。生产里 `_loader is None` —— 见 Broker.__init__ 的说明。"""

    def __init__(self, fail_load=(), fail_rollback=False):
        self.fail_load = set(fail_load)
        self.fail_rollback = fail_rollback
        self.calls = []
        # ★ S16b:记一份"真的在跑的是哪些" —— 按需驻留的用例要能让 actual_resident
        #   变成**独立观测**(否则它退回账本,I2 的作用域收窄就测不出来:
        #   拿账本跟账本比,减不减 transient 都相等,那是个假检测器)。
        self.loaded = set()

    async def unload(self, ids):
        self.calls.append(("unload", list(ids)))
        self.loaded -= set(ids)
        # ★ V16:回执三分。替身也要**如实**回报,不许返回一个空壳 ——
        #   一个总说"都杀干净了"的替身,会让真实现里的那条核实路径永远测不到。
        return {"killed": list(ids), "skipped_adopted": [], "kill_failed": []}

    async def verify_unloaded(self, ids, keep):
        """★ V16:替身的核实必须问**它自己那个世界**的真相(self.loaded),
        不能硬编码成空列表 —— 那样它就是在替被测代码打掩护。"""
        self.calls.append(("verify", list(ids)))
        return [{"component": c, "port": 0, "port_state": "ready",
                 "we_spawned_it": True, "pid": None, "why": "替身:还在 loaded 里"}
                for c in ids if c in self.loaded]

    async def readopt(self, ids):
        self.calls.append(("readopt", list(ids)))
        return [c for c in ids if c in self.loaded]

    async def residency_truth(self):
        return {"live_ports": [], "ledger_ports": [], "orphan_ports": [],
                "orphan_candidates": {}, "probed": 0, "note": "替身"}

    async def load(self, ids):
        self.calls.append(("load", list(ids)))
        if self.fail_rollback and len([c for c in self.calls if c[0] == "load"]) > 1:
            raise RuntimeError("回滚也装不上")
        if set(ids) & self.fail_load:
            raise RuntimeError("装载失败")
        self.loaded |= set(ids)

    async def running(self):
        return sorted(self.loaded)

    async def adopt(self):
        # ★ 不认领任何东西 —— 认领会让测试去动**真实系统**的状态(见 _NoAdoptLoader 那段的理由)
        return []


def _mkbroker(free, loader=None, state=None):
    b = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
    b._state = state or gpu_broker.STATE_READY
    b._loader = loader
    b._free = free
    b._sampled_at = 0.0
    b._sample_once = lambda: None          # 冻住采样,由 _free 直接给值
    return b


_small = min(gpu_broker.BROKER.cfg.components, key=lambda c: gpu_broker.BROKER.cfg.peak(c))


async def _tx_tests():
    o = {}
    # ① 预检不过 → 回编辑态,committed 一字未动
    b = _mkbroker(free=0.05)
    b._committed = [_small]
    r = await b.apply_intended([_small])
    o["reject"] = (r.ok, r.code, r.state, list(b._committed))

    # ② ★ 装载器缺席 → 失败关闭,绝不到 READY
    b2 = _mkbroker(free=64.0, loader=None)
    r2 = await b2.apply_intended([_small])
    o["absent"] = (r2.ok, r2.code, r2.state, list(b2._committed))

    # ③ 成功事务
    b3 = _mkbroker(free=64.0, loader=_FakeLoader())
    r3 = await b3.apply_intended([_small], permitted=[])
    o["ok"] = (r3.ok, r3.state, list(b3._committed), list(b3._intended))

    # ④ 装载失败 → 回滚 → RECONCILING,committed 回到 prev
    b4 = _mkbroker(free=64.0, loader=_FakeLoader(fail_load={_small}))
    b4._committed = []
    r4 = await b4.apply_intended([_small])
    o["rollback"] = (r4.ok, r4.code, r4.state, list(b4._committed))

    # ⑤ 回滚也失败 → DEGRADED_SAFE
    b5 = _mkbroker(free=64.0, loader=_FakeLoader(fail_load={_small}, fail_rollback=True))
    r5 = await b5.apply_intended([_small])
    o["degraded"] = (r5.ok, r5.code, r5.state)

    # ⑥ 非 READY 状态拒新事务(单写者)
    b6 = _mkbroker(free=64.0, loader=_FakeLoader(), state=gpu_broker.STATE_APPLYING)
    r6 = await b6.apply_intended([_small])
    o["busy"] = (r6.ok, r6.code)

    # ⑦ ★ 重新求值:预览时够、确定时不够
    b7 = _mkbroker(free=64.0, loader=_FakeLoader())
    pre = gpu_broker.vram_gate.evaluate([_small], b7.cfg, free=64.0)   # 预览:过
    b7._free = 0.05                                                    # 期间桌面吃满了
    r7 = await b7.apply_intended([_small])
    o["revalue"] = (pre.ok, r7.ok, r7.code)

    # ⑧ blocking_set:**点名了组件**的租约 → 交还用户裁定,不动 committed
    #
    # ★★★ 2026-08-06 修正(审计 A1②)。这一段原来有两处错,而且互相掩护:
    #   ① 它改的是**全局** `gpu_broker.DRAIN_WINDOW_S = 0.01` ——
    #      **把 5 秒代价整个盖住了**。所有用例都在 0.01 秒的世界里跑,于是
    #      「用户点确定要真的等 5 秒」这件事**从来没有被任何断言碰过**;
    #      而下面那条 `DRAIN_WINDOW_S == 5.0` 在改回去之后照样绿
    #      ⇒ 常量看着被钉住了,**行为一次都没被验过**。
    #   ② 它给 `client_session` 传了 `[_small]` —— 而**真实客户端传的是空**
    #      (`LeaseKeeper.cs`:「本类不申请任何组件……声明的是"有人在"」)
    #      ⇒ 它钉死的是一个**生产里从不发生**的形状,顺带把
    #      「用户自己的会话挡住用户自己的变更」当成了正确行为。
    #   ⇒ 现在:**改实例不改全局**;用真会点名组件的 kind(agent_task);
    #     并单独钉一条**反向**断言 —— 空组件的 client_session 不得挡住变更。
    b8 = _mkbroker(free=64.0, loader=_FakeLoader())
    b8.drain_window_s = 0.01                                     # ★ 改实例,不改全局
    b8._committed = [_small]
    await b8.grant("agent_task", "PC-B", [_small], ttl_s=30.0)   # 点名了组件 = 有活在跑
    r8 = await b8.apply_intended([])
    o["blocked"] = (r8.ok, r8.code, len(r8.blocking), list(b8._committed))
    b8._state = gpu_broker.STATE_READY
    r8b = await b8.apply_intended([_small], interrupt_running=True)
    o["interrupt"] = (r8b.ok, r8b.state)

    # ⑧b ★★★ 反向:**空组件**的 client_session —— 正是真实客户端持的那一份 ——
    #     不得挡住用户自己的变更。
    #     ★ 这里**故意用默认的 5.0**:万一它又开始挡了,这条不但会红,
    #       还会**等满 5 秒才红** —— 那个耗时本身就是证据。
    b8c = _mkbroker(free=64.0, loader=_FakeLoader())
    await b8c.grant("client_session", "PC-A", [], ttl_s=30.0)
    _t0 = time.monotonic()
    r8c = await b8c.apply_intended([_small])
    o["idle_session"] = (r8c.ok, r8c.code, len(r8c.blocking), time.monotonic() - _t0)

    # ⑧c ★★ 5 秒排空窗口**真的被付一次**。
    #     没有这条,「排空窗口存在」就只是一个常量断言 —— 而常量绿不代表代码走过它。
    b8d = _mkbroker(free=64.0, loader=_FakeLoader())
    b8d._committed = [_small]
    await b8d.grant("agent_task", "PC-B", [_small], ttl_s=30.0)
    _t1 = time.monotonic()
    r8d = await b8d.apply_intended([])          # ★ 不注入 ⇒ 走默认 5.0
    o["drain_cost"] = (r8d.code, time.monotonic() - _t1)

    # ⑨ 回收超时 → vram_not_reclaimed
    b9 = _mkbroker(free=1.0)
    err = await b9._await_reclaim(expect_free=50.0, timeout=0.15, poll=0.02)
    ok2 = await b9._await_reclaim(expect_free=1.1, timeout=0.15, poll=0.02)  # 在 ±0.2 内
    o["reclaim"] = (err, ok2)
    return o


_x = asyncio.run(_tx_tests())
check("预检不过 → 回 STAGING 编辑态", _x["reject"][2] == gpu_broker.STATE_STAGING, _x["reject"])
check("★ 预检不过时【一个组件都没卸】—— committed 一字未动",
      _x["reject"][3] == [_small], _x["reject"][3])
check("预检不过的 code 指出是哪道闸", _x["reject"][1].startswith("gate_"), _x["reject"][1])
check("★★★ 装载器缺席 → 失败关闭,code=loader_absent", _x["absent"][1] == "loader_absent",
      _x["absent"])
check("★★★ 装载器缺席时【绝不到达 READY】(否则报 READY 而显存里什么都没有)",
      _x["absent"][2] != gpu_broker.STATE_READY and _x["absent"][0] is False, _x["absent"])
check("装载器缺席时 committed 未被写入", _x["absent"][3] == [])
check("事务成功 → READY 且 committed/intended 都落到申请集合",
      _x["ok"][0] and _x["ok"][1] == gpu_broker.STATE_READY
      and _x["ok"][2] == [_small] and _x["ok"][3] == [_small], _x["ok"])
check("★ 装载失败 → 回滚到上一个成功集合,落 RECONCILING",
      _x["rollback"][2] == gpu_broker.STATE_RECONCILING and _x["rollback"][3] == [],
      _x["rollback"])
check("★ 回滚也失败 → DEGRADED_SAFE(等价 Off + 托盘红 + 不可忽略通知)",
      _x["degraded"][2] == gpu_broker.STATE_DEGRADED_SAFE
      and _x["degraded"][1] == "rollback_failed", _x["degraded"])
check("单写者:非 READY 状态拒新事务", _x["busy"][1] == "busy", _x["busy"])
check("★★ 点确定时【重新求值】:预览过 → 确定时不过(挑组件几十秒,期间桌面会变)",
      _x["revalue"][0] is True and _x["revalue"][1] is False
      and _x["revalue"][2].startswith("gate_"), _x["revalue"])
check("★ 有任务在跑(租约**点名了组件**)→ needs_user_choice,并点名是哪几条租约",
      _x["blocked"][1] == "needs_user_choice" and _x["blocked"][2] == 1, _x["blocked"])
check("★ 交还用户裁定时 committed 一字未动", _x["blocked"][3] == [_small])
check("★★★ 反向(A1②):**空组件**的 client_session ——真实客户端持的正是这一份——"
      "【不挡】用户自己的变更。挡住的话,用户改驻留集合会被自己的会话拦下,"
      "还要等满 5 秒,最后被问「有任务在跑」,而根本没有任何任务在跑",
      _x["idle_session"][0] is True and _x["idle_session"][2] == 0, _x["idle_session"])
check("★★ 而且它**一秒都没等** —— 空组件根本进不了 blocking_leases,自然没有排空窗口。"
      "(这条同时是上一条的独立佐证:即便 ok 被别的原因弄成 True,耗时也瞒不住)",
      _x["idle_session"][3] < 1.0, f'{_x["idle_session"][3]:.2f}s')
check("★★★ 5 秒排空窗口**真的被付了一次** —— 此前门禁把全局改成 0.01,"
      "这个代价从来没有被任何断言碰过(常量绿 ≠ 代码走过它)",
      _x["drain_cost"][0] == "needs_user_choice" and _x["drain_cost"][1] >= 5.0,
      f'{_x["drain_cost"][0]} · 实际等了 {_x["drain_cost"][1]:.2f}s')
check("用户选『优雅中断』后事务照常走完",
      _x["interrupt"][0] and _x["interrupt"][1] == gpu_broker.STATE_READY, _x["interrupt"])
check("★ 显存没回收到 ±0.2 GiB → vram_not_reclaimed(不是「大概回收了就算了」)",
      _x["reclaim"][0] == "vram_not_reclaimed", _x["reclaim"])
check("回收到容差内 → 通过", _x["reclaim"][1] is None)


def _nodoc(fn):
    return re.sub(r"#.*", "", re.sub(r'"""(?:.|\n)*?"""', "", inspect.getsource(fn)))


_apply_nodoc = _nodoc(gpu_broker.Broker.apply_intended)
check("★ 一律先卸后装:源码里 unload 出现在 load 之前",
      _apply_nodoc.index(".unload(") < _apply_nodoc.index(".load("))
check("★ loader_absent 的检查在进入 APPLYING 【之前】(否则就是先卸了再发现装不了)",
      _apply_nodoc.index("loader_absent") < _apply_nodoc.index("STATE_APPLYING"))
check("★★ 预检不过的落点上【没有任何一行写 _committed】(方案书第 2 条的字面落实)",
      "_committed" not in _nodoc(gpu_broker.Broker._back_to_staging))
check("DRAIN_WINDOW_S = 5 秒排空窗口(方案书 §8.1.6)", gpu_broker.DRAIN_WINDOW_S == 5.0)
check("★ 新建的 Broker 默认就取模块常量(不是另抄一个数字)",
      gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg).drain_window_s == gpu_broker.DRAIN_WINDOW_S)
check("★★ apply_intended 必须睡 **self.drain_window_s** 而不是模块常量 —— "
      "睡常量的话那个可注入属性就只是装饰品,测试改了它也不起作用",
      "self.drain_window_s" in _apply_nodoc and "sleep(DRAIN_WINDOW_S)" not in _apply_nodoc)


# ══════════════════════════════════════════════════════════════════════
#  ★★★ A1② · blocking_leases 的**逐 kind 反向全表**
#
#  规矩:`blocking_leases()` 回答的是「改动驻留集合会不会杀掉正在跑的活」。
#  一份 components 为空的租约**没有点名任何组件** ⇒ 改任何组件都打不断它
#  ⇒ 它声明的是「有人在」,不是「有活在跑」。
#
#  ★ 两个方向都钉(只钉一边等于没钉):
#    · 空组件 ⇒ **不得**出现在 blocking_leases 里(漏了这条,用户会被自己的会话挡住);
#    · 有组件 ⇒ **必须**出现(漏了这条,一次变更会静默杀掉正在跑的活)。
#  ★ 逐 kind 走全表 —— 新加一种 kind 时,它落哪边必须是被想过的,而不是继承来的。
# ══════════════════════════════════════════════════════════════════════
print("\n=== A1②:blocking_leases 逐 kind 反向全表(空组件 = 有人在,不是有活在跑) ===")


async def _t_blocking_by_kind():
    out = {}
    for _k in gpu_broker.LEASE_KINDS:
        b_empty = _mkbroker(free=64.0)
        st_e, _ = await b_empty.grant(_k, "H", [], ttl_s=30.0)
        b_named = _mkbroker(free=64.0)
        st_n, _ = await b_named.grant(_k, "H", [_small], ttl_s=30.0)
        out[_k] = (st_e, len(b_empty.blocking_leases()),
                   st_n, len(b_named.blocking_leases()))
    return out


_bk = asyncio.run(_t_blocking_by_kind())
for _k, _v in _bk.items():
    check(f"★ [{_k}] 两次 grant 都成功(前提成立,否则下面两条是空跑)",
          _v[0] == gpu_broker.LEASE_OK and _v[2] == gpu_broker.LEASE_OK, _v)
    check(f"★★ [{_k}] **空组件 ⇒ 不阻塞**(「有人在」不是「有活在跑」)", _v[1] == 0, _v)
    check(f"★★ [{_k}] **点名了组件 ⇒ 阻塞**(反过来钉:漏了它,变更会静默杀掉正在跑的活)",
          _v[3] == 1, _v)
check("★ 全表覆盖:LEASE_KINDS 里每一个 kind 都被上面两个方向各钉过一次",
      set(_bk) == set(gpu_broker.LEASE_KINDS), sorted(set(gpu_broker.LEASE_KINDS) - set(_bk)))
check("★★★ blocking_leases 的源码里确实有 components 这个条件 —— "
      "行为断言 + 结构断言各一条:行为那条证明它今天是对的,"
      "结构这条让「顺手把条件删掉」当场可见",
      "l.components" in _nodoc(gpu_broker.Broker.blocking_leases))
check("回收容差 / 超时与方案书行 1507 一致(±0.2 GiB / 10 s)",
      gpu_broker.RECLAIM_TOLERANCE_GIB == 0.2 and gpu_broker.RECLAIM_TIMEOUT_S == 10.0)
check("★ _await_reclaim 的 timeout 走 None 而非默认参数绑常量(否则测试改不动,断言只能抄数字)",
      inspect.signature(gpu_broker.Broker._await_reclaim).parameters["timeout"].default is None)

print("\n=== 14. P4-S8 · admission_guard(通用降幅,不看进程名)===")
_ag_nodoc = _nodoc(gpu_broker.Broker.admission_guard)
check("★★ admission_guard 全程不读进程名(原文「检测独占全屏游戏」特例已删除)",
      not any(w in _ag_nodoc.lower() for w in ("process", "proc_name", "exe", "fullscreen", "game")),
      _ag_nodoc)
check("常量与方案书行 1623 一致:5 s 窗口 / 1.0 GiB 降幅",
      gpu_broker.ADMISSION_GUARD_WINDOW_S == 5.0 and gpu_broker.ADMISSION_GUARD_DROP_GIB == 1.0)

_bg = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
_bg._free_history = [(100.0, 12.0), (101.0, 11.8), (102.0, 9.5)]
_g1 = _bg.admission_guard()
_bg._free_history = [(100.0, 12.0), (102.0, 11.4)]
_g2 = _bg.admission_guard()
_bg._free_history = [(80.0, 12.0), (100.0, 9.0), (101.0, 9.0)]   # 降幅在 5 s 窗口【之外】
_g3 = _bg.admission_guard()
_bg._free_history = [(100.0, None), (101.0, None)]
_g4 = _bg.admission_guard()
check("5 s 内降 2.5 GiB → 触发", _g1 is not None and _g1["drop_gib"] == 2.5, _g1)
check("触发后的动作是拒新申请", _g1 and _g1["action"] == "refuse_new_admission")
check("降 0.6 GiB(≤1.0)→ 不触发", _g2 is None, _g2)
check("★ 窗口【之外】的降幅不算(否则开机以来的总降幅会永远触发)", _g3 is None, _g3)
check("采样失败(None)不被当成降幅", _g4 is None, _g4)
_so = _nodoc(gpu_broker.Broker._sample_once)
check("★ 采样失败也要入历史(否则「采不到」会被当成「没变化」,故障反而更安静)",
      "_free_history.append" in _so and "finally" in _so)
check("历史有上界,不会无界增长", "FREE_HISTORY_MAX" in _so)

print("\n=== 15. P4-S8 · 失败落点(D24 排查带出,原文完全没定义过)===")


async def _fl_test():
    b = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
    _, l1 = await b.grant("model_ref", "h", [_small], ttl_s=30.0)        # evictable
    _, l2 = await b.grant("client_session", "h", [_small], ttl_s=30.0)   # 不可驱逐
    return b.failure_landing(), l1.lease_id, l2.lease_id


_fl, _lev, _lpin = asyncio.run(_fl_test())
check("★★ 失败恒落在 AI 侧,不得由桌面承担分配失败",
      _fl["then_lands_on"] == "ai" and _fl["never"] == "desktop", _fl)
check("★★ 且明写这是【策略不是保证】(WDDM 不按优先级驱逐)", _fl["guarantee"] is False)
check("可驱逐租约排在先驱逐名单里", _lev in _fl["evict_first"], _fl)
check("不可驱逐租约不在驱逐名单、而在 pinned",
      _lpin not in _fl["evict_first"] and _lpin in _fl["pinned_not_evicted"], _fl)
check("AI 侧的动作就是拒新申请 / DEGRADED_SAFE",
      _fl["ai_actions"] == ["refuse_new_admission", "degraded_safe"])
_fls = inspect.getsource(gpu_broker.Broker.failure_landing)
check("★ 文档写明 WDDM 不按优先级驱逐 —— 不得声称能保证桌面不被挤",
      "WDDM" in _fls and "不按优先级驱逐" in _fls and "不等于" in _fls)
check("blocking_set 恰好是方案书那三个",
      gpu_broker.BLOCKING_SET == frozenset({gpu_broker.BLOCKING_USER,
                                            gpu_broker.BLOCKING_ASYNC,
                                            gpu_broker.BLOCKING_RESIDENT}))

print("\n=== 16. P4-S9 · STARTING 的出口(此前根本没有)===")
# ★ 实测撞出来的:Broker 从建好那一刻起就停在 STARTING,而 apply_intended 只接受 READY
#   ⇒ 整条事务路径【从来走不到】,线上表现是恒返回 busy。


class _BrokerWithRealActual(gpu_broker.Broker):
    """★ 注入一个**独立于账本**的 actual —— 这是 P5 接上装载器之后的形状。
    今天生产代码里 actual_resident 就是 _committed 本身,所以只有这样才能
    真的把 STARTING → RECONCILING 那条分支执行到(不留"从没跑过的分支")。"""

    fake_actual: list = []

    @property
    def actual_resident(self):
        return list(self.fake_actual)


async def _startup_test():
    o = {}
    b1 = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)          # 空集合:actual == committed
    o["empty"] = await b1.finish_startup()
    b2 = _BrokerWithRealActual(cfg=gpu_broker.BROKER.cfg)
    b2._committed = [_small]
    b2.fake_actual = []                                         # 账面装了一个,实际一个都没装
    o["short"] = await b2.finish_startup()
    o["short_serves"] = b2.serves_requests()
    b3 = _BrokerWithRealActual(cfg=gpu_broker.BROKER.cfg)
    b3._committed = [_small]
    b3.fake_actual = [_small]                                   # 装齐了
    o["match"] = await b3.finish_startup()
    b4 = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
    await b4.finish_startup()
    o["idem"] = await b4.finish_startup()                        # 二次调用不该再动
    return o


_su = asyncio.run(_startup_test())
check("★ 空集合 ⇒ actual == committed ⇒ 进 READY", _su["empty"] == gpu_broker.STATE_READY, _su)
check("★★ 装载没齐时【不宣布 READY】—— 放行条件就是 I2 的后件,不给启动阶段开例外",
      _su["short"] == gpu_broker.STATE_RECONCILING, _su)
check("★ 且装载没齐时仍然对外提供服务(不把还活着的一并判死)", _su["short_serves"] is True)
check("装齐了才进 READY", _su["match"] == gpu_broker.STATE_READY, _su)
check("finish_startup 幂等(已离开 STARTING 就不再动)", _su["idem"] == gpu_broker.STATE_READY)
_fs = _nodoc(gpu_broker.Broker.finish_startup)
check("★ 放行判据确实是 actual 与 committed 相等,不是「反正启动阶段先放过」",
      "actual == committed" in _fs and "STATE_READY" in _fs)

# ★★★ 钉住那条【恒真性】本身。
#   今天 actual_resident 就是 _committed(S7 已如实标注:无装载器 + WDDM 不暴露逐进程显存),
#   所以 finish_startup 的判据等价于 committed == committed —— **它今天不是检测器,是形状**。
#   这条断言的用处是:P5 让 actual 变成独立观测的那天,它会红,提醒回来把它当真检测器复核。
# ★★★ 2026-08-05(S14)改写。这条原本是「记录恒真性」的绊线,写着
#   「若这条红了 = actual 已变成独立观测」。装载器接上之后 actual **确实**变成独立观测了,
#   而绊线**没有响** —— 因为它测的是一个【没接装载器】的 Broker,那条路径上恒真性仍然成立。
#   ⇒ 绊线本身写窄了:它守的是"某个 Broker 实例",而该守的是**两条路径各自的性质**。
#   ★ 这是「断言也会说假话」的又一种形态:不是说了假话,是**根本没在看那件事**。
#     修法是把两条分支都显式钉住,而不是删掉。
_probe = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
_probe._committed = [_small]
check("★ 没有装载器时:actual 退回账本(恒真),confidence 如实标 self_reported",
      list(_probe.actual_resident) == list(_probe._committed)
      and {r.invariant: r.confidence for r in _probe.check_invariants()}.get("I2") == "self_reported")


class _FakeLoaderObs:
    """★ 独立事实源的替身:它报的东西**与账本无关** —— 这正是 S14 之后的真实形态。"""

    def __init__(self, actual):
        self._a = list(actual)

    async def running(self):
        return list(self._a)


_probe2 = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
_probe2.attach_loader(_FakeLoaderObs([]))
_probe2._committed = [_small]
_probe2._actual_cache = []          # ★ 装载器说"一个都没装",而账本说装了一个
check("★★★ 接上装载器后:actual **不再等于账本** —— 它是独立观测",
      list(_probe2.actual_resident) != list(_probe2._committed),
      f"actual={_probe2.actual_resident} committed={_probe2._committed}")
check("★★ confidence 随之升为 observed(跟着事实源走,不是写死的)",
      {r.invariant: r.confidence for r in _probe2.check_invariants()}.get("I2") == "observed")
_probe2._state = gpu_broker.STATE_READY
check("★★★ 而 I2 现在**真的抓得住谎**:账本说装了、实际没装 ⇒ 判违反。"
      "这正是 S7 写它的目的 —— 它当了一天恒真式,今天才第一次有活干",
      not {r.invariant: r.holds for r in _probe2.check_invariants()}["I2"])
check("★ I3 的 confidence 与 I2 同源(同一个事实源,不该一个 observed 一个 self_reported)",
      len({r.confidence for r in _probe2.check_invariants() if r.invariant in ("I2", "I3")}) == 1)

print("\n=== 17. P4-S9 · 事务可【重试】—— S8 漏掉的那条路径 ===")
# ★★ 这一组是补 S8 的洞:预检不过的落点是 STAGING,而 apply_intended 原来只收 READY
#   ⇒ 用户改完选择再点一次确定,得到的是 busy。
#   S8 的测试每个 broker 只跑一次事务,**恰好把它盖住了** —— 不是断言写错,是没构造第二次。
check("★ ACCEPTS_TRANSACTION 恰好是 READY 与 STAGING 两个",
      gpu_broker.ACCEPTS_TRANSACTION == frozenset({gpu_broker.STATE_READY,
                                                   gpu_broker.STATE_STAGING}),
      sorted(gpu_broker.ACCEPTS_TRANSACTION))
check("★ 正在跑的那几个状态一律不许再发起(否则就是双写者)",
      not (gpu_broker.ACCEPTS_TRANSACTION & {gpu_broker.STATE_PRECHECK, gpu_broker.STATE_APPLYING,
                                             gpu_broker.STATE_RECONCILING,
                                             gpu_broker.STATE_DEGRADED_SAFE,
                                             gpu_broker.STATE_STARTING}))
check("★ STAGING 不是锁:它同时在 SERVING_STATES 里(面板开着不该把服务停掉)",
      gpu_broker.STATE_STAGING in gpu_broker.SERVING_STATES)


async def _retry_test():
    b = _mkbroker(free=0.05, loader=_FakeLoader())
    r1 = await b.apply_intended([_small])                       # 预检不过 → STAGING
    r2 = await b.apply_intended([_small])                       # ★ 改完再点一次
    b._free = 64.0
    r3 = await b.apply_intended([_small])                       # 这次该过
    return (r1.code, r1.state), (r2.code, r2.state), (r3.ok, r3.state)


_rt = asyncio.run(_retry_test())
check("第一次预检不过 → STAGING", _rt[0][1] == gpu_broker.STATE_STAGING, _rt[0])
check("★★ 第二次【不是 busy】—— 重试路径通(S8 的洞)",
      _rt[1][0] != "busy", _rt[1])
check("★ 第二次拿到的是它自己的判据(仍是闸拒,不是被状态挡住)",
      _rt[1][0].startswith("gate_"), _rt[1])
check("★ 条件变好后同一个 broker 能把事务走完", _rt[2][0] and _rt[2][1] == gpu_broker.STATE_READY, _rt[2])

print("\n=== 18. P4-S9 · 组件目录:准入白名单本身,不是它的摘抄 ===")
import json as _json
import warnings as _warnings

_warnings.filterwarnings("ignore")
from starlette.testclient import TestClient as _TC   # noqa: E402

# ★★ P4-S10 之后必须【显式声明档位】。此前这一段是在"人人都能改"的世界里写的:
#   GPU 面只挡 remote-unauthenticated,于是测试跑成什么档位都无所谓。
#   六元组落地后,本机账户若不在 caller-accounts.toml 的 allowlist 里会落 unregistered-local
#   ⇒ 变更端点 403。让测试显式说明"我以哪个档位在测",比依赖环境凑巧更结实。
_cc_saved = gateway.classify_caller
gateway.classify_caller = lambda r: "trusted-local"

# ══════════════════════════════════════════════════════════════════════
#  ★★★ 2026-08-06:这一段端点测试原来用的是**全局 BROKER 单例**,
#  而 TestClient 会触发 @app.on_event("startup") —— 于是它
#  ① 接上一个**真装载器**,② `adopt_running()` **认领正在跑的 llama-server**。
#
#  后果实测:栈起着的时候,committed 变成真模型 ⇒ 一次 apply 会先去**卸它** ⇒
#  卸不掉(认领来的进程按设计不杀)⇒ 回 vram_not_reclaimed,
#  于是三条断言集体变红,而它们自称在测的是"装载器拒收未验证的 kind"。
#  ★ 这正是 ASSERTION-PITFALLS 第 5 条:**一条断言若会因为「功能终于能用了」而变红,
#    它测的就不是它自称在测的东西**。而且这一次更糟 —— 它还会**动到真实系统的状态**。
#
#  ⇒ 换成注入:自己的 Broker(空 committed、固定 free、冻住采样)+
#    一个**不认领任何东西**的装载器(load 的判据仍走真实现,所以"尚未验证"仍是真的)。
# ══════════════════════════════════════════════════════════════════════
import model_loader as _ml                                    # noqa: E402


class _NoAdoptLoader(_ml.ModelLoader):
    """真装载器,但**不认领**任何已在跑的后端。★ load/unload 的判据一字未改 ——
    改了的话这段测试就不再是在测真实现了。"""

    async def adopt(self):
        return []

    async def running(self):
        return []


_BROKER_saved, _ML_saved = gpu_broker.BROKER, _ml.ModelLoader
_iso = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
_iso._state = gpu_broker.STATE_READY
_iso._free = 64.0
_iso._sampled_at = 0.0
_iso._sample_once = lambda: None          # 冻住采样:判据不随桌面占用漂移
gpu_broker.BROKER = _iso
_ml.ModelLoader = _NoAdoptLoader

with _TC(gateway.app, client=("127.0.0.1", 5555)) as _c:
    _cat = _c.get("/v1/gpu/components")
    _catj = _cat.json()
    _cfg = gpu_broker.BROKER.cfg
    check("目录端点 200", _cat.status_code == 200, _cat.status_code)
    check("★★ 反向全表:目录逐条列出准入白名单的【全部】成员,一个不漏",
          {i["id"] for i in _catj["components"]} == set(_cfg.components),
          f'少了 {sorted(set(_cfg.components) - {i["id"] for i in _catj["components"]})}')
    check("★ 不做任何过滤 —— 装不下的组件也必须出现(否则「我没勾它」与「闸说装不下」无从对账)",
          len(_catj["components"]) == len(_cfg.components))
    check("每项的 peak 直接来自同一份配置,不另抄一遍数字",
          all(abs(i["peak_gib"] - _cfg.peak(i["id"])) < 1e-9 for i in _catj["components"]))
    check("★ display 缺失时回落到 id 本身(不跳过、不空串)",
          all(i["display"] for i in _catj["components"]))
    check("★ 测量出处 note 原样带出(界面照抄,不改写)",
          any("实测" in i["note"] for i in _catj["components"]))
    check("★ 别名映射由服务端下发(客户端不得自己再猜一遍 id ↔ 功能)",
          "aliases_by_component" in _catj and _catj["aliases_by_component"])
    check("★ 预算段带 safety_margin —— 面板要靠它区分两种撞墙(改预留有用 / 没用)",
          _catj["budget"]["safety_margin"] == _cfg.budget.safety_margin
          and _catj["budget"]["safety_margin"] is not None, _catj["budget"])
    check("目录带 stale / sampler_error(采样器死了必须看得见)",
          "stale" in _catj and "sampler_error" in _catj)

    print("\n=== 19. P4-S9 · 「点确定」端点:每种失败有自己的 code,且不得回 200 ===")
    _g = _c.get("/v1/gpu/snapshot").json()["generation"]
    _r_miss_gen = _c.post("/v1/gpu/intended", json={"components": []})
    _r_miss_comp = _c.post("/v1/gpu/intended", json={"if_generation": _g})
    _r_conflict = _c.post("/v1/gpu/intended", json={"if_generation": _g + 999, "components": []})
    _r_over = _c.post("/v1/gpu/intended",
                      json={"if_generation": _c.get("/v1/gpu/snapshot").json()["generation"],
                            "components": ["llm.assistant.30b-a3b@32k"]})
    # ★★★ 2026-08-06(P5 语音 v1 / D?):例子从 `speech.lite` 换成 `vlm.small`。
    #   **这是本断言第二次因为同一个原因改** —— 上一次是 S14(见下面那段注释)。
    #   原因一模一样:它守的性质是「**启动方式尚未验证**的 kind 必须 fail-closed」,
    #   而它拿来举例的那个组件**被验证了** ⇒ 例子失效,断言开始守一句已经不成立的话。
    #   · speech 的启动方式已由 `10-core/speech/verify_launch.py` **真的起过一次**
    #     (两档 ASR 在 HF_HUB_OFFLINE=1 下离线加载 + Piper 合成),
    #     读数在 `10-core/speech/launch.toml` 的 [verified] 段;
    #   · `vlm` / `comfyui` **今天仍然没有人验证过** ⇒ 换它举例,性质原样守住。
    #   ★ 没有删断言、没有放宽判据 —— 换的是**例子**,守的还是那条性质。
    #     这正是这条断言上一次改动时自己写下的规矩:「拒绝理由变了,断言必须跟着变;
    #     守着旧理由就是守一句已经不成立的话」。
    _r_ok_path = _c.post("/v1/gpu/intended",
                         json={"if_generation": _c.get("/v1/gpu/snapshot").json()["generation"],
                               "components": ["vlm.small"]})
    check("★ if_generation 必填(省略不等于「我不在乎」,那是 fail-open)",
          _r_miss_gen.status_code == 400
          and _r_miss_gen.json()["error"]["type"] == "missing_if_generation", _r_miss_gen.status_code)
    check("★★ components 必填 —— 省略【不】当成空集合(空集合意味着卸掉全部,必须写明)",
          _r_miss_comp.status_code == 400
          and _r_miss_comp.json()["error"]["type"] == "missing_components", _r_miss_comp.status_code)
    check("★ 世代号对不上 → 409 且【回带最新快照】(只回 409 会逼客户端再问一次 = 又变轮询)",
          _r_conflict.status_code == 409 and "snapshot" in _r_conflict.json(), _r_conflict.status_code)
    check("★ 超预算 → 422 且 code 指出是哪道闸(不是笼统的「失败了」)",
          _r_over.status_code == 422 and _r_over.json()["error"]["type"].startswith("gate_"),
          _json.dumps(_r_over.json().get("error"), ensure_ascii=False)[:120])
    # ★★★ 2026-08-05(S14):装载器**接上了**,这条断言随之改守新事实。
    #   它原本守「装载器缺席 → loader_absent」;而 S14 之后 speech.lite 走到的是
    #   另一条 fail-closed:**它的 kind 启动方式尚未验证**(start-stack 只起过 llm 一个后端)。
    #   ★ 两者都是 422、都不是 200 —— 承重的性质没变:**失败必须长得和成功不一样**。
    #   ★ 但拒绝理由变了,断言必须跟着变 —— 守着旧理由就是守一句已经不成立的话
    #     (这是「断言也会说假话」的第 6 次,见 ASSERTION-PITFALLS)。
    _err_type = _r_ok_path.json().get("error", {}).get("type", "")
    check("★★★ 装不了时 → 422,**不是** 200 —— 失败必须长得和成功不一样",
          _r_ok_path.status_code == 422,
          f'{_r_ok_path.status_code} {_json.dumps(_r_ok_path.json().get("error"), ensure_ascii=False)[:120]}')
    check("★★ 拒绝理由是【已登记的两种之一】,不是笼统失败",
          _err_type in ("loader_absent", "gate_admission", "gate_static", "gate_dynamic")
          or "尚未验证" in _json.dumps(_r_ok_path.json(), ensure_ascii=False),
          _err_type)
    check("★★★ 未验证过启动方式的 kind **fail-closed** —— "
          "猜一套参数的后果不是「可能起不来」,是【看起来支持而第一次真用时才炸】",
          "尚未验证" in _json.dumps(_r_ok_path.json(), ensure_ascii=False))
    check("★ 每种失败都回带变更后的快照(客户端一次就拿到重试所需的一切)",
          all("snapshot" in r.json() for r in (_r_over, _r_ok_path)))
    check("★ 事务没成时状态没被写坏:committed 仍为空",
          _r_ok_path.json()["snapshot"]["committed"] == [],
          _r_ok_path.json()["snapshot"]["committed"])

    # ══════════════════════════════════════════════════════════════════
    #  ★★★ 审计 A1 · 跨语言成对断言的【服务端那半边】
    #
    #  另一半在 `20-client-win/app/Selftest.cs`(搜 "A1 · 跨语言成对断言"),
    #  它拿**下面这张表的形状**去喂 `LeaseKeeper.TryParseGrant`,断言解析得出 lease_id。
    #
    #  ★ 为什么必须成对:A1 这一族 bug 的特征是**两边各自都绿,断的是中间那根线**。
    #    服务端这半只证明「我发的是这个形状」,客户端那半只证明「这个形状我读得懂」——
    #    任何一条单独存在都抓不住它。A 级 6 条里有 4 条是这个形状。
    #
    #  ★★ 实际发生的:服务端把 lease_id 放在 `lease` 子对象里,客户端在**顶层**找 ——
    #    于是 `AcquireAsync` 恒返回 false,而**中枢那边 grant 是真成功的**
    #    ⇒ 每次尝试都留下一份没人认领的 client_session。
    #    fence_token **恰好**在顶层拿得到,所以只有 lease_id 落空 —— 这就是它没被发现的原因。
    # ══════════════════════════════════════════════════════════════════
    print("\n=== A1:租约发放响应的顶层键集合(与客户端 TryParseGrant 成对) ===")
    _lz = _c.post("/v1/gpu/lease", json={
        "if_generation": _c.get("/v1/gpu/snapshot").json()["generation"],
        "kind": "client_session", "holder": "PC-A",
        "components": [], "ttl_s": 30.0})
    check("租约发放 200", _lz.status_code == 200, f"{_lz.status_code} {_lz.text[:160]}")
    _lzj = _lz.json()
    check("★★★ 顶层键集合**逐字钉死** —— 客户端就是照这张表解析的",
          set(_lzj.keys()) == {"status", "lease", "fence_token", "generation"},
          sorted(_lzj.keys()))
    check("★★★ lease_id 在 **lease 子对象**里(A1 病灶:客户端此前在顶层找它)",
          isinstance(_lzj.get("lease"), dict) and bool(_lzj["lease"].get("lease_id")),
          _lzj.get("lease"))
    check("★★★ **反向**:顶层【没有】lease_id —— 顶层要是也有一份,"
          "客户端那条 bug 就永远不会暴露,而它已经静默存在了整整一轮",
          "lease_id" not in _lzj, sorted(_lzj.keys()))
    check("★ fence_token **在顶层**(Lease.to_json() 里没有它)—— "
          "两个字段位置**不对称**,而这正是 A1 只伤到一个字段的原因",
          isinstance(_lzj.get("fence_token"), str) and bool(_lzj["fence_token"]))
    check("★ **反向**:lease 子对象里【没有】fence_token(不对称本身必须被钉住,"
          "否则哪天有人补齐了它,客户端的顶层兜底会掩盖真正的形状漂移)",
          "fence_token" not in _lzj["lease"], sorted(_lzj["lease"].keys()))

gateway.classify_caller = _cc_saved
# ★ 还回去 —— 这段用的是模块级单例,不还的话后面的断言(以及同进程里的任何东西)
#   会继续对着一个测试用的 Broker 说话。
gpu_broker.BROKER, _ml.ModelLoader = _BROKER_saved, _ML_saved
check("★★ 全局 BROKER 已还回去 —— 不还的话后面每一条断言都在对着一个测试用的 Broker 说话",
      gpu_broker.BROKER is _BROKER_saved)
check("★ 装载器类也还回去了", _ml.ModelLoader is _ML_saved)

_gi = _nodoc(gateway.gpu_intended)
check("★ 失败码不合并:四类失败在源码里各自成条",
      all(k in _gi for k in ("missing_if_generation", "missing_components",
                             "generation_conflict", "broker_unavailable")))
check("★ 不成功时【绝不回 200】(源码里显式按 code 分 409/422)",
      "409 if res.code in" in _gi and "422" in _gi)

print("\n=== 20. ★ 防复发:去注释器只许有一份 ===")
# ★★ 用户裁定(2026-08-04):**同一个陷阱踩了三次以上就要记下,防止以后再踩。**
#   「断言撞在解释性注释上」当天踩了 5 次(见 00-docs/ASSERTION-PITFALLS.md 第 1 条)。
#   它反复回来的原因之一是:每个测试文件各写一份去注释的正则,
#   于是每次都要在新文件里重新想起这件事。⇒ 收进 assert_helpers,并用反向全表钉住。
import pathlib as _pl

_HERE_T = _pl.Path(__file__).resolve().parent
_own = []
for _f in sorted(_HERE_T.glob("test_*.py")):
    _t = _f.read_text(encoding="utf-8")
    if "(?:.|" in _t and "assert_helpers" not in _t:
        _own.append(_f.name)
check("★★ 没有测试文件自己再写一份去注释器(要用 assert_helpers.code_only)",
      _own == [], f"自带一份的:{_own} —— 收进 assert_helpers,别各写各的")
check("assert_helpers 里三个工具都在",
      all(hasattr(assert_helpers, n) for n in ("code_only", "lock_bodies", "assignments_to")))
_DOC_P = _HERE_T.parents[1] / "00-docs" / "ASSERTION-PITFALLS.md"
check("★ 文档在(记下来才算防复发,光改代码不算)", _DOC_P.exists())
_doc = _DOC_P.read_text(encoding="utf-8") if _DOC_P.exists() else ""
# ★★ 判据盯【形状】不盯数字:次数本来就会涨(5 → 7 就红过一次,而那不是缺陷,是记录在更新)。
#   把具体数字写进断言 = 每记一次新实例都要改断言,而"改断言让它绿"正是本项目最该避免的动作。
# ★ 允许 markdown 强调:2026-08-07 有人把第 7 条写成「已踩 **8 次**」,
#   而提取器只认裸数字 ⇒ 当场少数一条、判红,**而文档是对的**。
#   这与本条上方那句「改判据不是改判词」是同一件事,又发生了一次。
_counted = re.findall(r"—— 已踩 \*{0,2}(\d+) \*{0,2}次", _doc)
# ★ 2026-08-05:放宽成允许 `## 3b.` 这种子编号 —— 审计新增的两条挂在第 3 条底下
#   (都是"守卫看起来在守、其实没盖住"的同族)。
#   ★★ 这是**改判据**不是**改断言的判词**:判词要的是"每条都标了次数",
#     而我新加的两条**确实都标了**;是提取器只认纯数字标题,漏掉了它们。
#     —— 这本身就是本文件第 4 条那个坑(判据与判词不一致),
#     所以下面紧跟着一条元断言,钉住"提取器确实数到了条目"。
_headings = re.findall(r"^## \d+[a-z]?\.", _doc, re.M)
check("★★ 每一条都标了【已踩几次】(次数是判断要不要装护栏的依据,不是修辞)",
      len(_counted) == len(_headings) and len(_headings) >= 5,
      f"{len(_counted)} 条标了次数 / 共 {len(_headings)} 条")
check("★ 至少有一条是重复踩到 3 次以上的(那正是 D85 的收录门槛)",
      any(int(n) >= 3 for n in _counted), _counted)
# ★ 元断言:提取器没有静默失灵。上面那条比的是两个数**相等** ——
#   而 0 == 0 也是相等。正则一旦写坏,它会安安静静地全绿。
check("★★ 元断言:标题提取器确实数到了条目(0 == 0 也是相等,那是零断言)",
      len(_headings) >= 8, f"只数到 {len(_headings)} 个标题")
check("★ 文档给每条写了护栏(没有护栏的条目等于没记)",
      _doc.count("护栏") >= 3, f"只有 {_doc.count('护栏')} 处")
check("★ 文档写明了两种【不许的修法】(删断言 / 改注释迁就测试)",
      "把断言删掉" in _doc and "迁就测试" in _doc)

#  ── 中央三文档串行闸(pre-commit ④)的反向全表 · D111 ────────────────────
#  ★ 为什么要这条:那张名单写在 shell 里,而**规矩写在 D111 里** ——
#    两处分家不会报错、不会告警,闸照样退出 0。多一份 = 有人被误拦,
#    少一份 = 有人静默越界。⇒ 用**反向全表**钉住:集合相等,不是"包含"。
#  ★★ 判据是**从钩子源码里解出来的集合**,不是我这里重抄一份手写名单 ——
#    手写名单只能当期望值,不能当遍历源(第 3b 条坑)。
_HOOK_P = _HERE_T.parents[1] / ".githooks" / "pre-commit"
check("★ pre-commit 钩子在(不在的话下面几条会【静默不跑】)", _HOOK_P.exists())
_hook = _HOOK_P.read_text(encoding="utf-8") if _HOOK_P.exists() else ""
# 从第 ④ 段那条 case 分支里把被盯的路径解出来:形如 `00-docs/X.md|00-docs/Y.md|...)`
_m = re.search(r"^\s*(00-docs/[^)]*?)\)\s*$", _hook, re.M)
_watched = set(_m.group(1).split("|")) if _m else set()
_EXPECT_CENTRAL = {
    "00-docs/DECISIONS.md",
    "00-docs/PROJECT_PLAN_v3.0.md",
    "00-docs/STATE.md",
}
check("★★★ 串行闸盯的正好是【中央三文档】—— 反向全表,多一份少一份都红",
      _watched == _EXPECT_CENTRAL,
      f"钩子里解出来的是 {sorted(_watched)},期望 {sorted(_EXPECT_CENTRAL)}")
# ★ 元断言:提取器没有静默失灵。空集 == 空集 也是相等 —— 那是零断言(第 4 条坑)。
check("★★ 元断言:提取器确实从钩子里解出了东西(空集也算相等,那是零断言)",
      len(_watched) >= 3, f"只解出 {len(_watched)} 条")
# ★★ 反过来钉:这两份**必须不在**闸里 —— D111 特意把它们改归共享文件类,
#   因为「学到教训的那条车道最有资格写它」。哪天有人顺手把它们加回名单,这条会红。
check("★★ ASSERTION-PITFALLS 不在串行闸里(D111:它归共享文件类,车道自己写)",
      "00-docs/ASSERTION-PITFALLS.md" not in _watched)
check("★★ worklog 不在串行闸里(D111 同上;协调层曾把它误称为中央四文档之一)",
      not any("worklog" in w for w in _watched))
# ★ 判据本身要说得出它拦的是什么:失败信息里必须给出可操作的出路,
#   否则人只会去找 --no-verify(本仓已被这个模式咬过一次,见 D24)。
check("★ 闸的失败信息给了出路(写进自己的决议包)而不只是拒绝",
      "decision-packets" in _hook and "LOCALAI_ALLOW_CENTRAL_DOCS" in _hook)
check("★★ 闸用【分支名】判而不是 worktree,并且 detached 落在【拦住】那边",
      "symbolic-ref" in _hook and "detached" in _hook)


# ══════════════════════════════════════════════════════════════════════
#  审计 2026-08-05 · loader_absent 必须说出【是哪一种】
#
#  抓到的问题:装载器接不上时,网关的 except 是个光秃秃的 `pass`,
#  而 loader_absent 的消息写着「装载器尚未实现(P5)」—— 两处都是假话
#  (S14 就实现了;P5 是语音 v1)。于是一个**接线断了**的生产故障,
#  在运维眼里长得和"这个阶段本来就没做"一模一样。
#
#  ★★★ 这不是文案问题。拒绝是对的,**把拒绝的理由丢掉**才是缺陷:
#     一个失败码若把两种成因说成同一句话,它就不再是诊断,只是个借口。
# ══════════════════════════════════════════════════════════════════════
print("\n=== 审计:loader_absent 的两种成因必须长得不一样 ===")


class _BoomLoader:
    """构造就抛 —— 模拟生产里唯一会发生的那种"接不上"(配置缺字段之类)。"""

    def __init__(self):
        raise RuntimeError("配置里少了 model_rel")


async def _t_loader_absent():
    # ① 有意没接(测试里的常态)
    b1 = _mkbroker(free=64.0, loader=None)
    r1 = await b1.apply_intended([_small])
    check("① 没接装载器 ⇒ loader_absent", r1.code == "loader_absent", r1.code)
    m1 = r1.message
    check("★ 不再声称「尚未实现」(装载器 S14 就落地了)", "尚未实现" not in m1, m1[:70])
    check("★ 不再把它安给 P5(P5 是语音 v1 —— 名字错了等于把它从清单里摘出去)",
          "P5" not in m1, m1[:70])
    check("① 说清是**有意没接**", "有意不接" in m1, m1[:70])
    s1 = b1.snapshot()
    check("① 快照如实说没接", s1.loader_present is False)
    check("★ 且 loader_error 为空 —— 「没试过」不等于「试了失败了」", s1.loader_error is None)

    # ② 接线失败(生产里那种)
    b2 = _mkbroker(free=64.0, loader=None)
    try:
        b2.attach_loader(_BoomLoader())
    except Exception as e:                                   # noqa: BLE001
        b2.note_loader_unavailable(f"{type(e).__name__}: {e}")
    r2 = await b2.apply_intended([_small])
    m2 = r2.message
    check("② 接线失败也落 loader_absent(失败关闭没变)", r2.code == "loader_absent", r2.code)
    check("★★ ② 的消息里**带着原因**(否则查不下去)", "model_rel" in m2, m2[:90])
    s2 = b2.snapshot()
    check("② 快照的 loader_error 带原因", "model_rel" in (s2.loader_error or ""), s2.loader_error)
    check("② loader_present 仍是 False(接线失败 ≠ 接上了)", s2.loader_present is False)

    # ★★★ 核心判据:两种成因**不许长成同一句话**。
    #   合并了就等于把一个生产故障伪装成预期行为 —— 这条红了不要去改断言。
    check("★★★ 两种成因的消息不同", m1 != m2, f"都是:{m1[:60]}")

    # ③ 接上之后:两个字段都要跟着翻面(★ 反过来钉,防止字段写死成 False)
    b3 = _mkbroker(free=64.0, loader=_FakeLoader())
    s3 = b3.snapshot()
    check("★ ③ 接上了 ⇒ loader_present 翻成 True(字段不是写死的)", s3.loader_present is True)
    check("③ 接上了 ⇒ loader_error 清空", s3.loader_error is None)


asyncio.run(_t_loader_absent())

# ── 结构:网关那个 except 不许再是光秃秃的 pass ──
_GW = (_HERE_T / "gateway.py").read_text(encoding="utf-8")
_gw_code = assert_helpers.code_only(_GW)
_m = re.search(r"attach_loader\(model_loader\.ModelLoader\(\)\)(.{0,400})", _gw_code, re.S)
check("★★ 网关确实在启动时接装载器", _m is not None)
if _m:
    _tail = _m.group(1)
    check("★★★ 接不上时**记下原因**(原来是 `except: pass`,原因一个字都没留下)",
          "note_loader_unavailable" in _tail, _tail[:120])
    check("★ 且留了一条运维看得见的记录", "log_upstream_problem" in _tail, _tail[:120])
# ★ 反向:全仓不得再出现那句过期文案(注释里引用旧文的除外 —— code_only 已去掉注释)
# ★★ 针拼出来,**不写成字面量** —— 第一版写死了,于是这条断言绊在**自己的字符串**上:
#    code_only 去注释但保留字符串字面量,断言的消息里那句话就成了它自己要找的东西。
#    (同款形状已踩多次,见 00-docs/ASSERTION-PITFALLS.md「断言绊在解释性文本上」。)
#    ★ 换成"跳过本文件"也能绿,但那是 fail-open:守卫从此不查自己。拼针才两全。
_needle = "装载器" + "尚未实现"
_stale_hits = []
for _f in sorted(_HERE_T.glob("*.py")):
    if _needle in assert_helpers.code_only(_f.read_text(encoding="utf-8")):
        _stale_hits.append(_f.name)
check(f"★★ 代码里不再有那句过期文案(它 S14 起就是假话)", _stale_hits == [], _stale_hits)
# ★ 反过来钉一次:针本身必须还能匹配到东西,否则上面那条是【零断言】——
#   拼错一个字它就永远绿,而"永远绿"正是这套项目最该怕的形状。
check("★ 针没拼错(拿一段确定含它的文本验一次)",
      _needle in f"旧文案:{_needle}(S14 前的说法)")


# ══════════════════════════════════════════════════════════════════════
#  审计 A1(2026-08-05)· 「推送非轮询」原来只做了一半
#
#  7 处 `_generation += 1` **没有一处在采样路径上** ⇒ 别的程序吃掉 4 GiB 显存时
#  状态没变、世代号不涨 ⇒ 推送流一帧都不发;而客户端的心跳照样把"数据新鲜"喂成真。
#  数字纹丝不动,**且看不出来它是旧的** —— 这正是本项目的签名失败模式。
# ══════════════════════════════════════════════════════════════════════
print("\n=== 审计 A1:显存变化必须推,而且不许动世代号 ===")

_a1 = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
_g0, _r0 = _a1.snapshot().generation, _a1.stream_rev
_a1._free = 10.0
_a1._note_free_for_push()
check("★ 第一次拿到读数就推一帧", _a1.stream_rev > _r0)

_r1 = _a1.stream_rev
_a1._free = 10.0 + _a1.VRAM_PUSH_DELTA_GIB / 5     # 噪声量级
_a1._note_free_for_push()
check("★★ 噪声级波动**不推** —— 每秒一帧的话推送就退化成轮询了",
      _a1.stream_rev == _r1, f"rev 从 {_r1} 涨到 {_a1.stream_rev}")

_a1._free = 10.0 + _a1.VRAM_PUSH_DELTA_GIB * 1.2   # 真事件
_a1._note_free_for_push()
check("★★★ 越过阈值必须推(开一局游戏吃掉几 GiB 就是这一条)", _a1.stream_rev > _r1)

# ★★ 累计判据:缓慢爬升也要抓得到。用"相邻差"的话,每次涨一点点会一帧都不发。
_r2 = _a1.stream_rev
_base = _a1._free
for _k in range(1, 4):
    _a1._free = _base + _a1.VRAM_PUSH_DELTA_GIB * 0.4 * _k
    _a1._note_free_for_push()
check("★★★ 缓慢爬升(每次 0.4 阈值)累计够了也要推 —— 判据比的是"
      "「客户端手上那个数偏了多少」,不是「这一秒动了多少」",
      _a1.stream_rev > _r2, f"rev {_r2} -> {_a1.stream_rev}")

# ★★★ 最要紧的一条:显存推送**不许动世代号**。
#   世代号是事务的乐观锁(if_generation);被显存波动带着涨的话,
#   用户"点确定"会一直撞 409 —— 修好一个 bug 造出另一个。
check("★★★ 显存推送期间世代号一动不动(它是事务的乐观锁,不是帧计数器)",
      _a1.snapshot().generation == _g0,
      f"世代号从 {_g0} 变成了 {_a1.snapshot().generation}")

# ★ 反过来:状态变更当然也要发帧
_r3 = _a1.stream_rev
asyncio.run(_a1._set_committed([_small]))
check("★ 状态变更也要发帧(两个计数器一起涨)", _a1.stream_rev > _r3)
check("★ 且这一次世代号**确实**涨了(它管的就是状态变更)",
      _a1.snapshot().generation > _g0)

# ── 阈值必须是实测出来的,不是拍的 ──
_thr = gpu_broker.Broker.VRAM_PUSH_DELTA_GIB
check("★★ 阈值高于本机实测的桌面漂移(30 秒实测 0.154 GiB,相邻差最大 0.130)",
      _thr > 0.154, f"阈值 {_thr} 压不住实测噪声 —— 会退化成每秒一帧")
check("★★ 也要远低于任何有意义的事件(最小的组件 speech.lite 是 2.07 GiB)",
      _thr < 2.0, f"阈值 {_thr} 太大,真事件会被压住")
_src_b = assert_helpers.code_only(gpu_broker.Broker._note_free_for_push)
check("★ 用的是【累计差】(与上次推出去的比),不是相邻差", "_pushed_free" in _src_b)

# ── 网关那一头:等的必须是推送修订号,心跳必须带数据 ──
_ge = assert_helpers.code_only(gateway.gpu_events)
check("★★★ SSE 等的是 stream_rev,不是 generation —— "
      "只看 generation 的话显存变化永远等不来一帧", "stream_rev" in _ge)
check("★★★ 心跳**带数据**(keepalive + 完整快照)—— 裸心跳会去喂客户端的『数据新鲜』判断,"
      "而它一个数字都没带", "keepalive" in _ge and "heartbeat" not in _ge)


# ══════════════════════════════════════════════════════════════════════
#  P4-S16b · 按需授权(permitted_on_demand)接线
#
#  ★★ 此前它**永远是空的**:apply_intended 有形参,而端点从不传、客户端从不发。
#     于是 I3 退化成 actual ⊆ committed,而方案书给按需装载留的那条合法车道
#     从来没有过成员。
#  ★★★ 用户裁定(2026-08-06):「不做自动触发」的主语**收窄到只管 committed**;
#     permitted_on_demand 里的成员允许被自动装卸 —— 这个字段就是那份**授权**本身。
# ══════════════════════════════════════════════════════════════════════
print("\n=== P4-S16b:按需授权必须过准入白名单,且省略 ≠ 清空 ===")


async def _t_permitted():
    b = _mkbroker(free=64.0, loader=_FakeLoader())
    # ① 授权一个已登记的组件 —— 应当收下
    r1 = await b.apply_intended([], permitted=[_small])
    check("★ 授权已登记的组件 → 收下", r1.ok, f"{r1.code} {r1.message[:60]}")
    check("★ 快照里能看到它", list(b.snapshot().permitted_on_demand) == [_small],
          list(b.snapshot().permitted_on_demand))

    # ② ★★★ 未登记的 id 必须被拒 —— 它是 I3 允许集的一半,撑大它等于把不变式关掉
    b2 = _mkbroker(free=64.0, loader=_FakeLoader())
    r2 = await b2.apply_intended([], permitted=["llm.根本没这个东西"])
    check("★★★ 授权未登记的组件 → 拒(fail-closed)", not r2.ok, r2.code)
    check("★ 归因到准入闸,并点名是哪个", r2.code == "gate_admission"
          and "根本没这个东西" in r2.message, f"{r2.code} {r2.message[:70]}")
    check("★★ 被拒时**状态机没被搅过** —— 参数不合法不该先把状态改一遍",
          b2.snapshot().state == gpu_broker.STATE_READY, b2.snapshot().state)

    # ③ ★★ 省略 ≠ 清空:一次普通变更不得静默清掉用户的按需授权
    b3 = _mkbroker(free=64.0, loader=_FakeLoader())
    await b3.apply_intended([], permitted=[_small])
    await b3.apply_intended([_small])                    # 不传 permitted
    check("★★★ 不传 permitted ⇒ 授权**保持不变**(否则每次普通变更都会静默清空它)",
          list(b3.snapshot().permitted_on_demand) == [_small],
          list(b3.snapshot().permitted_on_demand))
    # ④ 显式传空数组 = 明确撤销全部授权
    await b3.apply_intended([_small], permitted=[])
    check("★ 显式传 [] ⇒ 撤销全部授权(那是一次明确的意图)",
          list(b3.snapshot().permitted_on_demand) == [])


asyncio.run(_t_permitted())

# ── 网关那一头:端点必须真的把它传下去 ──
_gi = assert_helpers.code_only(gateway.gpu_intended)
check("★★★ 端点必须把 permitted 传给 Broker —— 形参在了三个月,端点从来没传过",
      "permitted=permitted" in _gi or "permitted = permitted" in _gi)
check("★★ 省略与空数组必须分开(None vs [])—— 合并的话普通变更会清空授权",
      "is not None" in _gi and "permitted_on_demand" in _gi)


# ══════════════════════════════════════════════════════════════════════
#  2026-08-06 审计 B5 · lease_count 是全模块唯一**不过滤过期**的租约读数
#
#  它正是「idle 可不可信」的开关(`idle_is_meaningful = lease_count > 0`),
#  而它坏在 fail-open 那一边:把"没人报告过在用"说成"有人在用"。
#  复现只要三步:发一份 ttl=0.05s 的租约 → 等 0.2 秒 → 取快照。
# ══════════════════════════════════════════════════════════════════════
print("\n=== 审计 B5:lease_count 必须与 leases 读同一份(已过滤过期)===")


async def _t_lease_count():
    b = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
    b._free, b._sampled_at = 8.0, 0.0
    await b.grant("client_session", "PC-A", [], ttl_s=0.05)
    await asyncio.sleep(0.2)
    return b.snapshot().to_json()


_j5 = asyncio.run(_t_lease_count())
check("★★★ 过期之后 lease_count 归零(改动前实测:leases=[] 而 lease_count=1)",
      _j5["lease_count"] == 0, f'lease_count={_j5["lease_count"]}')
check("★★ 且与 leases 长度**永远**一致 —— 两个数从同一份已过滤列表来",
      _j5["lease_count"] == len(_j5["leases"]),
      f'{_j5["lease_count"]} vs {len(_j5["leases"])}')
check("★★★ 于是 idle_is_meaningful 也跟着变假 —— 它是「能不能据此卸载」的开关,"
      "坏在 fail-open 那一边比坏在 fail-closed 更危险",
      _j5["idle_is_meaningful"] is False, _j5["idle_is_meaningful"])
check("★ 文案也如实说了「未过期」这三个字(不然读的人不知道这个数过不过滤)",
      "未过期" in _j5["idle_note"], _j5["idle_note"][:50])
_snap_src = assert_helpers.code_only(gpu_broker.Broker.snapshot)
check("★★ 反向:snapshot 里不得再出现 len(self._leases) —— 那是这条 bug 的原形",
      "len(self._leases)" not in _snap_src.replace(" ", ""), _snap_src[:200])
check("★ 而它确实数了那份已过滤的列表(判据不是空转)",
      "_live" in _snap_src)


# ══════════════════════════════════════════════════════════════════════
#  2026-08-06 审计 B1 / B2 / B3 · GPU 面的身份
#
#  ★★★ B1 与 B3 **必须同一提交落地**,理由不是洁癖:
#    今天 GPU 面用客户端自报的 MachineName、同步面用成员表 device_id,
#    **各自自洽所以不炸**;而 `/v1/session/end` 恰好横跨两者
#    (拿自报 device 逐条比 GPU 面的 holder)——
#    只改一边,那条路径当场失配 ⇒ **退出不再释放任何租约**,而它吞异常。
# ══════════════════════════════════════════════════════════════════════
print("\n=== 审计 B1/B3:设备身份只有一套,且只来自服务端 ===")

_pd = assert_helpers.code_only(gateway.principal_device)
check("★★★ 唯一解析口 principal_device 存在,且身份来自证书指纹经成员表反查",
      "resolve_lan_principal" in _pd and "x-localai-cert-sha256" in _pd)
check("★★ 它**不读** body —— 主体只来自成员表,自报一律忽略",
      "body" not in _pd and "json()" not in _pd)
check("★ 解析不出身份落 unknown(fail-closed),不给一个像样的名字冒充别人",
      "UNKNOWN_DEVICE" in _pd)
_sd = assert_helpers.code_only(gateway._sync_device)
check("★★★ B3 合一:同步面**不再有自己的实现**,只转发给 principal_device",
      "principal_device" in _sd and "resolve_lan_principal" not in _sd, _sd)
check("★ 而且它真的只剩一行转发(不是又抄了一份逻辑)",
      _sd.count("return") == 1, _sd)

# ── 反向全表:GPU 面**任何**处理函数都不许再从 body 里取 holder/device 当身份 ──
#   ★ 这条盯的是**全部** GPU 处理函数,不是我记得的那三处 —— 漏掉的恰恰是最危险的那个。
_ident_fns = {getattr(r, "endpoint").__name__ for r in gateway.app.routes
              if getattr(r, "path", "") in ("/v1/gpu/lease", "/v1/gpu/lease/renew",
                                            "/v1/gpu/intended", "/v1/gpu/intent",
                                            "/v1/session/end")
              and hasattr(r, "endpoint")}
check("★ 元断言:确实数到了五条要查的路由(判据不是空转)",
      len(_ident_fns) == 5, sorted(_ident_fns))
for _n in sorted(_ident_fns):
    _s = assert_helpers.code_only(getattr(gateway, _n))
    check(f"{_n}:身份走 principal_device", "principal_device" in _s, _s[:150])
    check(f"★★ {_n}:不再拿 body 里的 holder 当身份"
          f"(它是限流桶的 key,自报 = 每换个名字一个新桶)",
          'get("holder")' not in _s.replace(" ", ""), _s[:200])

print("\n=== 审计 B1/B2 端到端:自报的名字既进不了账本,也点不动别人的租约 ===")

_LAN_H = {"x-localai-cert-sha256": "aa"}
#: 当前这次请求被解析成哪台设备 —— 用可变盒子,便于在同一条连接里换身份。
_AS = {"device": "PC-A"}


class _Isolated:
    """把网关钉在一个**注入的** Broker + **注入的**装载器上再开 TestClient。

    ★★ 不这么做的话,`@app.on_event("startup")` 会:① 接上一个**真装载器**
      (把注入的 _FakeLoader 顶掉),② `adopt_running()` **认领正在跑的 llama-server**
      —— 于是测试会去动真实系统的状态,而断言变红的理由跟它自称在测的东西无关。
      与本文件 :1160 那段同一条理由,只是那里是另一组用例。
    """

    def __init__(self, loader=None, free=64.0, tier="lan-device"):
        self.loader = loader or _FakeLoader()
        self.free, self.tier = free, tier

    def __enter__(self):
        self._saved = (gpu_broker.BROKER, _ml.ModelLoader,
                       gateway.classify_caller, gateway.resolve_lan_principal)
        self.broker = _mkbroker(free=self.free, loader=self.loader)
        gpu_broker.BROKER = self.broker
        _ml.ModelLoader = lambda _l=self.loader: _l      # startup 接的还是这一个
        if self.tier == "lan-device":
            gateway.classify_caller = lambda r: "lan-edge"
            gateway.resolve_lan_principal = lambda fp: {"tier": "lan-device",
                                                        "device_id": _AS["device"]}
        else:
            gateway.classify_caller = lambda r, t=self.tier: t
            gateway.resolve_lan_principal = lambda fp: None
        gpu_policy.reset_quota()
        self._tc = _TC(gateway.app, client=("127.0.0.1", 5555))
        return self._tc.__enter__()

    def __exit__(self, *a):
        try:
            self._tc.__exit__(*a)
        finally:
            (gpu_broker.BROKER, _ml.ModelLoader,
             gateway.classify_caller, gateway.resolve_lan_principal) = self._saved
            gpu_policy.reset_quota()


def _gen(c, h=None):
    return c.get("/v1/gpu/snapshot", headers=h or _LAN_H).json()["generation"]


_AS["device"] = "PC-A"
with _Isolated() as _c:
    _r = _c.post("/v1/gpu/lease", headers=_LAN_H,
                 json={"if_generation": _gen(_c), "kind": "client_session",
                       "holder": "我是主机(其实不是)", "components": [], "ttl_s": 60})
    check("租约发得出来", _r.status_code == 200, _r.text[:140])
    _granted = _r.json().get("lease", {}).get("holder")
    check("★★★ B1:账本里记的是**服务端解出来的** PC-A,不是自报的那个名字 —— "
          "它会被印进「正在跑:xxx」对话框,自报等于让占用者的名字由被中断方自己填",
          _granted == "PC-A", f"实得 {_granted!r}")

    # ── B1 额度维:换名字不再是换桶 ──
    # ★ 2026-08-07(D?):原来这条打 /v1/gpu/intended。副机已经**没有** change_resident
    #   (四集合表:intended 只有主机变更面能写)⇒ 那条路今天恒 403,测不到额度维。
    #   ⇒ 改打 **/v1/gpu/lease**,即副机今天还有的那个桶。
    #   ★★ 承重的性质一个字没变:**桶的 key 必须来自服务端解析出来的设备**,
    #     而不是请求体里那个自报的 holder —— 否则每换一个名字就是一个新桶,
    #     额度维形同虚设(改动前实测 25/25 全过)。
    gpu_policy.reset_quota()
    _cap = gpu_policy.TIER_CAPS["lan-device"].leases_per_min
    _codes = []
    for _i in range(_cap + 5):
        _codes.append(_c.post("/v1/gpu/lease", headers=_LAN_H,
                              json={"if_generation": _gen(_c), "kind": "client_session",
                                    "components": [], "ttl_s": 60,
                                    "holder": f"PC-A-{_i}"}).status_code)
    check(f"★★★ B1:每次换一个自报名字打 {_cap + 5} 次,仍然会撞额度 —— "
          f"桶的 key 是**服务端解出来的 PC-A**,不是自报值",
          429 in _codes, f"实测状态码 {_codes}")

    # ── B2:一台副机点名释放另一台的租约 ──
    gpu_policy.reset_quota()
    _AS["device"] = "PC-VICTIM"                       # 受害者自己去申请两份租约
    for _ in range(2):
        _c.post("/v1/gpu/lease", headers=_LAN_H,
                json={"if_generation": _gen(_c), "kind": "client_session",
                      "components": [], "ttl_s": 60})
    _victim_n = len([l for l in gpu_broker.BROKER._leases.values() if l.holder == "PC-VICTIM"])
    check("★ 前提成立:受害者手上确实有租约(判据不是在空集合上测的)", _victim_n == 2, _victim_n)
    _AS["device"] = "PC-A"                            # 换回攻击者
    gpu_policy.reset_quota()
    _own_n = len([l for l in gpu_broker.BROKER._leases.values() if l.holder == "PC-A"])
    _r2 = _c.post("/v1/session/end", headers=_LAN_H,
                  json={"device": "PC-VICTIM", "reason": "我是 PC-A,点名释放 PC-VICTIM"})
    check("★★★ B2:受害者的两份租约**原封不动**(改动前实测:被点名释放,released=2)",
          len([l for l in gpu_broker.BROKER._leases.values()
               if l.holder == "PC-VICTIM"]) == 2,
          f'released={_r2.json().get("released_leases")}')
    check("★★ 而 released 的数正好是**它自己**那几份 —— 不是【报了个数但放的是别人的】",
          _r2.status_code == 200 and _r2.json()["released_leases"] == _own_n,
          f'{_r2.json().get("released_leases")} vs 自己有 {_own_n} 份')
    check("★ 响应如实说了【你报的那个名字被忽略了】—— 静默丢弃会让旧客户端"
          "拿着一个 200 以为租约放掉了",
          _r2.json().get("ignored_device") == "PC-VICTIM", _r2.json())
    check("★ 而 device 回带的是**服务端认定**的那台", _r2.json()["device"] == "PC-A")

    # ── B2 反面:释放自己的照常有效(修的是冒名,不是把功能关掉)──
    _c.post("/v1/gpu/lease", headers=_LAN_H,
            json={"if_generation": _gen(_c), "kind": "client_session",
                  "components": [], "ttl_s": 60})
    _r3 = _c.post("/v1/session/end", headers=_LAN_H, json={"reason": "quit"})
    check("★ 释放**自己**的租约照常有效", _r3.json()["released_leases"] >= 1, _r3.text[:140])


# ══════════════════════════════════════════════════════════════════════
#  2026-08-06 审计 B6 + D90 裁定① · 「按需授权只有主机变更面能写」
# ══════════════════════════════════════════════════════════════════════
print("\n=== 审计 B6:写按需授权是【另一个动作】,只有主机档有 ===")
check("★★★ permit_on_demand 已登记为独立动作(不是 change_resident 的一个字段)",
      "permit_on_demand" in gpu_policy.ACTIONS, gpu_policy.ACTIONS)
check("★★★ 只有 trusted-local 有它 —— 副机看不到主机屏幕,不该替机主签这个字",
      {t for t, c in gpu_policy.TIER_CAPS.items() if "permit_on_demand" in c.actions}
      == {"trusted-local"},
      sorted(t for t, c in gpu_policy.TIER_CAPS.items() if "permit_on_demand" in c.actions))
_gi2 = assert_helpers.code_only(gateway.gpu_intended)
check("★★ 端点真的判了这一维(而不是只在注释里写着「只有主机能写」)",
      '"permit_on_demand"' in _gi2)
check("★ 且只在**带了这个字段**时才判 —— 普通变更不该被一道不相干的闸拦住",
      "is not None" in _gi2)

_AS["device"] = "PC-A"
with _Isolated() as _c:
    _r = _c.post("/v1/gpu/intended", headers=_LAN_H,
                 json={"if_generation": _gen(_c), "components": [_small],
                       "permitted_on_demand": [_small]})
    check("★★★ B6:副机写按需授权 → 403(改动前实测:授权当场写进了权威状态)",
          _r.status_code == 403, f"{_r.status_code} {_r.text[:140]}")
    # ══════════════════════════════════════════════════════════════════
    #  ★★ 2026-08-07(D?):点名的动作从 `permit_on_demand` 变成了 `change_resident`。
    #  **这不是判据松了,是拒得更早了。** `gpu_intended` 的顺序是
    #    ① read 档 → ② resolve_action(components) 再判 → ③ 带了 permitted_on_demand 才判 B6 那道闸。
    #  副机现在在 ② 就被拦住(它连普通变更都不能做),根本走不到 ③。
    #
    #  ★★★ 副作用要如实记:**B6 那道闸今天对所有档位都够不着了** ——
    #    表里只有 `trusted-local` 有 change_resident,而它同时也有 permit_on_demand。
    #    ⇒ 它现在是**纵深防御的第二层**,不是唯一那道闸。留着是对的:
    #    哪天有人加一个"能改驻留、但不能签按需授权"的新档位,它会立刻重新承重。
    #    ⇒ 判据只钉【工具】维 + 【动作被点名】,不钉具体是哪一个动作 ——
    #      钉死具体动作名,就是把"今天恰好在哪一层拒的"焊进断言里(第 9 条坑)。
    # ══════════════════════════════════════════════════════════════════
    check("★ 拦在【工具】维,并点名了动作 —— 用户据此知道这事要去主机上做",
          _r.json()["error"]["dimension"] == "tool"
          and _r.json()["error"]["action"] in ("permit_on_demand", "change_resident"),
          _r.json()["error"])
    check("★★ 授权集合**一个字节都没被改**",
          list(gpu_broker.BROKER.snapshot().permitted_on_demand) == [],
          list(gpu_broker.BROKER.snapshot().permitted_on_demand))
    # ══════════════════════════════════════════════════════════════════
    #  ★★★ 2026-08-07(D?):这一条**翻面**了。
    #
    #  它原来写的是「副机的普通变更照常过权限层(否则副机面板变只读 = 产品回退)」——
    #  而那句话与方案书四集合表(行 1550)**直接冲突**:`intended_resident_set` 的
    #  「谁能写」一栏是 **只有主机变更面**。08-06 补 B6 时只给第二行
    #  (`permitted_on_demand_set`,行 1553)装了闸,两行一模一样的规格只装了一道。
    #  ⇒ 实测确认:副机带非空 components 打一次,**回 200,真的写进去了**。
    #
    #  ★ 产品代价如实记:副机的组件勾选面板变为只读。但**不是**"副机什么都动不了" ——
    #    `lease` 档留着,D87①「意图即起」在副机上照常工作(见下一条正面断言)。
    # ══════════════════════════════════════════════════════════════════
    gpu_policy.reset_quota()
    _gen_before = _gen(_c)
    _intended_before = list(gpu_broker.BROKER.snapshot().intended)
    _r2 = _c.post("/v1/gpu/intended", headers=_LAN_H,
                  json={"if_generation": _gen_before, "components": [_small]})
    check("★★★ 副机的**普通变更**同样被拒 —— 四集合表:intended 只有主机变更面能写"
          "(改动前实测 200,驻留集合真的被副机改掉了)",
          _r2.status_code == 403, f"{_r2.status_code} {_r2.text[:140]}")
    check("★ 同样拦在【工具】维并点名动作 —— 与 permit_on_demand 那条同一个形状",
          _r2.status_code == 403
          and _r2.json()["error"]["dimension"] == "tool"
          and _r2.json()["error"]["action"] == "change_resident", _r2.text[:200])
    check("★★ 驻留集合**一个字节都没被改**(只看状态码不够:403 也可能是写完之后才拒的)",
          list(gpu_broker.BROKER.snapshot().intended) == _intended_before,
          list(gpu_broker.BROKER.snapshot().intended))
    # ★ 正面:副机**不是**什么都不能动 —— 意图即起(lease 档)照常。
    #   没有这一条,上面那三条就读成了"把副机关掉",而那不是这次的裁定。
    gpu_policy.reset_quota()
    _alias_lan = next((a for a in gateway.REGISTRY
                       if _small in gateway.components_for_alias(a)), None)
    if _alias_lan:
        _r3 = _c.post("/v1/gpu/intent", headers=_LAN_H, json={"alias": _alias_lan})
        check("★★★ 副机的「意图即起」照常过权限层 —— 变成只读的**只有**"
              "「替机主改写常驻清单」这一件,他要用的功能仍会为他起来",
              _r3.status_code not in (401, 403), f"{_r3.status_code} {_r3.text[:140]}")

with _Isolated(tier="trusted-local") as _c:
    _g = _c.get("/v1/gpu/snapshot").json()["generation"]
    _r = _c.post("/v1/gpu/intended",
                 json={"if_generation": _g, "components": [_small],
                       "permitted_on_demand": [_small]})
    check("★★ 主机侧写按需授权 → 过(闸挡的是副机,不是这件事本身)",
          _r.status_code == 200, f"{_r.status_code} {_r.text[:140]}")
    check("★ 且授权真的落进了权威状态",
          list(gpu_broker.BROKER.snapshot().permitted_on_demand) == [_small],
          list(gpu_broker.BROKER.snapshot().permitted_on_demand))


# ══════════════════════════════════════════════════════════════════════
#  P4-S16b 接动作 · 按需驻留(transient)平面
#
#  D90 裁定③(结构性强制):自动路径**不得复用 apply_intended**,必须是一条
#  **结构上够不着 committed** 的独立路径,并配**反向断言**:
#  自动路径的源码里不许出现对 `_committed` 的赋值。
# ══════════════════════════════════════════════════════════════════════
print("\n=== S16b:按需装载 / 空闲卸载 —— 结构上够不着 committed ===")

_AUTO_PATHS = ("request_on_demand", "sweep_idle_transient")
_WRITE_PAT = re.compile(r"self\._committed\s*(?:=[^=]|\.append|\.remove|\.clear|"
                        r"\.extend|\.insert|\.pop|\.sort|\.reverse)")
for _m in _AUTO_PATHS:
    _s = inspect.getsource(getattr(gpu_broker.Broker, _m))
    _hit = _WRITE_PAT.search(_s)
    check(f"★★★ {_m} 的源码里**没有任何**对 _committed 的写 —— D90 裁定③的反向断言",
          _hit is None, _hit.group(0) if _hit else "")
    check(f"★ 而它确实**读**得到 _committed —— 判据不是靠【压根没提这个名字】蒙过去的",
          "self._committed" in _s)
# ★ 判据自检:同一条正则拿 apply_intended 一试必须命中 —— 否则它只是个永远不响的探测器。
check("★★ 元断言:这条正则对**真的会写 committed** 的那条路径确实命中"
      "(不然它是个永远不响的探测器)",
      _WRITE_PAT.search(inspect.getsource(gpu_broker.Broker.apply_intended)) is not None)

# ── 字段名不复用(D90 裁定③:D24「cap」那个亏)──
_sn_keys = set(gpu_broker.BROKER.snapshot().to_json()["sets"])
check("★★ 按需那一半用 transient_ 前缀,**不复用** committed 那一半的任何名字",
      "transient_resident" in _sn_keys and "committed_resident" in _sn_keys, sorted(_sn_keys))
check("★ 五个集合齐备(四个老的 + transient)",
      _sn_keys == {"intended_resident", "committed_resident", "actual_resident",
                   "permitted_on_demand", "transient_resident"}, sorted(_sn_keys))


async def _t_on_demand():
    ld = _FakeLoader()
    b = _mkbroker(free=64.0, loader=ld)
    b._committed = []
    # ① 没授权过 → 拒,并给出"去主机上勾一次"这条下一步
    r = await b.request_on_demand(_small)
    check("★★★ 没被授权过的组件 → 拒(D90 裁定①的代价段:先授权一次才有按需可言)",
          r["code"] == gpu_broker.ON_DEMAND_NOT_PERMITTED, r)
    check("★ 而且说清了下一步是【去主机上勾一次】,不是一句【起不来】",
          "主机" in str(r["message"]) and "授权" in str(r["message"]), r["message"])
    check("★★ 被拒时 transient 平面是空的(拒绝不留半份账)",
          list(b._transient_resident) == [])

    # ② 授权之后 → 装得上,且**只进 transient 平面**
    b._permitted_on_demand = [_small]
    r2 = await b.request_on_demand(_small)
    check("★★★ 授权之后按需装载成功", r2["code"] == gpu_broker.ON_DEMAND_OK, r2)
    check("★★★ committed **一个字节都没动** —— 这是 D90 裁定①的硬边界",
          list(b._committed) == [], list(b._committed))
    check("★ 它落在 transient 平面里", list(b._transient_resident) == [_small])
    check("★ 装载器**真的**被调用了(不是只改了账本)",
          ("load", [_small]) in ld.calls, ld.calls)

    # ③ 再来一次 → ALREADY,不重复装
    _n_before = len([c for c in ld.calls if c[0] == "load"])
    r3 = await b.request_on_demand(_small)
    check("★ 重复意图 → ALREADY,不重复装载",
          r3["code"] == gpu_broker.ON_DEMAND_ALREADY
          and len([c for c in ld.calls if c[0] == "load"]) == _n_before, r3)

    # ④ I2 的作用域:actual 里多出一个按需成员,**不算** I2 违反
    b._actual_cache = await ld.running()          # ★ 让 actual 变成独立观测
    _i2 = [i for i in b.check_invariants() if i.invariant == "I2"][0]
    check("★★★ I2 作用域收窄到常驻面:按需成员在跑 ⇒ I2 仍然成立",
          _i2.holds, _i2.to_json())
    check("★ 且 detail 里**说出**了它是被排除的那一个(不是悄悄减掉)",
          _small in _i2.detail, _i2.detail)
    _i3 = [i for i in b.check_invariants() if i.invariant == "I3"][0]
    check("★ I3 照常成立(transient ⊆ permitted)", _i3.holds, _i3.to_json())

    # ⑤ ★★ 反向:一个既不在 committed 也不在 transient 的野组件,I2/I3 必须红 ——
    #    收窄的是作用域,不是把检测器关小
    _other = sorted(c for c in b.cfg.components if c != _small)[0]
    b._actual_cache = [_small, _other]
    _i2b = [i for i in b.check_invariants() if i.invariant == "I2"][0]
    _i3b = [i for i in b.check_invariants() if i.invariant == "I3"][0]
    check("★★★ 反向:野组件仍然让 I2 判红(减的只是 transient 登记过的那些)",
          not _i2b.holds, _i2b.to_json())
    check("★★ 反向:I3 也点名了它", (not _i3b.holds) and _other in _i3b.detail, _i3b.to_json())
    b._actual_cache = await ld.running()

    # ⑥ 提级为常驻之后,不能同时挂在两个平面上
    b2 = _mkbroker(free=64.0, loader=_FakeLoader())
    b2._permitted_on_demand = [_small]
    await b2.request_on_demand(_small)
    await b2.apply_intended([_small])
    check("★★ 勾进常驻之后从 transient 平面摘掉(否则 I2 会报『缺了它』而它明明在跑)",
          list(b2._transient_resident) == [] and list(b2._committed) == [_small],
          f"transient={b2._transient_resident} committed={b2._committed}")
    return b, ld


_b_od, _ld_od = asyncio.run(_t_on_demand())

print("\n=== S16b:空闲即卸 —— 三条放行条件缺一不可 ===")


async def _t_sweep():
    ld = _FakeLoader()
    b = _mkbroker(free=64.0, loader=ld)
    b._permitted_on_demand = [_small]
    await b.request_on_demand(_small)

    # ① 没空够久 → 不卸
    r1 = await b.sweep_idle_transient(idle_after_s=600.0)
    check("★★ 条件①:没空够久 ⇒ 不卸,并**说出**为什么(静默的收割与没跑过一模一样)",
          r1["unloaded"] == [] and r1["reason"] == "not_idle_enough", r1)

    # ② 空够久了,但有**点名了组件**的租约在跑 → 不卸(D90 裁定②,硬条件)
    b._transient_last_intent[_small] = time.monotonic() - 10_000
    await b.grant("agent_task", "PC-A", [_small], ttl_s=60)
    r2 = await b.sweep_idle_transient(idle_after_s=600.0)
    check("★★★ 条件②:正在跑的不动(D90 裁定②是**硬条件**,不是【提醒后仍卸】)",
          r2["unloaded"] == [] and r2["reason"] == "blocking_leases", r2)
    check("★ 且回带了是谁挡的 —— 拒绝信息要含占用者",
          r2["blocking"] and r2["blocking"][0]["kind"] == "agent_task", r2.get("blocking"))

    # ③ 租约没了 → 卸
    b._leases.clear()
    r3 = await b.sweep_idle_transient(idle_after_s=600.0)
    check("★★★ 三条都成立 ⇒ 真的卸(而且装载器真的被调用了)",
          r3["unloaded"] == [_small] and ("unload", [_small]) in ld.calls, (r3, ld.calls))
    check("★ 卸完 transient 平面清空", list(b._transient_resident) == [])

    # ④ ★★★ 条件③:常驻成员**永远**不是候选 —— 哪怕它同时被授权、且空了一万秒
    ld2 = _FakeLoader()
    b2 = _mkbroker(free=64.0, loader=ld2)
    b2._committed = [_small]
    b2._permitted_on_demand = [_small]
    b2._transient_last_intent[_small] = time.monotonic() - 10_000
    r4 = await b2.sweep_idle_transient(idle_after_s=1.0)
    check("★★★ 条件③:常驻成员碰不到 —— 候选池只从 transient 平面来",
          r4["unloaded"] == [] and list(b2._committed) == [_small], (r4, b2._committed))
    check("★ 而且装载器**一次都没被调** —— 不是【卸了又装回去】",
          ld2.calls == [], ld2.calls)

    # ⑤ 装载器缺席 → 不动账本(账面上卸了而显存里还在,比不卸更坏)
    b3 = _mkbroker(free=64.0, loader=_FakeLoader())
    b3._permitted_on_demand = [_small]
    await b3.request_on_demand(_small)
    b3._loader = None
    b3._transient_last_intent[_small] = time.monotonic() - 10_000
    r5 = await b3.sweep_idle_transient(idle_after_s=1.0)
    check("★★ 装载器缺席 ⇒ 不卸也**不清账本**(清了就是账面卸了而显存里还在)",
          r5["reason"] == "loader_absent" and list(b3._transient_resident) == [_small], r5)


asyncio.run(_t_sweep())

# ── 两个钟不可互相替代 ──
_idle_src = inspect.getsource(gpu_broker.Broker.idle_seconds.fget)
check("★★★ idle_seconds 明说了它**不是**按需卸载的放行条件(它被心跳刷新 ⇒ 恒假式)",
      "恒假式" in _idle_src)
_sw_src = assert_helpers.code_only(gpu_broker.Broker.sweep_idle_transient)
check("★★ 收割看的是 _transient_last_intent(逐组件),不是 _last_activity_at(全网)",
      "_transient_last_intent" in _sw_src and "_last_activity_at" not in _sw_src, _sw_src[:200])
# ── 不设收割线程:S16b 的收割必须搭在采样循环里,不许再起一个 task ──
_all_src = inspect.getsource(gpu_broker)
check("★★★ 接了动作之后仍然只有**一个**后台任务(收割线程 = 第二个写者)",
      _all_src.count("create_task") == 1,
      "create_task 出现次数 = " + str(_all_src.count("create_task")))
check("★ 而收割确实被采样循环调用了(判据不是空转)",
      "sweep_idle_transient()" in assert_helpers.code_only(gpu_broker.Broker._sampler_loop))
# ── D87③「显存压力即让」**有意留空**,不是没做完 ──
check("★★ admission_guard 仍然只报告降幅、不触发任何卸载 —— "
      "它是**降幅**规则(5 秒内掉 1 GiB),与 D87③ 的**水位**判据不是一回事",
      "unload" not in assert_helpers.code_only(gpu_broker.Broker.admission_guard))


# ══════════════════════════════════════════════════════════════════════
#  V8 · D87③「显存压力即让」(2026-08-07 用户裁定后落地)
#
#  用户裁定原文:「AI,让,任务暂停,并弹提示。然后在任务进度里面可以再开,
#  然后启动需要的模型,前提是显存允许的情况,不然开始按钮是不可用的。」
# ══════════════════════════════════════════════════════════════════════
print("\n=== V8 · D87③:压力让位与 D90 空闲回收是【两条独立路径】 ===")

_yield_src = assert_helpers.code_only(gpu_broker.Broker.yield_under_pressure)
_sweep_src2 = assert_helpers.code_only(gpu_broker.Broker.sweep_idle_transient)
check("★★★ 两条路径是**两个方法**,不是一个方法的两个参数 —— "
      "合并之后没人说得清某次卸载是哪条规则干的",
      hasattr(gpu_broker.Broker, "yield_under_pressure")
      and hasattr(gpu_broker.Broker, "sweep_idle_transient"))
# ★★ 判据要落在**进入条件**上,不是"这段代码提没提过某个名字"。
#   第一版写成「压力那条不许出现 _transient_last_intent」—— **当场变红**,
#   因为让位之后要把那条组件的时刻表项清掉(`pop`),那是**清理**不是**判据**。
#   ⇒ 那是 ASSERTION-PITFALLS 第 4 条(判据比想判的东西宽)。改成只问进入条件:
check("★★★ 进入条件不同:压力那条看 under_pressure(NVML 水位),"
      "空闲那条看空闲阈值 —— 两个判据互不出现在对方里",
      "under_pressure" in _yield_src and "under_pressure" not in _sweep_src2
      and "idle_after_s" in _sweep_src2 and "idle_after_s" not in _yield_src)
check("★★★ 每次自动卸载都带**是哪条规则**,而且两条的值不同 —— "
      "不带的话日志里两种卸载长得一模一样,而它们的允许范围完全不同",
      gpu_broker.UNLOAD_BY_IDLE != gpu_broker.UNLOAD_BY_PRESSURE
      and "UNLOAD_BY_PRESSURE" in _yield_src and "UNLOAD_BY_IDLE" in _sweep_src2)
# ★ 允许范围仍受 D90 约束:压力路径同样够不着 committed
_hit_y = _WRITE_PAT.search(inspect.getsource(gpu_broker.Broker.yield_under_pressure))
check("★★★ 压力路径的源码里同样**没有任何**对 _committed 的写(D90 裁定③照旧管着它)",
      _hit_y is None, _hit_y.group(0) if _hit_y else "")
#   ★ 而"读得到 committed"这条要问**挑人的那个函数**(pressure_victims),
#     不是 yield_under_pressure —— 排除常驻成员是在挑人那一步做的。
#     第一版问错了地方,红得对:判据必须落在真的做那件事的那段代码上。
_hit_pv = _WRITE_PAT.search(inspect.getsource(gpu_broker.Broker.pressure_victims))
check("★★ 挑人那段同样不写 _committed,只**读**它来排除常驻成员",
      _hit_pv is None and "self._committed" in
      inspect.getsource(gpu_broker.Broker.pressure_victims))
check("★★ 仍然只有**一个**后台任务:压力那条也搭在采样循环里,没另起 task",
      inspect.getsource(gpu_broker).count("create_task") == 1)
check("★ 而它确实被采样循环调用了(判据不是空转)",
      "yield_under_pressure()" in assert_helpers.code_only(gpu_broker.Broker._sampler_loop))


def _mk_pressure_broker(free, transient=(), committed=(), loader=None):
    b = _mkbroker(free=free, loader=loader or _FakeLoader())
    b._committed = list(committed)
    b._transient_resident = list(transient)
    b._permitted_on_demand = list(transient)
    for c in transient:
        b._transient_last_intent[c] = time.monotonic()
    return b


print("\n=== V8 · 判据:NVML free 掉到阈值以下,而且要【连续】 ===")
_bp = _mk_pressure_broker(free=0.5, transient=[_small])
check("★★★ 单次低于阈值**不算** —— 按一次波动去打断用户正在跑的任务,"
      "是这条规则最容易造成的伤害",
      not _bp.under_pressure())
for _i in range(gpu_broker.PRESSURE_CONSECUTIVE_SAMPLES):
    _bp._note_pressure_sample()
check(f"★★ 连续 {gpu_broker.PRESSURE_CONSECUTIVE_SAMPLES} 次之后才算压力态", _bp.under_pressure())
_bp._free = 8.0
_bp._note_pressure_sample()
check("★ 一次回到阈值以上就清零(压力解除是立刻的,不用等)", not _bp.under_pressure())
# ★★ 读不到 free 时:既不清零也不累加
_bp2 = _mk_pressure_broker(free=0.5, transient=[_small])
for _i in range(gpu_broker.PRESSURE_CONSECUTIVE_SAMPLES):
    _bp2._note_pressure_sample()
_bp2._free = None
_bp2._note_pressure_sample()
check("★★★ 读不到 free ⇒ **既不清零也不累加**:清零 = 一次读失败就把压力态抹掉"
      "(而读失败可能正是压力造成的);累加 = 拿【不知道】当【很紧】去打断任务",
      _bp2.under_pressure() and _bp2._pressure_low_streak
      == gpu_broker.PRESSURE_CONSECUTIVE_SAMPLES)
check("★ 阈值高于 safety_margin(要在桌面撞墙**之前**让),低于最小组件 peak(否则一让就是全让)",
      gpu_broker.PRESSURE_FREE_FLOOR_GIB > gpu_broker.BROKER.cfg.budget.safety_margin
      and gpu_broker.PRESSURE_FREE_FLOOR_GIB
      < min(gpu_broker.BROKER.cfg.peak(c) for c in gpu_broker.BROKER.cfg.components),
      gpu_broker.PRESSURE_FREE_FLOOR_GIB)

print("\n=== V8 · 让谁:按 peak 从大到小,【与 kind 无关】 ===")
_pv_src = assert_helpers.code_only(gpu_broker.Broker.pressure_victims)
check("★★★ 选择判据里**不出现任何 kind 名**(llm / speech / vlm / comfyui)—— "
      "写成 kind 白名单的话,新增一种组件会**默认落在【不让】那边**,"
      "于是压力来了让不出东西,而日志里什么都看不出来",
      not any(k in _pv_src for k in ("llm", "speech", "vlm", "comfyui")), _pv_src[:200])
# ★ 正面验:一个 speech 组件同样会被选中(不是"没写死"就完了,要真的能覆盖它)
_speech = next((c for c in gpu_broker.BROKER.cfg.components
                if str(gpu_broker.BROKER.cfg.components[c].get("kind") or "").startswith("speech")),
               None)
check("★ 前提:配置里确实有 speech 类组件(否则下面那条测的是空集)", _speech is not None, _speech)
if _speech:
    _bs = _mk_pressure_broker(free=0.5, transient=[_speech])
    check("★★★ speech 组件同样会被选中 —— V7 正在把它接成可装载组件,这条路径要能自然覆盖",
          _bs.pressure_victims(1.0) == [_speech], _bs.pressure_victims(1.0))
_big = max(gpu_broker.BROKER.cfg.components, key=gpu_broker.BROKER.cfg.peak)
_bm = _mk_pressure_broker(free=0.5, transient=[_small, _big])
check("★★ 从大到小:同样腾出 N GiB,动的组件数最少 ⇒ 被打断的任务最少",
      _bm.pressure_victims(0.1) == [_big], _bm.pressure_victims(0.1))
_bc = _mk_pressure_broker(free=0.5, transient=[], committed=[_small])
check("★★★ 常驻成员**永不入选**(D90:committed 一个字节都不许自动改)——"
      "用户裁定里的『让』指的只是按需那一层",
      _bc.pressure_victims(99.0) == [], _bc.pressure_victims(99.0))


async def _t_pressure():
    ld = _FakeLoader()
    b = _mk_pressure_broker(free=0.5, transient=[_small], loader=ld)
    ld.loaded.add(_small)
    # 还没连续够 ⇒ 不动
    r0 = await b.yield_under_pressure()
    check("★★ 没到连续次数 ⇒ 什么都不做,并**说出**为什么",
          r0["code"] == gpu_broker.YIELD_NO_PRESSURE, r0)
    for _ in range(gpu_broker.PRESSURE_CONSECUTIVE_SAMPLES):
        b._note_pressure_sample()
    # 给它一份点名了该组件的租约 —— 它正在跑
    await b.grant("agent_task", "PC-A", [_small], ttl_s=60)
    _n_leases = len(b._leases)
    r1 = await b.yield_under_pressure()
    check("★★★ 压力下**正在跑的也让**(与 D90「正在跑的不动」正相反,这是两条路径的分界)",
          r1["code"] == gpu_broker.YIELD_DONE and r1["yielded"] == [_small], r1)
    check("★★★ 而且**点名了谁被打断** —— 客户端据它把任务转成暂停(不是失败)",
          len(r1["affected_leases"]) == 1
          and r1["affected_leases"][0]["kind"] == "agent_task", r1.get("affected_leases"))
    check("★★★ 但**不撤销任何租约**:不变式 I1 只允许拒发新租约,不允许撤销已发的。"
          "让位动的是**显存**,不是别人手里的凭据",
          len(b._leases) == _n_leases, f"{_n_leases} → {len(b._leases)}")
    check("★ 装载器真的被调了(不是只改账本)", ("unload", [_small]) in ld.calls, ld.calls)
    check("★★ committed 一个字节没动", list(b._committed) == [])
    j = b.snapshot().to_json()
    check("★★★ 通知进快照 ⇒ 走 SSE 推给**所有**客户端 —— D87③ 点名要防的就是"
          "「只在主机上弹,副机那边任务凭空失败而人不知道为什么」",
          j["pressure"]["notice"] is not None
          and j["pressure"]["notice"]["unload_reason"] == gpu_broker.UNLOAD_BY_PRESSURE,
          j["pressure"]["notice"])
    check("★★ 通知里明说【已暂停,不是失败】—— 失败是终点,暂停不是",
          "暂停" in j["pressure"]["notice"]["message"]
          and "不是失败" in j["pressure"]["notice"]["message"],
          j["pressure"]["notice"]["message"])
    check("★ 诚实边界照抄 D87③ 与 §8.1:这是**策略不是保证**(WDDM 不按优先级驱逐)",
          j["pressure"]["guarantee"] is False and "不是保证" in j["pressure"]["note"])
    check("★★ 让完清零连续计数 —— 否则一次压力会把 transient 平面一口气全掏空",
          not b.under_pressure())
    # 通知过期
    b._pressure_notice_at = time.monotonic() - gpu_broker.PRESSURE_NOTICE_TTL_S - 1
    b._expire_pressure_notice()
    check("★ 通知挂满 TTL 就摘掉(不摘的话几小时前那次会一直挂在界面上)",
          b.snapshot().to_json()["pressure"]["notice"] is None)

    # 有压力但没东西可让 ⇒ **不是成功**,要说出来
    b2 = _mk_pressure_broker(free=0.5, transient=[], committed=[_small])
    for _ in range(gpu_broker.PRESSURE_CONSECUTIVE_SAMPLES):
        b2._note_pressure_sample()
    r2 = await b2.yield_under_pressure()
    check("★★★ 有压力却让不出东西 ⇒ 明确回 NOTHING_TO_YIELD(D24:失败落在 AI 侧,"
          "而【我们让不出东西】必须看得见,否则界面会显示一切正常)",
          r2["code"] == gpu_broker.YIELD_NOTHING_TO, r2)


asyncio.run(_t_pressure())


# ══════════════════════════════════════════════════════════════════════
#  ★★★ D92 硬前置 · 跨语言成对断言
#
#  A1 是一条**跨语言缝**:服务端把 lease_id 放在 body["lease"]["lease_id"],
#  客户端在**顶层**找 —— 服务端测"顶层有哪些键"、客户端测"这个形状能不能解析",
#  **各测各的,中间那条缝谁也没看**,于是租约一次都没持住过而 5 秒卡顿照样交付。
#
#  ⇒ 每一个跨进程响应契约必须有**一条成对断言**:
#     服务端钉顶层键集合 · 客户端钉「拿这个形状能解析出目标字段」;
#     并配一条**元断言**枚举所有此类契约、缺配对即判红
#     —— 照 ROUTE_TIERS 反向全表那个已被验证的形状。
# ══════════════════════════════════════════════════════════════════════
print("\n=== D92 硬前置:跨进程响应契约,两侧成对 ===")

#: 契约登记表。key = 契约号(客户端那半边必须原样出现这个字符串)。
#  ★ 新增任何跨进程响应形状 → 必须进这张表 → 元断言会去客户端源码里找同名标记。
#  ★★ 键集合一律**手写字面量**,不从 `to_json()` 反推。
#     从被测函数自己推期望值 = 拿账本跟账本比:改个键名两边一起变,
#     而"改个键名"正是这条契约存在的**全部理由**。
_SNAPSHOT_TOP_KEYS = {
    "generation", "committed", "reserved", "leases", "sets", "state", "power_on",
    "invariants", "vram", "sampled_at", "age_s", "stale", "sampler_error",
    "loader_present", "loader_error", "idle_seconds", "lease_count",
    "idle_is_meaningful", "idle_note", "idle_note_transient",
    "transient_idle_s", "transient_idle_threshold_s", "transient_note",
    # ★ 2026-08-08(V16):新增三段。**同样是被门禁逼出来的** —— 加上它们那一刻
    #   V5 钉的三条断言(gpu.snapshot 顶层键 · SSE 帧载荷 · 409 里那份完整快照)
    #   一起变红,理由写着「多 [...]」。这就是成对断言在挡自己人的契约漂移。
    #   · residency_truth —— 「Broker 说已卸载 vs 进程真的没了」那条**能为假**的判据;
    #   · reconcile_log / reconcile_note —— 进/出 RECONCILING 的可查记录;
    #   · footprint —— 装载时**实测**的显存足迹(卸载的回收判据用它,不用 peak)。
    "residency_truth", "reconcile_log", "reconcile_note", "footprint",
    # ★ 2026-08-07(V8 · D87③):新增 pressure 段。**这一行是被门禁逼出来的** ——
    #   加上 `pressure` 那一刻,V5 钉的三条断言(gpu.snapshot 顶层键 · SSE 帧载荷 ·
    #   409 里那份完整快照)**同时变红**,理由都写着「多 ['pressure']」。
    #   ⇒ 成对断言在这里第一次挡住了**自己人**的契约漂移,不是只挡"别人改坏了"。
    "pressure",
}

CROSS_PROCESS_CONTRACTS = {
    "CONTRACT:gpu.lease.grant":  ("POST /v1/gpu/lease 200",
                                  {"status", "lease", "fence_token", "generation"}),
    "CONTRACT:gpu.intent":       ("POST /v1/gpu/intent 200",
                                  {"status", "intent", "lease", "fence_token", "generation"}),
    "CONTRACT:gpu.lease.renew":  ("POST /v1/gpu/lease/renew 200", {"result", "snapshot"}),
    "CONTRACT:session.end":      ("POST /v1/session/end 200",
                                  {"status", "released_leases", "device", "reason"}),
    "CONTRACT:gpu.intended.blocking": ("POST /v1/gpu/intended 409 的 result.blocking[i]",
                                       {"lease_id", "kind", "holder", "components",
                                        "granted_at", "expires_at", "held_s",
                                        "evictable", "blocking", "exclusive"}),
    # ── 2026-08-06 夜 · V5:还清 [GPU/租约切片] 那 4 条欠债 ──────────────
    "CONTRACT:gpu.snapshot":     ("GET /v1/gpu/snapshot 200", set(_SNAPSHOT_TOP_KEYS)),
    # ★★ SSE 的契约是**每一帧**的顶层键集合,不是整个响应体 ——
    #    响应体是一条**永不结束**的流,它根本没有"顶层键集合"这种东西。
    #    钉成响应体的话,判据要么恒真、要么根本写不出来。见下方那一组逐帧断言。
    "CONTRACT:gpu.events.frame": ("GET /v1/gpu/events 的**每一帧** data 载荷",
                                  set(_SNAPSHOT_TOP_KEYS)),
    "CONTRACT:gpu.components":   ("GET /v1/gpu/components 200",
                                  {"generation", "components", "aliases_by_component",
                                   "budget", "state", "stale", "sampler_error"}),
    # ★ 成功与失败**两个形状**都要钉:失败那个多一个 error,而 snapshot 必须还在 ——
    #   客户端读不出 snapshot 就无从重试(见下方 409 那一组)。
    "CONTRACT:gpu.intended":     ("POST /v1/gpu/intended 200", {"result", "snapshot"}),
}

#: 意图那条契约要一个**真的有别名指向它**的组件 —— `_small`(speech.lite)今天没有别名,
#  拿它去打 /v1/gpu/intent 会走进 no_gpu_needed 那条分支,契约形状就不是要测的那个。
#  ★ 这不是"挑一个能过的" —— 是判据要落在**它真正描述的那条路径**上。
_small_aliased = min((c for c in gpu_broker.BROKER.cfg.components
                      if gateway.aliases_for_component(c)),
                     key=lambda c: gpu_broker.BROKER.cfg.peak(c))
check("★ 前提:至少有一个组件真的被别名指着(否则下面那条契约测的是另一条分支)",
      bool(gateway.aliases_for_component(_small_aliased)), _small_aliased)

_observed = {}
_AS["device"] = "PC-A"
with _Isolated() as _c:
    gpu_broker.BROKER._permitted_on_demand = [_small_aliased]
    _rl = _c.post("/v1/gpu/lease", headers=_LAN_H,
                  json={"if_generation": _gen(_c), "kind": "client_session",
                        "components": [], "ttl_s": 60})
    _observed["CONTRACT:gpu.lease.grant"] = (_rl.status_code, set(_rl.json()))
    _rr = _c.post("/v1/gpu/lease/renew", headers=_LAN_H,
                  json={"lease_id": _rl.json()["lease"]["lease_id"],
                        "fence_token": _rl.json()["fence_token"], "ttl_s": 60})
    _observed["CONTRACT:gpu.lease.renew"] = (_rr.status_code, set(_rr.json()))
    # 意图:挑一个真的映射到 _small_aliased 的别名
    _alias = next((a for a in gateway.REGISTRY
                   if _small_aliased in gateway.components_for_alias(a)), None)
    if _alias:
        _ri = _c.post("/v1/gpu/intent", headers=_LAN_H, json={"alias": _alias})
        _observed["CONTRACT:gpu.intent"] = (_ri.status_code, set(_ri.json()))
    _re = _c.post("/v1/session/end", headers=_LAN_H, json={"reason": "quit"})
    _observed["CONTRACT:session.end"] = (_re.status_code, set(_re.json()))

    # ── V5 · CONTRACT:gpu.snapshot ──────────────────────────────────
    _rs = _c.get("/v1/gpu/snapshot", headers=_LAN_H)
    _observed["CONTRACT:gpu.snapshot"] = (_rs.status_code, set(_rs.json()))
    _snap_body = _rs.json()

    # ── V5 · CONTRACT:gpu.components ────────────────────────────────
    _rc = _c.get("/v1/gpu/components", headers=_LAN_H)
    _observed["CONTRACT:gpu.components"] = (_rc.status_code, set(_rc.json()))
    _cat_body = _rc.json()

    pass

# ── V5 · CONTRACT:gpu.intended(200 与 409 两个形状都要)────────────────
# ★★ 2026-08-07(D?):这两次**必须用主机档**打。副机已经没有 change_resident
#   (四集合表:intended 只有主机变更面能写)⇒ 拿 lan-device 打只会拿到 403,
#   而 403 的形状**不是**这条契约要登记的那个形状 —— 那样登记下来的"契约"
#   会把客户端引到一条它永远解析不出 result/snapshot 的路上。
#   ⇒ 换 `trusted-local`(不带 _LAN_H:那个头会把它解析成 lan-device)。
with _Isolated(tier="trusted-local") as _c:
    # ★ 这里**不能**走 _gen():它的默认头是 _LAN_H(证书指纹),而 `h or _LAN_H` 里
    #   空字典是假值 ⇒ 传 {} 也照样带上那个头,请求就被解析成另一个主体了(实测 KeyError)。
    _g_host = _c.get("/v1/gpu/snapshot").json()["generation"]
    _ri2 = _c.post("/v1/gpu/intended",
                   json={"if_generation": _g_host, "components": [_small]})
    _observed["CONTRACT:gpu.intended"] = (_ri2.status_code, set(_ri2.json()))
    # ★ 故意用一个**过期**的世代号 —— 这是客户端最常撞上的那条失败路径
    _rconf = _c.post("/v1/gpu/intended",
                     json={"if_generation": -1, "components": [_small]})
    _intended_conflict = (_rconf.status_code, _rconf.json())


check("★ 元断言:意图那条契约真的被打到了(别名桥没断)",
      "CONTRACT:gpu.intent" in _observed,
      f"没有任何别名映射到 {_small} —— 那本身就是一条要查的事")
#: 不走「打一次端点、读顶层键集合」那条通用路的契约 —— 各自在下面单独钉。
#  ★ 有了这个集合,下面那条元断言才能把「没被观测到」与「有意另行处理」分开。
_SPECIAL_CIDS = {"CONTRACT:gpu.intended.blocking", "CONTRACT:gpu.events.frame"}

# ★★★ 元断言:每条契约要么被真的打过一次,要么显式登记为特例。
#   此前这个循环写的是 `if _cid not in _observed: continue` —— **静默跳过**:
#   请求万一失败(状态机、额度、权限任一处),那条契约的检查就凭空消失,
#   而覆盖账照样全绿。那正是这张表本身要防的形状,只不过长在检查器自己身上。
check("★★★ 元断言:每条契约要么被实打过、要么在特例表里(不许静默跳过)",
      set(CROSS_PROCESS_CONTRACTS) == set(_observed) | _SPECIAL_CIDS,
      f"没打到也不在特例表:{sorted(set(CROSS_PROCESS_CONTRACTS) - set(_observed) - _SPECIAL_CIDS)}"
      f" · 打到了却不在登记表:{sorted(set(_observed) - set(CROSS_PROCESS_CONTRACTS))}")

for _cid, (_what, _keys) in CROSS_PROCESS_CONTRACTS.items():
    if _cid == "CONTRACT:gpu.intended.blocking":
        # blocking 那一条不是顶层响应,是 Lease.to_json() 的形状 —— 直接对着它钉
        _lease_keys = set(gpu_broker.Lease(
            "id", "fence", "client_session", "h", [], 0.0, 1.0).to_json())
        check(f"★★ {_cid}({_what})顶层键集合恰好是登记的那一组",
              _lease_keys == _keys, f"实得 {sorted(_lease_keys)}")
        continue
    if _cid in _SPECIAL_CIDS or _cid not in _observed:
        continue
    _st, _got = _observed[_cid]
    check(f"★★ {_cid}({_what})状态 200", _st == 200, _st)
    check(f"★★★ {_cid} 顶层键集合**恰好**是登记的那一组 —— "
          "「多一个键」和「换了一个键」都要红,数量断言拦不住后者",
          _got == _keys, f"多 {sorted(_got - _keys)} 少 {sorted(_keys - _got)}")


# ══════════════════════════════════════════════════════════════════════
#  V5 · 四条欠债的服务端半边,逐条把「这条契约到底要防什么」写成判据
# ══════════════════════════════════════════════════════════════════════
print("\n=== V5 · CONTRACT:gpu.snapshot —— generation 读错会伪装成「中枢忙」 ===")
check("★★★ generation 在**顶层**,而且是整数 —— "
      "客户端 LeaseKeeper 拿它去发租约,它读错的表现是**每次 if_generation 都 409**,"
      "而 409 的字面意思是「别处刚改过」⇒ 一次解析缺陷会稳定伪装成中枢并发",
      isinstance(_snap_body.get("generation"), int)
      and not isinstance(_snap_body.get("generation"), bool),
      f'{type(_snap_body.get("generation")).__name__} = {_snap_body.get("generation")!r}')
check("★ 且它非负(世代号是单调计数器,负数只可能是形状对不上)",
      _snap_body["generation"] >= 0, _snap_body["generation"])
check("★★ 五个集合都在 sets 里(界面要能分清「你勾的」与「系统临时装的」)",
      set(_snap_body["sets"]) == {"intended_resident", "committed_resident",
                                  "actual_resident", "permitted_on_demand",
                                  "transient_resident"}, sorted(_snap_body["sets"]))
check("★★ vram 段的键集合也钉住 —— 面板靠它区分两种撞墙,少一个就算不出第二种",
      set(_snap_body["vram"]) == {"free_gib", "total_gib", "vram_budget", "desktop_floor",
                                  "non_ai_used_gib_inferred", "non_ai_is_inferred",
                                  "non_ai_note"}, sorted(_snap_body["vram"]))

print("\n=== V5 · CONTRACT:gpu.events.frame —— SSE 的契约是【每一帧】,不是响应体 ===")


# ══════════════════════════════════════════════════════════════════════
#  ★★★ 为什么这一条**不用 TestClient**(实测踩过,不是预防性洁癖)
#
#  第一版写的是 `with _c.stream("GET", "/v1/gpu/events") …` 读第一帧就 break ——
#  **整个套件挂住,120 秒后被超时杀掉**。根因是那条流**永不结束**:
#  退出 with 时要关连接,而服务端那个生成器正 await 在 `wait_for_change` 上,
#  TestClient 的 portal 等它收尾,两边互相等。
#  ★ 与 ASSERTION-PITFALLS 第 6 条同源但不是同一条:那条说的是**跨连接**推送
#    (两个 TestClient 各起一个事件循环),这条是**单连接的无限流**。
#
#  ⇒ 改成直接驱动端点那个**真的**异步生成器,只 __anext__ 一次。
#    ★ 它仍然是在测真实现(`gateway.gpu_events` 本体),不是抄一份形状来测自己:
#      第一帧在 `is_disconnected()` 之前就 yield 出来了,所以不需要任何连接语义。
# ══════════════════════════════════════════════════════════════════════
class _FakeReqSSE:
    """给 `gpu_events` 用的最小 Request:它只读 headers,再把 gen() 交出来。"""

    def __init__(self, headers=None):
        self.headers = headers or {}
        self.client = None

    async def is_disconnected(self):
        return True          # ★ 第二帧起就断开 —— 生成器不会无限跑下去


async def _first_gpu_frame():
    resp = await gateway.gpu_events(_FakeReqSSE(_LAN_H))
    it = resp.body_iterator
    raw = await it.__anext__()
    try:
        await it.aclose()
    except Exception:                                         # noqa: BLE001
        pass
    return raw


_cc_s, _lp_s = gateway.classify_caller, gateway.resolve_lan_principal
try:
    gateway.classify_caller = lambda r: "lan-edge"
    gateway.resolve_lan_principal = lambda fp: {"tier": "lan-device", "device_id": "PC-A"}
    _raw_frame = asyncio.run(_first_gpu_frame())
except Exception as _e:                                       # noqa: BLE001
    _raw_frame = None
    check("★ 取第一帧不该抛", False, f"{type(_e).__name__}: {_e}")
finally:
    gateway.classify_caller, gateway.resolve_lan_principal = _cc_s, _lp_s

check("★ 至少收到一帧(收不到就什么都没测到,那比测错更该红)",
      isinstance(_raw_frame, str) and _raw_frame.startswith("event: "), repr(_raw_frame)[:120])
if isinstance(_raw_frame, str) and _raw_frame.startswith("event: "):
    # 一帧 = "event: <名字>\ndata: <json>\n\n"
    _ev0 = _raw_frame.split("\n", 1)[0][len("event: "):].strip()
    _data0 = _raw_frame.split("data: ", 1)[1].rstrip("\n")
    check("★★ 连上先给的是 event: snapshot(重连即对齐,不必先问一次)",
          _ev0 == "snapshot", _ev0)
    _fr = _json.loads(_data0)
    check("★★★ **帧的 data 载荷**顶层键集合 == 快照的那一组 —— "
          "钉响应体是钉不出来的:那条流永不结束,它根本没有顶层键集合",
          set(_fr) == _SNAPSHOT_TOP_KEYS,
          f"多 {sorted(set(_fr) - _SNAPSHOT_TOP_KEYS)} 少 {sorted(_SNAPSHOT_TOP_KEYS - set(_fr))}")
# ★★ 三种带数据的事件名是**闭集**:客户端按事件名分派,多一种它认不出、少一种它收不到。
#   判据取源码 —— 心跳与错误帧要等 15 秒/等崩溃才发得出来,不能在门禁里真等。
_ge_src = inspect.getsource(gateway.gpu_events)
for _evname in ("snapshot", "update", "keepalive", "error"):
    check(f"★ 推送流会发 event: {_evname}", f"event: {_evname}" in _ge_src)
check("★★★ error 帧的形状是 {type, message} —— 中枢自报出错时必须**说得出原因**,"
      "客户端才不会把它翻译成一句『帧读不懂』(那是指向别处的假理由)",
      '"type": type(e).__name__' in _ge_src and '"message": str(e)' in _ge_src, _ge_src[-400:])
check("★★ keepalive 帧**带完整快照**(裸心跳会去喂客户端的『数据新鲜』判断)",
      "event: keepalive" in _ge_src and "snap.to_json()" in _ge_src)

print("\n=== V5 · CONTRACT:gpu.components —— 取不到就【什么都不列】,不是兜底 ===")
check("★★ 目录逐条列出准入白名单全体,一个不漏(漏一个 = 用户看不见但闸仍然会算它)",
      {c["id"] for c in _cat_body["components"]} == set(gpu_broker.BROKER.cfg.components),
      f'实得 {sorted(c["id"] for c in _cat_body["components"])}')
check("★★★ 每个组件的键集合恰好是这一组 —— 客户端 ParseCatalog 逐个 GetProperty,"
      "少一个键它会整份返回 null(**这是设计**:不保留半份目录)",
      all(set(c) == {"id", "display", "kind", "peak_gib", "note",
                     "intended", "committed", "permitted_on_demand", "transient_resident"}
          for c in _cat_body["components"]),
      f'实得 {sorted(_cat_body["components"][0])}')
check("★★ budget 段的键集合(客户端拿它算两堵墙,safety_margin 缺了第二堵墙算不出来)",
      set(_cat_body["budget"]) == {"vram_budget", "total_gib", "desktop_floor",
                                   "free_gib", "safety_margin"},
      sorted(_cat_body["budget"]))
check("★ aliases_by_component 覆盖全部组件(界面要说清『勾掉它,哪些功能会停』)",
      set(_cat_body["aliases_by_component"]) == set(gpu_broker.BROKER.cfg.components))

print("\n=== V5 · CONTRACT:gpu.intended —— 失败必须回带 snapshot,否则客户端无从重试 ===")
_cst, _cbody = _intended_conflict
check("★ 世代号对不上回 409(不是 200 里塞一个 ok=false)", _cst == 409, _cst)
check("★★★ 409 的顶层键集合含 snapshot —— 客户端要拿它里面的新 generation 重试;"
      "只回一个裸 409 会让它必须再发一次请求才知道现在是什么样,那就又变成轮询了",
      "snapshot" in _cbody and "error" in _cbody, sorted(_cbody))
check("★★ 而且那个 snapshot 是**完整快照**(键集合与 gpu.snapshot 那条一致)——"
      "半份快照客户端读不出 generation,重试照样撞 409",
      set(_cbody["snapshot"]) == _SNAPSHOT_TOP_KEYS,
      f'少 {sorted(_SNAPSHOT_TOP_KEYS - set(_cbody["snapshot"]))}')
check("★ 失败码点名(generation_conflict),不是一句『失败了』",
      _cbody["error"]["type"] == "generation_conflict", _cbody["error"])

# ── 元断言:每一条契约在**客户端**那半边都必须有配对 ──
#   ★ 缺配对即判红。找不到客户端源码也判红 —— 「查不了」不等于「没问题」,
#     那正是本项目最恨的那种静默。
_SELFTEST = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                         "..", "..", "20-client-win", "app", "Selftest.cs")
_st_src = None
try:
    with open(_SELFTEST, "r", encoding="utf-8") as _f:
        _st_src = _f.read()
except Exception as _e:                                       # noqa: BLE001
    _st_src = None
check("★★★ 能读到客户端自检源码(读不到 ⇒ 配对无从核对 ⇒ 判红,不当作没问题)",
      _st_src is not None, f"{_SELFTEST}")
if _st_src is not None:
    # ══════════════════════════════════════════════════════════════════
    #  ★★★ 2026-08-06 夜(V5)修:判据从裸 `in` 换成**带边界**的匹配。
    #
    #  实测撞到:新登记 `CONTRACT:gpu.intended` 那一刻,这条元断言**当场就绿了** ——
    #  而客户端那半边一个字都还没写。原因是它是 `CONTRACT:gpu.intended.blocking`
    #  的**前缀**,裸 `in` 在后者身上命中。
    #  ⇒ 一条"缺配对即判红"的护栏,在**恰好最容易缺配对**的那种命名下静默失效。
    #  ★ 这与本仓已记的那条同形(`/v1/gpu/lease` 是 `/v1/gpu/lease/renew` 的前缀,
    #    裸 `in` 会把后者算成前者的消费者)—— 同一个坑,换了个位置。
    #  ⇒ 契约号只由 [a-z0-9_.] 组成,所以边界判据是「后面不许再跟这些字符」。
    # ══════════════════════════════════════════════════════════════════
    def _has_anchor(src: str, cid: str) -> bool:
        return re.search(re.escape(cid) + r"(?![a-z0-9_.])", src) is not None

    for _cid in CROSS_PROCESS_CONTRACTS:
        check(f"★★★ 元断言:{_cid} 在客户端那半边有配对断言(缺配对即判红)",
              _has_anchor(_st_src, _cid),
              "Selftest.cs 里找不到这个契约号 —— 服务端钉了形状,客户端没人证明它读得懂")
    check("★ 且元断言本身不是空转(确实读到了内容)", len(_st_src) > 1000)
    # ★★ 判据自检:拿一个**只以已登记契约号为前缀**的假契约号问一遍,必须判**不在**。
    #   没有这一条,上面那条边界修复本身也只是"看着修好了"。
    check("★★★ 元断言的边界判据自己被钉住:前缀不算命中(gpu.intended 不许靠 "
          "gpu.intended.blocking 蒙混过关)",
          not _has_anchor("CONTRACT" + ":gpu.intended.blocking 只有这一个",
                          "CONTRACT" + ":gpu.intended"))
    check("★ 反向:真的写了那个契约号时,边界判据认得出来",
          _has_anchor("… CONTRACT" + ":gpu.intended —— 说明 …", "CONTRACT" + ":gpu.intended"))

# ══════════════════════════════════════════════════════════════════════
#  V9 · 自动关栈(D?)—— 判据在中枢侧、跨机、fail-closed
#
#  ★★★ 这一组的第一个理由是**同一形状已经出现三次**:
#    A5 库写好零调用点 · doctor ⑫ 环写好没提交 · `model_loader.shutdown()` 调用点 0 处。
#    ⇒ 通则:**凡"写好了的收尾/清理函数",都要有一条断言钉住它有调用点。**
#    下面第一条就是这条通则在本例上的落点。
# ══════════════════════════════════════════════════════════════════════
print("\n=== V9:关栈的安全判据 + 收尾函数必须有调用点 ===")

# ── ★★★ 通则:凡"写好了的收尾/清理函数",都要有一条断言钉住它有调用点 ──
#   今天同一形状第三次:A5 库零调用点 · doctor ⑫ 环写好没提交 · ModelLoader.shutdown 零调用点。
_shutdown_handlers = list(getattr(gateway.app.router, "on_shutdown", []))
check("★★★ 网关注册了 shutdown 钩子 —— 在这之前**一个都没有**,"
      "而 ModelLoader.shutdown() 的调用点是 0 处 ⇒ 关网关会留下孤儿后端、显存继续占着",
      len(_shutdown_handlers) >= 1, f"实测 {len(_shutdown_handlers)} 个")
_reap_src = assert_helpers.code_only(gateway._reap_backends_on_shutdown)
check("★★★ 那个钩子**真的调了**装载器的 shutdown(不是只登记了一个空函数)——"
      "「写好了的收尾函数有没有调用点」正是这条断言要钉的那件事",
      ".shutdown()" in _reap_src and "_loader" in _reap_src)
check("★★ 钩子**不碰** _adopted —— 认领来的不是我们起的,不该被我们杀;"
      "那条边界在 model_loader 里本来就写对了,这里只是别去'改进'它",
      "_adopted" not in _reap_src)

# ── ★ 坑 2 的检查保留:关了会不会切断别人,要能问得出来 ──
_ok, _why = gateway.safe_to_stop_stack(blocking=0, resident=0)
check("★★★ 没人在用时答【可以关】—— **判据不能是恒假的**:"
      "一个永远答'不能关'的判据和没有判据是一回事", _ok, _why)
for _kw, _name in (
    (dict(blocking=1, resident=0), "有 blocking 租约(坑 2 · D90②)"),
    (dict(blocking=0, resident=1), "还有组件驻留着"),
):
    _r, _w = gateway.safe_to_stop_stack(**_kw)
    check(f"★★ 答不能关:{_name}", not _r, _w)
    check(f"★ 而且说得出理由:{_name}", len(_w) > 10)

# ── ★★ 口径变更(2026-08-07):自动关的那一半**撤掉了**,别让它悄悄长回来 ──
check("★★★ 网关里**没有**自动关栈的执行者 —— 新设计下关栈是【人的动作】不是推断;"
      "跨机空闲阈值/副机在线名单/巡检全部撤掉(它们是在替人做判断)",
      not any(hasattr(gateway, _n) for _n in
              ("_stack_shutdown_sweeper", "peers_online", "stack_idle_seconds",
               "STACK_IDLE_SHUTDOWN_S", "STACK_AUTO_SHUTDOWN")))
_safe_src = assert_helpers.code_only(gateway.safe_to_stop_stack)
check("★★ 而 safe_to_stop_stack **自己不关任何东西**(只回答安不安全)",
      "kill" not in _safe_src and "SIGINT" not in _safe_src)


##########################################################################
#  V16 · D? · Broker 卸载与启动重整 —— 三条**今天第一次可达**的缺陷
#
#  来源:V13 收工报告(提交 0474d8c,packet host-loopback-business-route §4.2)。
#  三条都**不是 V13 引入的** —— 在它之前没有任何设备写得了 intended,所以一次都
#  不可能被触发;V13 把那条路打通,它们才第一次真实可达。
#
#  ★★★ 本节每一条断言都**先在旧代码上判过红**(V16 收工报告附实测),
#    因为「没红过的护栏」和「没有护栏」是一回事(ASSERTION-PITFALLS 通则)。
#
#  ★ 实机复现(2026-08-08,本机真中枢 · 真进程 · 真 NVML · 无任何故障注入):
#    路径 A:勾 speech.lite → 确定 → 装上 → 取消勾选 → 确定
#            实测 free 14.559 → 14.557(**足迹 0.002 GiB**,而 peak 声称 2.07)
#            ⇒ 10.7 秒后 vram_not_reclaimed → RECONCILING → 此后一切「点确定」退 busy。
#            逐条试过 finish_startup / set_power(False) / set_power(True) / 再点确定
#            —— **一条都出不去**,只能重启进程。
#    路径 B:按需起 llm.assistant.8b@8k(实测占 5.311 GiB)→ 模拟网关重启 →
#            adopt 把它当成 8b@16k 采纳进 committed → 取消勾选 → 确定
#            ⇒ 认领来的进程按边界不杀,而账本把它抹了 ⇒ 一个 6.5 GiB 的进程活着,
#            `running()` 报空,**I2/I3/I4 三条全绿**,再重启一次又被采纳一遍。
##########################################################################

#: ★★ 取证用的容错取值器。**只用在采数那一步,不用在判词里**。
#  理由与本文件头那条编码双保险同款:这一节的断言必须能在**修好之前**的代码上
#  逐条判红 —— 而旧代码里 `_footprint` / `reconcile_tick` / `residency_truth`
#  压根不存在,一条 AttributeError 会把整套脚本掀翻,于是
#  「一条断言变红」表现成「整套崩溃」,运行器看不出是**哪一条**没守住。
#  ⇒ 缺了就回 None/空,让**断言自己**去判红。判词一个字没放松:
#    下面每条断言比的都是具体的值,拿 None/空 一样红。
def _v16_get(obj, name, default=None):
    return getattr(obj, name, default)


def _v16_m(obj, name):
    """取一个协程方法;不存在就回一个总是回 None 的替身(理由同上)。"""
    fn = getattr(obj, name, None)
    if fn is not None:
        return fn

    async def _absent(*a, **k):
        return None
    return _absent


print("\n=== V16 ① · 回收判据:peak 是**准入上界**,不是必然回吐量 ===")

# ── 判据本身还在(方案书行 1507 那条没被删掉,只是换了期望值的来源)──
check("★ vram_not_reclaimed 这个码仍然存在(不是把闸删掉了事)",
      "vram_not_reclaimed" in inspect.getsource(gpu_broker.Broker._await_reclaim))
check("★ 容差/超时两个常量一个没动", gpu_broker.RECLAIM_TOLERANCE_GIB == 0.2
      and gpu_broker.RECLAIM_TIMEOUT_S == 10.0)

_exp_fn = getattr(gpu_broker.Broker, "_expected_reclaim", None)
check("★★★ 存在一条**专门算「该吐回来多少」**的函数 —— "
      "旧代码把这个算式内联在 apply_intended 里,于是它既测不到、也没人能指着它说话",
      _exp_fn is not None)
if _exp_fn is not None:
    _exp_src = assert_helpers.code_only(_exp_fn)
    check("★★★ 它的源码里**没有 peak** —— peak 是准入用的保守上界,"
          "vram-budget.toml 自己写着「5.31 > 5.0,闸变更保守」是 fail-safe 方向;"
          "而在回收方向,多算一点就是**必然误报**",
          "peak" not in _exp_src, _exp_src[:200])
# ★ 元断言:上面那条"没有 peak"的判据不能是个永远不响的探测器 ——
#   同一个词在**真的用 peak 的那段**必须查得到。
check("★★ 元断言:同一个词在 pressure_victims(真的按 peak 挑人)里查得到 —— "
      "否则「源码里没有 peak」只是因为我把词写错了",
      "peak" in assert_helpers.code_only(gpu_broker.Broker.pressure_victims))


class _VramLoader:
    """★ 会**真的影响那个 free 读数**的替身 —— 装载器改的是显存,不是账本。

    `cost` 逐组件给:0 就是「装上了但一个字节都不占」(speech.lite 的真实形态,
    实测足迹 0.002 GiB)。判据必须能分清「没占」与「没卸掉」。
    """

    def __init__(self, broker, cost):
        self.b, self.cost, self.loaded = broker, dict(cost), set()

    async def load(self, ids):
        for c in ids:
            self.loaded.add(c)
            self.b._free = round(self.b._free - self.cost.get(c, 0.0), 4)

    async def unload(self, ids):
        rep = {"killed": [], "skipped_adopted": [], "kill_failed": []}
        for c in ids:
            if c in self.loaded:
                self.loaded.discard(c)
                self.b._free = round(self.b._free + self.cost.get(c, 0.0), 4)
                rep["killed"].append(c)
        return rep

    async def verify_unloaded(self, ids, keep):
        return [{"component": c, "port": 0, "port_state": "ready",
                 "we_spawned_it": True, "pid": None, "why": "杀不掉"}
                for c in ids if c in self.loaded]

    async def readopt(self, ids):
        return []

    async def running(self):
        return sorted(self.loaded)

    async def adopt(self):
        return []


def _mkvram(free, cost):
    b = gpu_broker.Broker(cfg=gpu_broker.BROKER.cfg)
    b._state = gpu_broker.STATE_READY
    b._free = free
    b._sampled_at = 0.0
    b._sample_once = lambda: None          # ★ free 由 _VramLoader 直接改,不去真采样
    b.attach_loader(_VramLoader(b, cost))
    return b


async def _t_v16_reclaim():
    o = {}
    # ── ① 零足迹组件(speech.lite 的真实形态):装上 → 卸掉,事务**必须成功** ──
    #   旧代码:expect = free + peak(2.07),而 free 一动不动 ⇒ 等满 10 秒 → RECONCILING。
    b = _mkvram(free=14.5, cost={_small: 0.0})
    t0 = time.monotonic()
    r_on = await b.apply_intended([_small], permitted=[])
    o["fp_zero"] = dict(_v16_get(b, "_footprint", {}) or {})
    r_off = await b.apply_intended([], permitted=[])
    o["zero"] = (r_on.ok, r_off.ok, r_off.code, r_off.state, list(b._committed),
                 time.monotonic() - t0)

    # ── ② 真占显存的组件:装上量到足迹,卸掉时按**足迹**等,一样过 ──
    #   ★ 挑**闸放得过**的那个里 peak 最大的 —— 挑全表最大(30b 的 11.9 > 预算 8.52)
    #     会在预检就被拒,于是这条用例根本走不到装载,而它自称在测的是装载后的足迹。
    #     那是 ASSERTION-PITFALLS 第 5 条的形状:**判据落在了它没在看的那条路径上**。
    _big = max((c for c in gpu_broker.BROKER.cfg.components
                if gpu_broker.BROKER.cfg.peak(c) <= gpu_broker.BROKER.cfg.budget.vram_budget),
               key=lambda c: gpu_broker.BROKER.cfg.peak(c))
    o["big_peak"] = gpu_broker.BROKER.cfg.peak(_big)
    b2 = _mkvram(free=64.0, cost={_big: 3.0})     # ★ 真实占用 3.0,而 peak 声称更多
    await b2.apply_intended([_big], permitted=[])
    o["fp_big"] = dict(_v16_get(b2, "_footprint", {}) or {})
    r2 = await b2.apply_intended([], permitted=[])
    o["big"] = (r2.ok, r2.code, r2.state)

    # ── ③ **反向**:显存真的没吐回来 ⇒ 仍然要判 vram_not_reclaimed ──
    #   没有这一条,上面两条可以被一个「永远说通过」的实现全部满足。
    b3 = _mkvram(free=64.0, cost={_big: 3.0})
    await b3.apply_intended([_big], permitted=[])
    b3._loader.cost[_big] = 0.0                   # 卸的时候**不还**那 3.0 GiB
    b3._await_reclaim = lambda *a, **k: _done("vram_not_reclaimed")   # 免去真等 10 秒
    r3 = await b3.apply_intended([], permitted=[])
    o["not_reclaimed"] = (r3.ok, r3.code, r3.state, list(b3._committed))
    return o


async def _done(v):
    return v


_v16a = asyncio.run(_t_v16_reclaim())
check("★★★ 零足迹组件(speech.lite:实测 0.002 GiB 而 peak 声称 2.07)"
      "**装得上也卸得掉** —— 这正是 V13 撞出来的那条路",
      _v16a["zero"][0] and _v16a["zero"][1] and _v16a["zero"][2] == ""
      and _v16a["zero"][3] == gpu_broker.STATE_READY, _v16a["zero"])
check("★★ 而且**没有等满那 10 秒** —— 没什么可等的时候就不该等",
      _v16a["zero"][5] < 5.0, f'{_v16a["zero"][5]:.2f}s')
check("★ 卸完之后 committed 是空的(账本跟上了现实)", _v16a["zero"][4] == [])
check("★★ 实测足迹被**真的量了一次**,而且量出来是 0(不是拿 peak 顶上)",
      _v16a["fp_zero"].get(_small) == 0.0, _v16a["fp_zero"])
check("★★★ 真占显存的组件:足迹量成**实测的 3.0**,而不是它声称的 peak —— "
      "旧判据要求把整个 peak 吐回来,差额就是它必然误报的量",
      bool(_v16a["fp_big"]) and abs(list(_v16a["fp_big"].values())[0] - 3.0) < 0.01
      and list(_v16a["fp_big"].values())[0] < _v16a["big_peak"],
      (_v16a["fp_big"], _v16a["big_peak"]))
check("★ 按足迹等,卸载事务通过", _v16a["big"][0] and _v16a["big"][2] == gpu_broker.STATE_READY,
      _v16a["big"])
check("★★★ **反向**:显存真的没吐回来 ⇒ 照样判 vram_not_reclaimed。"
      "没有这一条,上面几条可以被一个「永远说通过」的实现全部满足",
      _v16a["not_reclaimed"][1] == "vram_not_reclaimed", _v16a["not_reclaimed"])
check("★★★ 但它**不再是 RECONCILING** —— 进程确实没了、账本也记下了 ⇒ "
      "账本与现实**没有分家**,而 RECONCILING 的语义就是分家。"
      "拿它当『显存异常』的落点,是把一次可重试的失败变成死锁态",
      _v16a["not_reclaimed"][2] == gpu_broker.STATE_READY, _v16a["not_reclaimed"])
check("★★ 而且卸下来的那一份**真的从 committed 里去掉了** —— "
      "旧代码在这条路径上直接 return,committed 仍然列着一个进程已经死了的组件",
      _v16a["not_reclaimed"][3] == [], _v16a["not_reclaimed"][3])


print("\n=== V16 ② · RECONCILING 必须**出得去**,而且要留下可查的记录 ===")

# ── 反向全表:ALLOWED_TRANSITIONS 里写着合法的边,必须**真的有代码走过它** ──
#   旧代码:RECONCILING → READY 写在白名单里(:194)而全模块零调用点 ——
#   「看着有出口、实际没有」正是本项目最恨的那种形状。
check("★ 白名单里 RECONCILING → READY 这条边仍然登记着",
      gpu_broker.STATE_READY in gpu_broker.ALLOWED_TRANSITIONS[gpu_broker.STATE_RECONCILING])
check("★★★ 而且**有代码走它**:存在 reconcile_tick(判据)与 reconcile_to_actual(人的动作)",
      hasattr(gpu_broker.Broker, "reconcile_tick")
      and hasattr(gpu_broker.Broker, "reconcile_to_actual"))
check("★★ 采样循环里真的调了那条判据(不是写了个没人叫的函数 —— D102 那个形状)",
      "reconcile_tick()" in assert_helpers.code_only(gpu_broker.Broker._sampler_loop))
check("★★★ 而 reconcile_to_actual(会改 committed 的那条)**不在**采样循环里 —— "
      "它是人的动作,挂进自动路径就是 D10 禁的那种自动触发",
      "reconcile_to_actual" not in assert_helpers.code_only(gpu_broker.Broker._sampler_loop))
check("★★ 它在网关的**开机路**上有调用点(写好了的恢复动作零调用点 = 没写)",
      "reconcile_to_actual" in assert_helpers.code_only(gateway._start_gpu_broker))
check("★★★ **反向**:它**不在任何请求处理器**里 —— 它会改 committed,"
      "而开机那一刻 committed 本来就是刚从现实推出来的、还没承载任何用户意图;"
      "挂到请求路径上就变成了「一个请求把别人的账本改了」",
      "reconcile_to_actual" not in assert_helpers.code_only(gateway.gpu_intended)
      and "reconcile_to_actual" not in assert_helpers.code_only(gateway.gpu_intent))

# ══════════════════════════════════════════════════════════════════════
#  ★★★ V16 · 反向全表:白名单里的**每一条边**都要登记一个**驱动者**
#
#  这条表就是 V16 撞到的那件事的**一般形式**:`RECONCILING → READY` 写在
#  `ALLOWED_TRANSITIONS` 里(:194),而全模块**零调用点** ——
#  于是「看着有出口、实际没有」躲过了此前**所有**断言,直到有人真的被卡在里面。
#  ⇒ 从此:加一条边而不给它驱动者,是一件**会红**的事;
#    而"今天有意留着没有驱动者"的那几条,必须**逐条写在下面这张表里**,
#    ★ 那不是豁免,是一张**看得见的欠债表**。
# ══════════════════════════════════════════════════════════════════════
_S = gpu_broker
_EDGE_DRIVERS = {
    (_S.STATE_STARTING,    _S.STATE_READY):         "finish_startup",
    (_S.STATE_STARTING,    _S.STATE_RECONCILING):   "finish_startup",
    (_S.STATE_READY,       _S.STATE_STAGING):       "apply_intended",
    (_S.STATE_STAGING,     _S.STATE_PRECHECK):      "apply_intended",
    (_S.STATE_STAGING,     _S.STATE_READY):         "apply_intended",
    (_S.STATE_PRECHECK,    _S.STATE_APPLYING):      "apply_intended",
    (_S.STATE_PRECHECK,    _S.STATE_STAGING):       "_back_to_staging",
    (_S.STATE_APPLYING,    _S.STATE_READY):         "apply_intended",
    (_S.STATE_APPLYING,    _S.STATE_RECONCILING):   "_to_reconciling",
    # ★★★ V16 新接上的那一条 —— 在此之前它是白名单里唯一"合法却没人走"的出口边
    (_S.STATE_RECONCILING, _S.STATE_READY):         "reconcile_tick",
    (_S.STATE_RECONCILING, _S.STATE_DEGRADED_SAFE): "apply_intended",
    (_S.STATE_DEGRADED_SAFE, _S.STATE_STARTING):    "set_power",
}
#: **今天有意没有驱动者**的边。★ 一张欠债表,不是豁免表 —— 逐条写明为什么。
_EDGES_WITHOUT_DRIVER = {
    # READY 永远先经 STAGING 进事务(apply_intended 第 ① 步),所以这条直达边
    # 今天没有任何代码走。**留着**是因为 §8.1 的状态图里有它;
    # 若哪天真要用,得先在上表登记驱动者,否则本条断言会红。
    (_S.STATE_READY, _S.STATE_RECONCILING),
}
_ALL_EDGES = {(src, dst) for src, dsts in _S.ALLOWED_TRANSITIONS.items() for dst in dsts}
check("★★★ 白名单里的每条边**要么有驱动者、要么登记在欠债表里** —— "
      "没有第三种。V16 之前 RECONCILING → READY 就落在第三种里:"
      "写着合法、零调用点、而所有断言都看不见它",
      _ALL_EDGES == (set(_EDGE_DRIVERS) | _EDGES_WITHOUT_DRIVER),
      f"没登记 {sorted(_ALL_EDGES - set(_EDGE_DRIVERS) - _EDGES_WITHOUT_DRIVER)} · "
      f"登记了但白名单里没有 {sorted((set(_EDGE_DRIVERS) | _EDGES_WITHOUT_DRIVER) - _ALL_EDGES)}")
for (_src, _dst), _drv in sorted(_EDGE_DRIVERS.items()):
    _fn = getattr(gpu_broker.Broker, _drv, None)
    check(f"★ 驱动者 {_drv} 真的存在({_src} → {_dst})", _fn is not None)
    if _fn is not None:
        check(f"★★ 而且它源码里真的写着那个目标状态({_src} → {_dst})——"
              "光有个函数名不算,判据要落在它真的走那条边上",
              f"STATE_{_dst}" in assert_helpers.code_only(_fn), _drv)
check("★★ 欠债表**只许变短**:今天恰好一条,而且是那条 READY → RECONCILING 直达边",
      _EDGES_WITHOUT_DRIVER == {(_S.STATE_READY, _S.STATE_RECONCILING)},
      sorted(_EDGES_WITHOUT_DRIVER))
check("★★★ 落盘的那条也有调用点,而且落在 {state}/logs(与 upstream_problem 同一套强 ACL)",
      hasattr(gateway, "log_gpu_reconcile")
      and inspect.getsource(gateway).count("log_gpu_reconcile(") >= 3
      and "gpu_reconcile.jsonl" in inspect.getsource(gateway.log_gpu_reconcile))


async def _t_v16_recon():
    o = {}
    # ── ① 账本与现实重新对上 ⇒ 自愈回 READY ──
    b = _mkbroker(free=64.0, loader=_FakeLoaderObs([_small]))
    b._state = gpu_broker.STATE_RECONCILING
    b._committed = [_small]
    b._actual_cache = [_small]
    o["tick_ok"] = (await _v16_m(b, "reconcile_tick")(), b._state)

    # ── ② 还没对上 ⇒ **停在 RECONCILING**(判据不是"到点就放行")──
    b2 = _mkbroker(free=64.0, loader=_FakeLoaderObs([]))
    b2._state = gpu_broker.STATE_RECONCILING
    b2._committed = [_small]
    b2._actual_cache = []
    o["tick_no"] = (await _v16_m(b2, "reconcile_tick")(), b2._state, b2.serves_requests())
    # 但它必须**说得出**是哪几项对不上,而不是一句"忙"
    _r = await b2.apply_intended([], permitted=[])
    o["busy_why"] = (_r.code, _r.message)
    # ── ③ 人的动作:以现实为准对齐 ⇒ 出得去,且 intended 一个字不动 ──
    b2._intended = [_small]
    o["realign"] = (await _v16_m(b2, "reconcile_to_actual")() or {}, b2._state,
                    list(b2._committed), list(b2._intended))

    # ── ④ **端到端**:一次真的失败事务把它打进 RECONCILING,之后**必须还能点确定** ──
    #   这正是 V13 实机撞出来的那条路(旧代码:此后一切变更永久 busy)。
    b3 = _mkbroker(free=64.0, loader=_FakeLoader(fail_load={_small}))
    r_fail = await b3.apply_intended([_small], permitted=[])
    _rl = _v16_get(b3, "_reconcile_log", []) or []
    o["entered"] = (r_fail.code, r_fail.state, tuple(x["event"] for x in _rl))
    b3._loader.fail_load = set()               # 故障过去了
    b3._actual_cache = []                      # 现实:一个都没装,与 committed(空)一致
    r_retry = await b3.apply_intended([_small], permitted=[])
    o["retry"] = (r_retry.ok, r_retry.code, r_retry.state)
    _rl2 = _v16_get(b3, "_reconcile_log", []) or []
    o["log"] = [x["event"] for x in _rl2]
    o["log_has_why"] = bool(_rl2) and all(x.get("code") and x.get("message") for x in _rl2)
    return o


_v16b = asyncio.run(_t_v16_recon())
check("★★★ 账本与现实重新对上 ⇒ 离开 RECONCILING 回 READY(此前**没有任何**代码走这条边)",
      _v16b["tick_ok"] == (gpu_broker.STATE_READY, gpu_broker.STATE_READY), _v16b["tick_ok"])
check("★★★ **反向**:还没对上就**停在 RECONCILING** —— "
      "一个「到点就放行」的实现能让上一条绿,却把这条判据整个抽空",
      _v16b["tick_no"][0] is None
      and _v16b["tick_no"][1] == gpu_broker.STATE_RECONCILING, _v16b["tick_no"])
check("★ 停在 RECONCILING 期间**仍然提供服务**(方案书行 1606 给它的原意)",
      _v16b["tick_no"][2] is True)
check("★★ 而拒绝的理由**点名**是哪几项对不上 —— 「忙」是个指向别处的假理由",
      _v16b["busy_why"][0] == "busy" and "committed" in _v16b["busy_why"][1],
      _v16b["busy_why"])
check("★★★ 人的动作:以现实为准对齐 ⇒ 出得去",
      _v16b["realign"][0].get("ok") and _v16b["realign"][1] == gpu_broker.STATE_READY,
      _v16b["realign"][:2])
check("★★ 对齐**只动 committed,不动 intended** —— 与 I4 同一条理由:"
      "系统对齐自己的账本,不该顺手改写用户勾了什么",
      _v16b["realign"][2] == [] and _v16b["realign"][3] == [_small], _v16b["realign"][2:])
check("★★★ 端到端:一次真失败把它打进 RECONCILING",
      _v16b["entered"][1] == gpu_broker.STATE_RECONCILING, _v16b["entered"])
check("★★★ 而**之后还点得动确定** —— 这一条就是 V13 撞出来的那条卡死路径。"
      "旧代码在这里永久返回 busy,只能重启进程",
      _v16b["retry"][0] is True and _v16b["retry"][2] == gpu_broker.STATE_READY,
      _v16b["retry"])
check("★★★ 进/出都留下了**可查的记录** —— 旧代码把 _transition 的 why 整个丢掉,"
      "而本模块没有任何日志 ⇒ 「为什么进的 RECONCILING」连重启之前都查不到",
      _v16b["log"] == ["entered", "resolved"], _v16b["log"])
check("★★ 每条记录都带**码与人话**(只记一个时间戳等于没记)", _v16b["log_has_why"])
check("★ 记录环有上限(不设上限的话长跑的中枢会把它涨成内存泄漏)",
      (_v16_get(gpu_broker.Broker, "RECONCILE_LOG_MAX") or 1 << 30) <= 64)
# ── ★★ 反向:DEGRADED_SAFE **不许**被自愈判据带出来(它是终态,只有人能开电源轴)──
_dg = _mkbroker(free=64.0, loader=_FakeLoaderObs([]))
_dg._state = gpu_broker.STATE_DEGRADED_SAFE
asyncio.run(_v16_m(_dg, "reconcile_tick")())
check("★★★ 反向:自愈判据**碰不到** DEGRADED_SAFE —— 它是终态,唯一出口是人重开电源轴(D10)。"
      "没有这条反向,一个「见状态就放行」的实现会把终态也一并放掉",
      _dg._state == gpu_broker.STATE_DEGRADED_SAFE, _dg._state)


print("\n=== V16 ③ · 「Broker 说已卸载」与「进程真的没了」之间那条**能为假**的判据 ===")

import model_loader as _mlx     # noqa: E402  ★ 本节要子类化装载器注入端口真假


class _PortLoader(_mlx.ModelLoader):
    """★ 把**端口的真假**注入进来 —— 不去真的绑 18081/18085(那是去动真实系统)。"""

    def __init__(self, cfg, live=()):
        super().__init__(cfg=cfg)
        self.live = set(live)

    async def _port_state(self, port, timeout=2.0):
        return self.PORT_READY if port in self.live else self.PORT_DOWN


_CFG = gpu_broker.BROKER.cfg
_P18081 = sorted(c for c in _CFG.components if int(_CFG.components[c].get("port") or 0) == 18081)
check("★ 前提:确实存在同端口多组件(18081 被 8b 三档共用)—— "
      "『分不清是哪一档』这条诚实边界要真的有对象",
      len(_P18081) >= 2, _P18081)


async def _t_v16_truth():
    o = {}
    # ── ① 账本空 + 端口活着 = 孤儿。running() **结构上看不见**,residency_truth 看得见 ──
    ld = _PortLoader(_CFG, live={18081})
    o["running_blind"] = await ld.running()
    o["truth"] = await _v16_m(ld, "residency_truth")() or {"orphan_ports": None, "orphan_candidates": {}}

    b = gpu_broker.Broker(cfg=_CFG)
    b.attach_loader(ld)
    b._actual_cache = await ld.running()
    b._residency_truth = o["truth"]
    o["i3_orphan"] = {r.invariant: (r.holds, r.detail) for r in b.check_invariants()}["I3"]

    # ── ② **反向**:端口全灭 ⇒ I3 必须回绿(一个永远判红的检测器等于没有检测器)──
    ld2 = _PortLoader(_CFG, live=set())
    b2 = gpu_broker.Broker(cfg=_CFG)
    b2.attach_loader(ld2)
    b2._actual_cache = await ld2.running()
    b2._residency_truth = await _v16_m(ld2, "residency_truth")()
    o["i3_clean"] = {r.invariant: r.holds for r in b2.check_invariants()}["I3"]

    # ── ③ **还没探过** ≠ 没问题:探针没跑时 I3 要说出来 ──
    b3 = gpu_broker.Broker(cfg=_CFG)
    b3.attach_loader(ld2)
    o["i3_unprobed"] = {r.invariant: r.detail for r in b3.check_invariants()}["I3"]

    # ── ④ unload 的回执:认领来的**没杀**必须说出来,不能与"已经没了"同形 ──
    ld3 = _PortLoader(_CFG, live={18081})
    ld3._adopted.add(_P18081[0])
    o["rep"] = await ld3.unload([_P18081[0]]) or {"killed": None, "skipped_adopted": None}
    o["verify"] = await _v16_m(ld3, "verify_unloaded")([_P18081[0]], keep=[]) or []
    o["readopt"] = await _v16_m(ld3, "readopt")([_P18081[0]])
    o["after_readopt"] = await ld3.running()

    # ── ⑤ 同端口多组件:隔壁那一档还在跑,**不算**没卸干净 ──
    ld4 = _PortLoader(_CFG, live={18081})
    o["verify_keep"] = await _v16_m(ld4, "verify_unloaded")([_P18081[0]], keep=[_P18081[1]])

    # ── ⑥ running() **不再因为一次非 2xx 就把 adopted 丢账** ──
    #   旧代码:非 2xx 即 discard ⇒ llama-server 加载中回 503 的那一瞬间,
    #   一个占着 6 GiB 的后端被从账本上永久抹掉,而抹掉之后再没人会回来探它。
    class _Loading(_PortLoader):
        async def _port_state(self, port, timeout=2.0):
            return self.PORT_ALIVE if port in self.live else self.PORT_DOWN

    ld5 = _Loading(_CFG, live={18081})
    ld5._adopted.add(_P18081[0])
    o["loading_running"] = await ld5.running()
    o["loading_kept"] = sorted(ld5._adopted)
    ld6 = _Loading(_CFG, live=set())
    ld6._adopted.add(_P18081[0])
    await ld6.running()
    o["down_dropped"] = sorted(ld6._adopted)
    return o


_v16c = asyncio.run(_t_v16_truth())
check("★★★ `running()` 对孤儿**结构上不可能为真** —— 它的候选池就是账本,"
      "账本忘了的那一条它永远不会去探。这是 S14 那条 /health 探活没抓到 V13 那一条的原因",
      _v16c["running_blind"] == [], _v16c["running_blind"])
check("★★★ 而 `residency_truth()` 探的是**全部登记端口** ⇒ 它**能为假**,"
      "并且这一次就为假了:18081 上有人,而账本说不出他是谁",
      _v16c["truth"].get("orphan_ports") == [18081], _v16c["truth"])
check("★★ 同端口多组件时**只报端口不报组件名**(分不清就不假装分得清,与 adopt 同一条边界)",
      set((_v16c["truth"].get("orphan_candidates") or {}).get(18081) or []) == set(_P18081),
      _v16c["truth"].get("orphan_candidates"))
check("★★★ I3 因此**判红** —— V16 之前它在同一处境下报绿"
      "(实机复现:6.5 GiB 的孤儿活着,I2/I3/I4 三条全绿)",
      _v16c["i3_orphan"][0] is False, _v16c["i3_orphan"])
check("★★ 而且说得出是**哪个端口**", "18081" in _v16c["i3_orphan"][1])
check("★★★ **反向**:端口全灭 ⇒ I3 回绿。一个永远判红的检测器和没有检测器是一回事",
      _v16c["i3_clean"] is True)
check("★★ **还没探过** ≠ 没问题:探针没跑时 I3 的理由里要说出这一点",
      "还没探过" in _v16c["i3_unprobed"], _v16c["i3_unprobed"])
check("★★★ `unload()` 回执把「没杀」与「已经没了」**分开** —— "
      "旧签名是 None,三个调用方一个字都收不到,于是两者在账上完全同形",
      _v16c["rep"].get("skipped_adopted") == [_P18081[0]]
      and _v16c["rep"].get("killed") == [], _v16c["rep"])
check("★★★ 卸完之后**核实得出来**它还活着(旧代码在这里一次核对都没有)",
      len(_v16c["verify"]) == 1 and _v16c["verify"][0].get("we_spawned_it") is False,
      _v16c["verify"])
check("★★ 还活着就**认回账上** —— 账本不该假装它已经没了;"
      "认回来不等于要杀它,边界(不是我们起的不该由我们杀)一个字没动",
      _v16c["readopt"] == [_P18081[0]] and _v16c["after_readopt"] == [_P18081[0]],
      (_v16c["readopt"], _v16c["after_readopt"]))
check("★★★ **反向**:隔壁那一档还占着同一个端口时,**不算**没卸干净 —— "
      "8b 三档共用 18081,不传 keep 的话每次卸载都会误报",
      _v16c["verify_keep"] == [], _v16c["verify_keep"])
check("★★★ `running()` 不再因为一次**非 2xx**就把 adopted 丢账 —— "
      "llama-server 加载中回 503,而那时它**已经占满了显存**",
      _v16c["loading_running"] == [] and _v16c["loading_kept"] == [_P18081[0]],
      (_v16c["loading_running"], _v16c["loading_kept"]))
check("★★ 而**真的连不上**(down)时仍然丢账 —— 否则账本只会涨不会缩",
      _v16c["down_dropped"] == [], _v16c["down_dropped"])

# ── _kill:杀不掉要**说出来**,而且**不许丢句柄** ──
class _Zombie:
    """★ terminate/kill 都不管用的进程 —— Windows 上两者都是 TerminateProcess,
    terminate 失败时 kill 会以同样的理由失败,而旧代码把两次异常都 pass 掉。"""
    pid = 4242
    returncode = None

    def poll(self):
        return None

    def terminate(self):
        raise OSError("拒绝访问")

    def kill(self):
        raise OSError("拒绝访问")


async def _t_v16_kill():
    ld = _PortLoader(_CFG, live={18081})
    ld._procs[_P18081[0]] = _Zombie()
    ok = await ld._kill(_P18081[0])
    return ok, list(ld._procs), (await ld.unload([_P18081[0]])
                                or {"kill_failed": None, "killed": None})


_kl_ok, _kl_procs, _kl_rep = asyncio.run(_t_v16_kill())
check("★★★ 杀不掉 ⇒ `_kill` 返回 False(旧代码永不抛、永不回,每一次卸载都『成功』)",
      _kl_ok is False)
check("★★★ 而且**句柄留在账上** —— 旧代码第一句就 pop,于是这个进程"
      "再也没有任何一条路径能杀它第二次,`running()` 也永远不会再探它",
      _kl_procs == [_P18081[0]], _kl_procs)
check("★★ unload 的回执把它记进 kill_failed(与 killed / skipped_adopted 三分)",
      _kl_rep.get("kill_failed") == [_P18081[0]] and _kl_rep.get("killed") == [], _kl_rep)
# ── ★★★ 两个来源必须**合起来**看:端口探针盖不到不监听端口的组件(comfyui port=0),
#   而回执盖不到「按边界没杀、但还占着显存」的认领孤儿。只用一个 = 有一整格盲区。
_NOPORT = sorted(c for c in _CFG.components if not int(_CFG.components[c].get("port") or 0))
check("★ 前提:确实存在**不监听端口**的组件(comfyui)—— 那一格端口探针什么都说不出来",
      bool(_NOPORT), _NOPORT)
_sf = getattr(gpu_broker.Broker, "_unload_shortfall", lambda *a: None)
check("★★★ 端口探针说没事,但回执里有 kill_failed ⇒ 仍然算**没卸掉**。"
      "只看端口的话,comfyui 这类不监听端口的组件杀失败会**完全静默**",
      [x["component"] for x in (_sf({"kill_failed": [_NOPORT[0]]}, []) or [])] == [_NOPORT[0]]
      if _NOPORT else False)
check("★★ **反向**:回执干净且端口探针也干净 ⇒ 就是干净的"
      "(一个永远说『没卸掉』的合流器和没有合流器是一回事)",
      _sf({"killed": [_NOPORT[0]], "kill_failed": []}, []) == [])
check("★ 两个来源指向同一个组件时**不重复计**",
      len(_sf({"kill_failed": [_P18081[0]]},
              [{"component": _P18081[0], "port": 18081, "port_state": "ready",
                "we_spawned_it": True, "pid": 1, "why": "x"}]) or []) == 1)

# ── 置信度:装载器接上了但**一次都没探过**时,actual 退回账本 ⇒ 不许自称 observed ──
_c_unprobed = gpu_broker.Broker(cfg=_CFG)
_c_unprobed.attach_loader(_PortLoader(_CFG))
_c_unprobed._committed = [_small]
check("★★★ 装载器接上但**还没探过**(_actual_cache is None)⇒ actual 退回账本,"
      "confidence 必须如实标 self_reported。旧判据只问「接线在不在」,"
      "于是实机复现时三条不变式自称 observed 而数据来自账本本身",
      list(_c_unprobed.actual_resident) == [_small]
      and {r.invariant: r.confidence
           for r in _c_unprobed.check_invariants()}["I2"] == "self_reported")


print(f"\n=== GPU Broker 骨架:{_pass} PASS · {_fail} FAIL ===")
sys.exit(1 if _fail else 0)
