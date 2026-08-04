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


def _lock_bodies(src):
    """精确取出每个 `async with self._lock:` 的【缩进块】。

    ★ 原来这里是 `(.*?)(?=\n    def )` —— 一路捕到下一个方法定义。
      S8 之前每个锁块恰好是方法的最后一段,所以看着对;S8 的 apply_intended 里
      锁块后面还有【缩进已经退回去】的代码,那段被误算进锁内 —— 三条全红。
      修法是**收紧**判据(只取缩进更深的行),不是把断言删掉。
    """
    lines, out = src.split("\n"), []
    for i, ln in enumerate(lines):
        m = re.match(r"^(\s*)async with self\._lock:\s*$", ln)
        if not m:
            continue
        ind, body = len(m.group(1)), []
        for nxt in lines[i + 1:]:
            if nxt.strip() == "" or len(nxt) - len(nxt.lstrip()) > ind:
                body.append(nxt)
            else:
                break
        out.append("\n".join(body))
    return out


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

print(f"\n=== GPU Broker 骨架:{_pass} PASS · {_fail} FAIL ===")
sys.exit(1 if _fail else 0)
