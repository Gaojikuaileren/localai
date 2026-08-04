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
    ("GET", "/v1/gpu/snapshot"),   # S3:只读快照
    ("GET", "/v1/gpu/events"),     # S5:推送流(SSE)
    ("POST", "/v1/gpu/lease"),     # S5:★ 第一个变更端点
}
_gpu_routes = {(m, p) for (m, p) in gateway.ROUTE_TIERS if p.startswith("/v1/gpu")}
check(f"GPU 路由逐条登记(实测 {sorted(_gpu_routes)})",
      _gpu_routes == _EXPECTED_GPU_ROUTES,
      f"多出 {sorted(_gpu_routes - _EXPECTED_GPU_ROUTES)} 少了 {sorted(_EXPECTED_GPU_ROUTES - _gpu_routes)}")
check("★ 变更端点只有一个(每多一个都该是一次有意的决定)",
      len([1 for m, _ in _gpu_routes if m != "GET"]) == 1)

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
for m in re.finditer(r"async with self\._lock:(.*?)(?=\n    (?:async )?def |\Z)", _lock_src, re.S):
    seg = m.group(1)
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

print(f"\n=== GPU Broker 骨架:{_pass} PASS · {_fail} FAIL ===")
sys.exit(1 if _fail else 0)
