"""P4-S13 · 内网同步(D86)。跑:python test_sync.py

★★★ 这一套守的第一件事是**不可撤销的错误**:
    把**个人待办 / 未共享会话**推到另一台机器上 —— 一旦发生,数据已经在对方硬盘上了,
    删也删不干净(对方可能已经看过、已经备份)。
  ⇒ 范围判据必须在**服务端**。客户端可能有 bug,而这类错误没有撤销键。

★★ 第二件是 D86 裁定③:**覆盖也是一种失败,得看得见**。
    静默丢 = 用户在副机上写的备注凭空消失,而他永远不会知道。
"""
import inspect
import pathlib
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
import tempfile
import warnings

warnings.filterwarnings("ignore")

import assert_helpers
import gateway
import gpu_policy
import sync_policy
import sync_store
from starlette.testclient import TestClient

_pass = _fail = 0


def check(name, cond, extra=""):
    global _pass, _fail
    if cond:
        _pass += 1
    else:
        _fail += 1
        print(f"  FAIL  {name} {extra}")


def _fresh():
    sync_store.STORE = sync_store.SyncStore(pathlib.Path(tempfile.mkdtemp(prefix="synct-")))
    return sync_store.STORE


print("=== 1. ★★★ 范围判据在【服务端】—— 这类错误没有撤销键 ===")
st = _fresh()
check("家庭待办收", st.put("todos", {"id": "t1", "scope": "家庭", "title": "买菜"}, "A")["ok"])
r = st.put("todos", {"id": "t2", "scope": "个人", "title": "私事"}, "A")
check("★★★ 个人待办【拒收】—— 收了就是把私人东西送到另一台机器上", not r["ok"])
# ★ 用 .get:判据被绕过时应当**红**,不该 KeyError 崩掉 ——
# 崩溃虽然也是信号,但形状不对(看不出是哪条断言没守住)。
check("★ 拒收说得出理由(不是静默丢)",
      "个人" in r.get("message", "") and "D52" in r.get("message", ""), r.get("message"))
check("★ 拒收的 code 是 out_of_scope,不是笼统的 error", r.get("code") == "out_of_scope")
check("未共享会话拒收", not st.put("sessions", {"id": "s1", "shared": False}, "A")["ok"])
check("已共享会话收", st.put("sessions", {"id": "s2", "shared": True}, "A")["ok"])
check("★ 孤儿消息拒收(所属会话不在共享里)",
      not st.put("messages", {"id": "m1", "session_id": "nope"}, "A")["ok"])
check("共享会话的消息收", st.put("messages", {"id": "m2", "session_id": "s2"}, "B")["ok"])
check("★★ 快照里确实一条个人的都没有",
      all(x.get("scope") == "家庭" for x in st.snapshot()["data"]["todos"]))
_sc = assert_helpers.code_only(sync_store.SyncStore.in_scope)
check("★ 范围判据是【纯函数】,不依赖请求里的任何自报字段(客户端说了不算)",
      "request" not in _sc and "header" not in _sc)
check("★ 未登记的集合也拒收(反向全表:加一个新 kind 默认落在拒收侧)",
      not st.put("brand_new_kind", {"id": "x"}, "A")["ok"])
check("KINDS 与 SCOPE_RULES 逐条对应(少写一条规则就红)",
      set(sync_store.KINDS) == set(sync_store.SCOPE_RULES))

print("\n=== 2. ★★ 裁定③:后到的赢,但被覆盖的那一版【存起来】 ===")
st = _fresh()
st.put("todos", {"id": "t1", "scope": "家庭", "title": "买菜"}, "PC-A")
r = st.put("todos", {"id": "t1", "scope": "家庭", "title": "买菜 + 买米"}, "PC-B")
check("后到的赢", st.snapshot()["data"]["todos"][0]["title"] == "买菜 + 买米")
check("★★ 回话里如实说【被覆盖了】", r["superseded"] is True)
check("★ 并说清覆盖掉谁写的", r["superseded_from"] == "PC-A")
sup = st.superseded_for("todos", "t1")
check("★★★ 被覆盖的那一版真的还在(静默丢 = 用户写的备注凭空消失而他永远不知道)",
      len(sup) == 1 and sup[0]["record"]["title"] == "买菜", sup)
r2 = st.put("todos", {"id": "t1", "scope": "家庭", "title": "买菜 + 买米"}, "PC-A")
check("★★ 同内容重推【不算】冲突 —— 否则每次同步都留一份记录,噪声淹掉真冲突",
      r2["superseded"] is False and len(st.superseded_for("todos", "t1")) == 1)
