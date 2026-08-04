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
import sys
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

print(f"\n=== 内网同步:{_pass} PASS · {_fail} FAIL ===")
sys.exit(1 if _fail else 0)
