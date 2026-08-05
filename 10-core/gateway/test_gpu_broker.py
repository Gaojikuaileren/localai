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

import assert_helpers
import gateway
import gpu_broker

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
}
_gpu_routes = {(m, p) for (m, p) in gateway.ROUTE_TIERS if p.startswith("/v1/gpu")}
check(f"GPU 路由逐条登记(实测 {sorted(_gpu_routes)})",
      _gpu_routes == _EXPECTED_GPU_ROUTES,
      f"多出 {sorted(_gpu_routes - _EXPECTED_GPU_ROUTES)} 少了 {sorted(_EXPECTED_GPU_ROUTES - _gpu_routes)}")
# ★ S5 时这条写的是"变更端点只有一个";S9 加了 POST /v1/gpu/intended,于是它被**有意地**
#   改成逐条列名 —— 数量断言只拦得住"多了几个",拦不住"换了一个"。改成集合相等更严,不更松。
check("★ 变更端点逐条列名(每多一个都该是一次有意的决定)",
      {p for m, p in _gpu_routes if m != "GET"} == {"/v1/gpu/lease", "/v1/gpu/intended"},
      f'{sorted(p for m, p in _gpu_routes if m != "GET")}')

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

    async def unload(self, ids):
        self.calls.append(("unload", list(ids)))

    async def load(self, ids):
        self.calls.append(("load", list(ids)))
        if self.fail_rollback and len([c for c in self.calls if c[0] == "load"]) > 1:
            raise RuntimeError("回滚也装不上")
        if set(ids) & self.fail_load:
            raise RuntimeError("装载失败")


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

    # ⑧ blocking_set:有人在等 → 交还用户裁定,不动 committed
    gpu_broker.DRAIN_WINDOW_S = 0.01
    b8 = _mkbroker(free=64.0, loader=_FakeLoader())
    b8._committed = [_small]
    await b8.grant("client_session", "h1", [_small], ttl_s=30.0)
    r8 = await b8.apply_intended([])
    o["blocked"] = (r8.ok, r8.code, len(r8.blocking), list(b8._committed))
    b8._state = gpu_broker.STATE_READY
    r8b = await b8.apply_intended([_small], interrupt_running=True)
    o["interrupt"] = (r8b.ok, r8b.state)
    gpu_broker.DRAIN_WINDOW_S = 5.0

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
check("★ 有任务在跑 → needs_user_choice,并点名是哪几条租约",
      _x["blocked"][1] == "needs_user_choice" and _x["blocked"][2] == 1, _x["blocked"])
check("★ 交还用户裁定时 committed 一字未动", _x["blocked"][3] == [_small])
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
    _r_ok_path = _c.post("/v1/gpu/intended",
                         json={"if_generation": _c.get("/v1/gpu/snapshot").json()["generation"],
                               "components": ["speech.lite"]})
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
_counted = re.findall(r"—— 已踩 (\d+) 次", _doc)
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

print(f"\n=== GPU Broker 骨架:{_pass} PASS · {_fail} FAIL ===")
sys.exit(1 if _fail else 0)