_co = assert_helpers.code_only(sync_store._content_of)
check("★ 比内容时忽略同步元数据(rev/synced_at/device)",
      all(k in _co for k in ("rev", "synced_at", "device")))
check("★ rev 单调递增(客户端靠它做增量)",
      st.snapshot()["generation"] >= 3)

print("\n=== 3. ★★ 坏档【不当成空表】 ===")
st = _fresh()
st.put("todos", {"id": "t1", "scope": "家庭", "title": "重要"}, "A")
(pathlib.Path(st._root) / "todos.json").write_text("{ 这不是 json", encoding="utf-8")
_broke = False
try:
    sync_store.SyncStore(st._root)
except RuntimeError as e:
    _broke = "留证" in str(e)
check("★★★ 坏档 → 抛错并留证,**不当成空表** —— "
      "当空会让下一次推送把全部共享数据整个覆盖掉(一次解析失败吃掉所有人的东西)",
      _broke)
check("★ 坏档被改名留证(不是删掉)",
      any(p.name.startswith("todos.corrupt-") for p in pathlib.Path(st._root).glob("*.json")))
_sv = assert_helpers.code_only(sync_store.SyncStore._save)
check("★★ 原子写:先写临时文件再 replace(写一半断电 = 共享数据损坏,"
      "而它现在是两台机器的唯一权威)",
      "os.replace" in _sv and ".tmp" in _sv)

print("\n=== 4. ★★ 档位:两个面的档位集合必须一致 ===")
_ok, _diff = sync_policy.tiers_match_gpu()
check("★★★ 同步面与 GPU 面的档位集合逐字相同 —— "
      "新增一个档位只改一边,另一边会把它落进 DENY_ALL,"
      "那台设备莫名其妙什么都做不了且【不会有任何东西报错】",
      _ok, f"差集 {_diff}")
check("表外档位 → DENY_ALL", sync_policy.caps_for("nope").actions == frozenset())
for t, c in sync_policy.TIER_CAPS.items():
    check(f"{t} 的动作都是已登记的", set(c.actions) <= set(sync_policy.ACTIONS))
    check(f"{t} 写了为什么", len(c.why) > 20)
check("★★ lan-device **必须能写** —— 否则「副机提升为共享」永远同步不过来,"
      "而那正是 D86 要解决的原始诉求",
      "sync_write" in sync_policy.TIER_CAPS["lan-device"].actions)
check("★ unregistered-local 只读(D30 降档不断连,但写共享数据是另一个量级)",
      sync_policy.TIER_CAPS["unregistered-local"].actions == frozenset({"sync_read"}))
check("★ denied-account 全拒(§6.8;共享数据里有会话正文)",
      sync_policy.TIER_CAPS["denied-account"].actions == frozenset())
check("★ lan-edge 全拒(代理进程档、非业务档)",
      sync_policy.TIER_CAPS["lan-edge"].actions == frozenset())
_gwsrc = assert_helpers.code_only(gateway)
check("★★ reset_quota 在生产代码里没有调用点(能被业务调用的『清空额度』= 额度维没实现)",
      "sync_policy.reset_quota" not in _gwsrc)

print("\n=== 5. 参数维 + 额度维 ===")
sync_policy.reset_quota()
_d = sync_policy.check("lan-device", "sync_write", batch=99999)
check("★ 一次推太多被拒且点名 param 维", (not _d.ok) and _d.dimension == "param", _d.to_json())
sync_policy.reset_quota()
_cap = sync_policy.TIER_CAPS["lan-device"].pushes_per_min
_rs = [sync_policy.check("lan-device", "sync_write", batch=1, holder="h") for _ in range(_cap + 2)]
check(f"前 {_cap} 次放行", all(x.ok for x in _rs[:_cap]))
check("★ 超额被拒且点名 quota 维", (not _rs[_cap].ok) and _rs[_cap].dimension == "quota")
check("★★ 文案说清「不是权限不够,是太快了」", "太快了" in _rs[_cap].message)
sync_policy.reset_quota()
check("★ 读不占写额度", all(sync_policy.check("lan-device", "sync_read").ok for _ in range(50)))

print("\n=== 6. HTTP 面 ===")


def _probe(tier, patch_lan=False):
    gateway.classify_caller = (lambda r: "lan-edge") if patch_lan else (lambda r, t=tier: t)
    if patch_lan:
        gateway.resolve_lan_principal = lambda fp: {"tier": "lan-device"}
    sync_policy.reset_quota()
    return {"x-localai-cert-sha256": "aa"} if patch_lan else {}


_cc_saved, _lan_saved = gateway.classify_caller, gateway.resolve_lan_principal
try:
    _fresh()
    h = _probe(None, patch_lan=True)
    with TestClient(gateway.app, client=("127.0.0.1", 5555)) as c:
        r = c.post("/v1/sync/push", headers=h, json={"device": "PC-B", "items": [
            {"kind": "sessions", "record": {"id": "s1", "shared": True}},
            {"kind": "messages", "record": {"id": "m1", "session_id": "s1", "text": "hi"}},
            {"kind": "todos", "record": {"id": "t1", "scope": "家庭"}},
            {"kind": "todos", "record": {"id": "t2", "scope": "个人"}},
        ]})
        j = r.json() if r.status_code == 200 else {"results": [], "accepted": -1}
        check("推送 200", r.status_code == 200, r.status_code)
        check("★★ 逐条回结果(一批里有的收有的拒,合成一个布尔会让客户端不知道哪条没上去)",
              len(j["results"]) == 4 and j["accepted"] == 3, j.get("accepted"))
        check("★ 被拒那条说得出理由", any("个人" in x.get("message", "") for x in j["results"]))
        _src = assert_helpers.code_only(gateway.sync_push)
        check("★★ 先收 sessions 再收 messages —— 顺序反了的话,"
              "同一批里「新共享的会话 + 它的消息」会因为会话还没进表而被拒",
              "sessions" in _src and "order" in _src)
        check("★ 缺 items 不当成空推(与 /v1/gpu/intended 同一条)",
              c.post("/v1/sync/push", headers=h, json={"device": "x"}).status_code == 400)
    for t, lan, rd, wr in (("trusted-local", False, 200, 200),
                           ("unregistered-local", False, 200, 403),
                           ("denied-account", False, 403, 403),
                           ("lan-edge", False, 403, 403),
                           ("remote-unauthenticated", False, 401, 401)):
        hh = _probe(t, patch_lan=lan)
        with TestClient(gateway.app, client=("127.0.0.1", 5555)) as c:
            a = c.get("/v1/sync/snapshot", headers=hh).status_code
            b = c.post("/v1/sync/push", headers=hh, json={"device": "x", "items": [
                {"kind": "todos", "record": {"id": "z", "scope": "家庭"}}]}).status_code
            check(f"{t} 读={rd} 写={wr}", a == rd and b == wr, f"实得 读={a} 写={b}")
finally:
    gateway.classify_caller, gateway.resolve_lan_principal = _cc_saved, _lan_saved

print("\n=== 7. ★ SSE 的判据只能走真 HTTP(见 ASSERTION-PITFALLS 第 6 条)===")
# ★★ 这里【故意不用 TestClient 测推送】:每个 TestClient 各起一个事件循环,
#   asyncio.Event 跨循环叫不醒 —— 2026-08-05 那次差点让我以为"推送设计有问题",
#   而实际是测法制造的假象。⇒ 只做结构断言;实时性由真 HTTP 手测(实测 24ms)。
_ev = assert_helpers.code_only(gateway.sync_events)
check("★ 连上先给全量(重连即对齐,不必先问一次)", "snapshot" in _ev)
check("★★ 有心跳 —— 没有它,一条【死掉】的长连接与「一直没变化」长得一模一样",
      "heartbeat" in _ev and "15" in _ev)
check("★ 超时要把自己从等待队列摘掉(否则无界增长)", "remove(ev)" in _ev)
check("★★ 推送流崩了要【说出来】(静默断开会被客户端当成「没有变化」)",
      "event: error" in _ev)
_nt = assert_helpers.code_only(gateway._sync_notify)
check("★ 写完就叫,不攒批 —— 攒批就不实时了(D86 裁定②)", "set()" in _nt)

print("\n=== ★★★ 删除 = 墓碑(2026-08-05 用户实测「删除时还是没法同步删除」)===")
#  没有删除语义时,「连上就对齐」会把对方删掉的东西**推回去**:
#  A 删了 → B 开机不知情 → B 把本地那份又推上来 → A 那边复活。
_ts = sync_store.SyncStore(root=tempfile.mkdtemp(prefix="tomb_"))
_r = _ts.put("todos", {"id": "t1", "scope": "家庭", "title": "买菜"}, "PC-A")
check("先有一条共享待办", _r["ok"])
_d = _ts.put("todos", {"id": "t1", "deleted": True}, "PC-A")
check("★★ 删除被收下(墓碑,不是把行去掉)", _d["ok"], _d)
_snap = _ts.snapshot()
_rec = {r["id"]: r for r in _snap["data"]["todos"]}
check("★★★ 墓碑仍然在快照里 —— 它必须传得出去,否则另一台永远不知道这条被删了",
      "t1" in _rec and _rec["t1"].get("deleted") is True, _rec.get("t1"))
check("★ 且 rev 涨了(增量拉取也能拿到它)", int(_rec["t1"]["rev"]) > int(_r["rev"]))

# ★ 只能删已经共享过的 —— 否则一条伪造的墓碑能凭空在别人机器上删东西,
#   而且墓碑不带 scope,等于绕过范围闸。
_bad = _ts.put("todos", {"id": "从来没有过的", "deleted": True}, "PC-B")
check("★★★ 删一条库里没有的 ⇒ 拒(fail-closed:认不出来源的删除不收)",
      not _bad["ok"] and _bad.get("code") == "out_of_scope", _bad)

# ★ 会话删了之后,它的消息也不再收 —— 否则另一台的对齐会把整段消息推回来,成孤儿
_ts.put("sessions", {"id": "s1", "shared": True, "title": "家庭计划"}, "PC-A")
_m1 = _ts.put("messages", {"id": "m1", "session_id": "s1", "text": "在"}, "PC-A")
check("会话活着时消息能收", _m1["ok"])
_ts.put("sessions", {"id": "s1", "deleted": True}, "PC-A")
_m2 = _ts.put("messages", {"id": "m2", "session_id": "s1", "text": "还在?"}, "PC-B")
check("★★ 会话已删 ⇒ 它的消息不再收(否则留下孤儿消息)",
      not _m2["ok"], _m2)

print("\n=== ★★ 幂等重推不许改写「谁写的」(2026-08-05 实测)===")
#  「连上就对齐」会让每台机器把它吸收到的东西再以自己的名义推一遍,
#  于是所有记录的 device 都变成最后上线的那台 —— 而界面上
#  「这条被另一台改过」正是靠 device 判的,属性一漂提示就开始指错人。
_as = sync_store.SyncStore(root=tempfile.mkdtemp(prefix="attr_"))
_as.put("todos", {"id": "a1", "scope": "家庭", "title": "副机写的"}, "PC-B")
_as.put("todos", {"id": "a1", "scope": "家庭", "title": "副机写的"}, "PC-A")   # 内容一样,只是对齐
check("★★★ 内容没变的重推**不改**作者(否则对齐一次全变成自己写的)",
      _as.snapshot()["data"]["todos"][0]["device"] == "PC-B",
      _as.snapshot()["data"]["todos"][0]["device"])
_as.put("todos", {"id": "a1", "scope": "家庭", "title": "主机改了"}, "PC-A")   # 真改了内容
check("★ 内容真变了才换作者", _as.snapshot()["data"]["todos"][0]["device"] == "PC-A")

print("\n=== ★★ 跨端对拍:客户端的单批上限必须与这边的 max_batch 一致 ===")
#  2026-08-05 实机修复带出来的:客户端原来把整个待推队列**一次**推上来,
#  而这边 max_batch=200,超了**整批**拒(denied_param)。被拒的那批一条都不出队
#  ⇒ 客户端永远重推、永远失败,表现为"同步一直在转但什么都没发生"。
#  ★ 两边各写一个数字,迟早会漂 —— 所以在这里对拍,让它漂的那天有人红。
_client = (pathlib.Path(__file__).resolve().parents[2]
           / "20-client-win" / "app" / "Services" / "SyncClient.cs")
if _client.exists():
    _cs = _client.read_text(encoding="utf-8")
    _m = re.search(r"MaxPerPush\s*=\s*(\d+)", _cs)
    check("★ 客户端确实声明了单批上限(找不到就是判据空转)", _m is not None)
    if _m:
        _srv = {t: c.max_batch for t, c in sync_policy.TIER_CAPS.items()
                if "sync_write" in c.actions}
        check(f"★★★ 客户端 MaxPerPush={_m.group(1)} 必须等于服务端能写的档位的 max_batch {sorted(set(_srv.values()))}",
              all(int(_m.group(1)) == v for v in _srv.values()) and len(_srv) > 0,
              f"服务端逐档:{_srv}")
    check("★ 客户端确实**切了批**(不切的话上限形同虚设)", "Take(MaxPerPush)" in _cs)
    check("★★ 客户端连上要【对齐】而不只是补队列 —— "
          "队列是纯内存的,关一次 App 那些数据就永远上不去(实机实测:中枢存档里两台真机 0 条记录)",
          "ReconcileAsync" in _cs and "FullSet" in _cs)

print(f"\n=== 内网同步:{_pass} PASS · {_fail} FAIL ===")
sys.exit(1 if _fail else 0)
