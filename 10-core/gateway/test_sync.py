"""P4-S13 · 内网同步(D86)。跑:python test_sync.py

★★★ 这一套守的第一件事是**不可撤销的错误**:
    把**个人待办 / 未共享会话**推到另一台机器上 —— 一旦发生,数据已经在对方硬盘上了,
    删也删不干净(对方可能已经看过、已经备份)。
  ⇒ 范围判据必须在**服务端**。客户端可能有 bug,而这类错误没有撤销键。

★★ 第二件是 D86 裁定③:**覆盖也是一种失败,得看得见**。
    静默丢 = 用户在副机上写的备注凭空消失,而他永远不会知道。
"""
import inspect
import json
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
        # ★ 桩要长得像真的:真实现返回 device_id,而 device 归因与额度桶都靠它(审计 C1)。
        gateway.resolve_lan_principal = lambda fp: {"tier": "lan-device", "device_id": "DEV-" + fp[:6]}
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

print("\n=== ★★★ 在线状态(审计 C2:注册在生成器之外会永久留人 + 零断言)===")
# ★★ 上一版把注册写在 sync_events 的**函数体**里、摘除写在 gen() 的 finally 里。
#   客户端建连后立刻断开时,Starlette 会取消 stream_response ——
#   **异步生成器从未启动,finally 就不会执行** ⇒ 那条记录永远留在名单里,
#   而 key 是个 object(),再也够不着。后果恰好是这段代码自己说的最坏那个:
#   **在线状态错了比没有更坏**(用户会以为东西已经同步过去了)。
_se = assert_helpers.code_only(gateway.sync_events)
_i_reg = _se.find("_sync_online[_me]")
_i_gen = _se.find("async def gen()")
check("★ 两处都找得到(位置判据的前提)", _i_reg >= 0 and _i_gen >= 0, f"reg={_i_reg} gen={_i_gen}")
check("★★★ 注册必须在 gen() **里面** —— 写在外面时,连上就断的客户端会永久留在名单里",
      _i_reg > _i_gen, f"reg={_i_reg} gen={_i_gen}")
check("★★ 摘除在 finally(无论怎么走到那儿都要摘)", "finally" in _se and "_sync_online.pop" in _se)
check("★★ 名单变了也要推 —— 「对方掉线了」没有任何数据变化伴随,不单独判就永远推不出去",
      "now_online != seen_online" in _se)

# ★★★ 审计 C1:device 一律由服务端解析,客户端自报不作数。
#   它同时是「谁写的」的归因来源、和额度维令牌桶的 key ——
#   自报的话每换一个名字就是一个新桶,pushes_per_min 形同虚设。
# ══════════════════════════════════════════════════════════════════════
#  ★★★ 2026-08-06 审计 B3:身份解析**合一**了 —— 这两条断言随之改判据。
#
#  原来它们 `code_only(gateway._sync_device)` 去看那个函数的**源码里有没有那两个词**。
#  合一之后 `_sync_device` 只剩一行转发(唯一实现搬到 `principal_device`),
#  于是断言当场变红 —— **而同步面的行为一个字节都没变**。
#  ⇒ 这正是 ASSERTION-PITFALLS 第 9 条的形状:判据问的是"这段代码长什么样",
#    而它真正关心的是"名字到底从哪儿来"。改成两条:
#      ① 结构:唯一入口是 principal_device,且同步面确实走它(不是又抄了一份);
#      ② **行为**:构造一个自报了别人名字的请求,看解析结果是不是那个自报值。
#    第二条是承重的 —— 实现再搬一次家,它照样成立。
# ══════════════════════════════════════════════════════════════════════
_pd = assert_helpers.code_only(gateway.principal_device)
check("★★★ 名字来自证书指纹反查的成员表,不是 body/query 的自报值",
      "x-localai-cert-sha256" in _pd and "resolve_lan_principal" in _pd)
check("★★ 解析不出身份 ⇒ 给 unknown(fail-closed),**不**退回自报值",
      "UNKNOWN_DEVICE" in _pd and gateway.UNKNOWN_DEVICE == "unknown")
_sd = assert_helpers.code_only(gateway._sync_device)
check("★★★ B3:同步面**不再有自己的实现**,只转发给唯一入口 —— "
      "两套身份各自自洽所以谁也不炸,而 /v1/session/end 恰好横跨两者",
      "principal_device" in _sd and "resolve_lan_principal" not in _sd, _sd)


class _FakeReq:
    """只带 headers 的最小请求。★ 判据要落在**行为**上,不能只看源码里有哪些词。"""

    def __init__(self, headers=None):
        self.headers = headers or {}
        self.client = None


_saved_lan = gateway.resolve_lan_principal
try:
    gateway.resolve_lan_principal = lambda fp: {"tier": "lan-device", "device_id": "PC-REAL"}
    _r = _FakeReq({"x-localai-cert-sha256": "aa"})
    check("★★★ 行为:自报名字进不来 —— 解析结果是成员表给的那个,与 body 无关",
          gateway._sync_device(_r, "lan-device") == "PC-REAL"
          and gateway.principal_device(_r, "lan-device") == "PC-REAL")
    gateway.resolve_lan_principal = lambda fp: None
    check("★★ 行为:指纹解不出成员 ⇒ unknown(fail-closed),不退回任何自报值",
          gateway._sync_device(_FakeReq({"x-localai-cert-sha256": "zz"}), "lan-device")
          == gateway.UNKNOWN_DEVICE)
    check("★ 行为:回环(主机本身)给固定名字 local",
          gateway._sync_device(_FakeReq(), "trusted-local") == gateway.LOCAL_DEVICE)
finally:
    gateway.resolve_lan_principal = _saved_lan
_push_src = assert_helpers.code_only(gateway.sync_push)
check("★★★ 推送路径也不再采信 body 里的 device(它是额度桶的 key)",
      "_sync_device(" in _push_src and 'body.get("device")' not in _push_src)

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


# ══════════════════════════════════════════════════════════════════════
#  ★★★ V15 · D? ①:范围判据在【读】这一侧也过一遍
#
#  D86 裁定①的原话是"只收家庭/共享的"。它此前**只落在 `put()`** ——
#  于是「只同步家庭/共享的」在**拉**的方向上是一句推论,不是判据:
#  库里已经躺着的东西,发出去之前没有任何人再问一句。
#
#  ★ 这不是理论风险。一条今天就能走到的路径:共享会话被删(墓碑)之后,
#    `in_scope` 明写不再收它的消息(免得留孤儿),**但已经在库里的那些照发不误** ——
#    会话都没了,正文还在一帧一帧往另一台机器上走。
#  ★★ 而这类错误**没有撤销键**:数据一旦到了对方硬盘上,删也删不干净。
# ══════════════════════════════════════════════════════════════════════
print("\n=== ★★★ V15:范围判据在【读】这一侧也过一遍(拉的方向) ===")

_rs = sync_store.SyncStore(root=tempfile.mkdtemp(prefix="readgate_"))
_rs.put("todos", {"id": "ok1", "scope": "家庭", "title": "买菜"}, "PC-A")

# ── ① 反向注入:绕开 put 直接把一条个人待办放进库里 ──────────────────
#  ★ 为什么必须注入:`put` 今天就拦得住它 ⇒ 光靠正常路径,读侧这道闸
#    **永远不会被走到**,那条断言就是恒绿的(给一条没人走的路配断言)。
#    库里出现表外记录的真实来路:老版本写进去的、手改过的存档、
#    以及下面 ③ 那条**今天就能走到**的。
_rs._cache["todos"]["leak"] = {"id": "leak", "scope": "个人", "title": "私事", "rev": 99}
check("★ 元断言:那条个人待办**确实**被塞进库里了(塞不进去 ⇒ 下一条空转)",
      _rs._cache["todos"].get("leak") is not None)
_snap = _rs.snapshot()
_ids = {r["id"] for r in _snap["data"]["todos"]}
check("★★★ 读侧把它**扣下**了 —— 只在写那侧过闸的话,库里已有的东西发出去前没人再问一句",
      "leak" not in _ids, sorted(_ids))
check("★ 反向:合格的那条**没有**被误扣(读侧闸不是『一律不发』)", "ok1" in _ids, sorted(_ids))
_wh = _rs.withheld()
check("★★★ 扣下**不是静默的**:留了账,而且说得出理由 —— "
      "从副机看,『扣下』和『丢了』长得一模一样(它就是少一条)",
      ("todos", "leak") in _wh and "个人" in _wh[("todos", "leak")], _wh)
check("★ 留证文件也落了盘(体检/事后查账用的那一份)",
      (pathlib.Path(_rs._root) / "withheld" / "todos-leak.json").exists())

# ── ② counts 报的是**真会发出去的**条数,不是库里有几条 ─────────────
check("★★ counts 不广告拿不到的数字 —— 报库里的数会让『counts 2 / data 1』这种差额"
      "变成一种没人在看的少给",
      _snap["counts"]["todos"] == len(_snap["data"]["todos"]) == 1, _snap["counts"])

# ── ③ 会话删了之后,它**已经在库里**的消息不再发出去(不用注入,今天就能走到)──
_rs.put("sessions", {"id": "s9", "shared": True, "title": "家庭计划"}, "PC-A")
_rs.put("messages", {"id": "m9", "session_id": "s9", "text": "正文"}, "PC-A")
check("★ 元断言:删之前那条消息**确实在**快照里(不在的话下一条测的是空气)",
      any(r["id"] == "m9" for r in _rs.snapshot()["data"]["messages"]))
_rs.put("sessions", {"id": "s9", "deleted": True}, "PC-A")
_snap2 = _rs.snapshot()
check("★★★ 会话删掉之后,它那些**已经在库里**的消息不再发出去 —— "
      "此前『不再收』只挡住了新的,旧的照发:会话都没了,正文还在往另一台机器上走",
      not any(r["id"] == "m9" for r in _snap2["data"]["messages"]),
      [r["id"] for r in _snap2["data"]["messages"]])
check("★★★ 而会话**墓碑本身仍然发得出去** —— 它要是也被扣下,"
      "另一台就永远不知道这条被删了,删除又变成删不掉",
      any(r["id"] == "s9" and r.get("deleted") for r in _snap2["data"]["sessions"]),
      _snap2["data"]["sessions"])

# ── ④ 两侧必须是**同一个**判据,不是各写一份 ───────────────────────
_snap_src = assert_helpers.code_only(sync_store.SyncStore.snapshot)
check("★★★ 读侧走的是**同一个** in_scope —— 另写一份的话两份判据会漂,"
      "而漂的那天写这侧是绿的、读这侧在漏,没有任何东西会红",
      "in_scope" in _snap_src, _snap_src[:0])
check("★★ 扣下的那条要留账(判据落在源码上:行为断言在上面 ①,这条防的是把留账删掉)",
      "_note_withheld" in _snap_src)


# ══════════════════════════════════════════════════════════════════════
#  ★★★ V15 · D? ②:`/v1/sync/snapshot` 的客户端消费者在【上线/重连】那条路上
#
#  V6 把这条路由接上了,但**只接在"帧内断层"那一处**,理由是"重连那条路本来
#  就没洞 —— `/v1/sync/events` 首帧就是全量"。那句话对了一半:
#  **服务端首帧确实是全量**,但「首帧是全量」≠「客户端拿到了全量」——
#  中间隔着"流没建起来""首帧没吃下"两格,而那两格的表现完全一样:
#  连接活着、generation 有值、界面说「已同步」,而这台机器上一条共享数据都没有。
#  ⇒ 用户 2026-08-07 裁:**上线拉一次,断线重连也拉**。
#
#  ★ 下面这组钉的是**它真的在那条路上**。少了这组,客户端可以把调用点挪回帧里,
#    而 `CONTRACT:sync.snapshot` 那条成对断言**照样绿** —— 它只问"两半在不在"。
# ══════════════════════════════════════════════════════════════════════
print("\n=== ★★★ V15:拉全量挂在【上线/重连】那条路上(不是只挂在帧上)===")


def _cs_code_only(src: str) -> str:
    """去掉 C# 的 // 与 /* */ 注释,并把字符串字面量整体换成 ""。

    ★ 必须去注释再判:本文件下面几条是**位置/嵌套**判据,而注释里就写着
      `if (line.StartsWith("data: "))` 这样的字样和成对的括号 ——
      不去注释的话,判据会命中一段注释,然后报出一个**假的**绿或红。
      (ASSERTION-PITFALLS 第 1 条的同款用法;那边怕注释弄红,这边怕注释弄绿。)
    """
    out, i, n = [], 0, len(src)
    while i < n:
        if src[i:i + 2] == "//":
            while i < n and src[i] != "\n":
                i += 1
            out.append("\n")
            continue
        if src[i:i + 2] == "/*":
            i += 2
            while i + 1 < n and src[i:i + 2] != "*/":
                i += 1
            i += 2
            continue
        if src[i] == '"':
            verbatim = i > 0 and src[i - 1] == "@"
            i += 1
            while i < n:
                if verbatim:
                    if src[i] == '"':
                        if src[i + 1:i + 2] == '"':
                            i += 1
                        else:
                            break
                elif src[i] == "\\":
                    i += 1
                elif src[i] == '"':
                    break
                i += 1
            i += 1
            out.append('""')
            continue
        out.append(src[i])
        i += 1
    return "".join(out)


def _block_end(s: str, i: int) -> int:
    """从 i 往后找第一个 `{`,返回与它配对的 `}` 的下标(找不到回 -1)。"""
    j = s.find("{", i)
    if j < 0:
        return -1
    d = 0
    while j < len(s):
        if s[j] == "{":
            d += 1
        elif s[j] == "}":
            d -= 1
            if d == 0:
                return j
        j += 1
    return -1


check("★★★ 能读到客户端源码(读不到 ⇒ 下面整组跨端断言**静默消失** ⇒ 判红,"
      "不当作没问题 —— 「查不了」不等于「没问题」)",
      _client.exists(), str(_client))
if _client.exists():
    _c = _cs_code_only(_cs)
    check("★ 元断言:去注释器没把整份文件吃掉(吃掉的话下面整组静默变成零断言)",
          len(_c) > len(_cs) * 0.3, f"{len(_c)}/{len(_cs)}")

    # ── ① 这条路由**真有**客户端消费者,而且拉的是全量 ──────────────
    check("★★ 客户端确实有 PullFullAsync(找不到 = 这条路由又变回没人走的路)",
          "PullFullAsync" in _c)
    check("★★★ 它打的就是 GET /v1/sync/snapshot(路径写在源码里,不是靠注释声称)",
          '"/v1/sync/snapshot"' in _cs)
    check("★★ 拉回来喂的是**同一个** Absorb —— 两份解析会漂,"
          "而漂的那天自检只盯着其中一份",
          "Absorb(text)" in _c)

    # ── ② 调用点在**开流之前**(上线/重连都走它)────────────────────
    #  ★ 锚点**只在 RunAsync 的方法体里**找,不扫整份文件 —— 与下面 ③ 同一个教训:
    #    判词说的是"在 RunAsync 里、开流之前",判据就不该去别的方法里碰运气。
    _i_run = _c.find("async Task RunAsync(")
    _run = _c[_i_run:_block_end(_c, _i_run) + 1] if _i_run >= 0 else ""
    check("★ 元断言:取到了 RunAsync 的方法体(取不到 ⇒ 下面几条是零断言)",
          len(_run) > 200 and "PullFullAsync" in _run, len(_run))
    _i_pull = _run.find("PullFullAsync(")
    _i_open = _run.find("Transport.OpenStream")
    check("★ 元断言:两个锚点都找得到(位置判据的前提;零命中判红)",
          _i_pull >= 0 and _i_open >= 0, f"pull={_i_pull} open={_i_open}")
    check("★★★ 拉全量在**开流之前** —— 这一格正是首帧永远盖不到的那一格:"
          "流建不起来时,平的 GET 是唯一还能跑的东西",
          0 <= _i_pull < _i_open, f"pull={_i_pull} open={_i_open}")
    check("★★★ 而且它在**重连循环里面**(不是只在 Start 里跑一次)—— "
          "只在开机跑一次的话,断线期间丢的更新靠推送永远补不回来",
          "while (!ct.IsCancellationRequested)" in _run
          and _run.find("while (!ct.IsCancellationRequested)") < _i_pull)

    # ══════════════════════════════════════════════════════════════════
    #  ── ②b 开流之前那一步**必须带期限**(V15 收工前对抗式复核抓到的自伤)──
    #
    #  这一步是 await 在 OpenStream **之前**的,而它底下走的 `Transport.Send`
    #  用的 HttpClient **没有设 Timeout** ⇒ 吃 .NET 默认的 **100 秒**
    #  (对照:同一份传输层里 OpenStream 显式设了 InfiniteTimeSpan ——
    #   也就是说"设不设超时"在那儿本来就是逐处决定的,不是漏了一个全局值)。
    #  ⇒ 「TCP 接得上、对端不答话」时,每一轮重连都要先耗掉最多 100 秒才轮到开流,
    #    这期间这台机器既不是 Live、也不在中枢的在线名单里 ——
    #    **那正好是实机记的那句「不是启动即连」**,而且是 V15 自己引进来的:
    #    改之前 PullFullAsync 只被 Task.Run 甩出去,卡多久都拦不住任何东西。
    # ══════════════════════════════════════════════════════════════════
    check("★★★ 开流之前那次对齐**带期限**(CancelAfter),而且期限设在开流之前 —— "
          "不带的话,一台『接得上但不答话』的主机能把同步流按住一分半钟",
          "CancelAfter" in _run and 0 <= _run.find("CancelAfter") < _i_open,
          f"cancelAfter={_run.find('CancelAfter')} open={_i_open}")
    _m_align = re.search(r"AlignDeadline\s*=\s*TimeSpan\.FromSeconds\((\d+)\)", _cs)
    _m_stale = re.search(r"StaleAfter\s*=\s*TimeSpan\.FromSeconds\((\d+)\)", _cs)
    check("★ 元断言:两个时限在源码里都找得到(找不到 ⇒ 下一条静默变成零断言)",
          _m_align is not None and _m_stale is not None,
          f"align={_m_align} stale={_m_stale}")
    check("★★ 对齐期限必须**小于**判活阈值 —— 对齐要是能比「判连接已死」还久,"
          "界面会在流都还没开出去的时候先说自己断了",
          bool(_m_align) and bool(_m_stale)
          and int(_m_align.group(1)) < int(_m_stale.group(1)),
          f"align={_m_align and _m_align.group(1)}s stale={_m_stale and _m_stale.group(1)}s")

    # ══════════════════════════════════════════════════════════════════
    #  ── ②c 对齐要先把 FullSet **落成一份表**再进锁 ────────────────────
    #
    #  宿主注入的 FullSet 是惰性的(SharedSnapshot 现场遍历各 Center 的 List),
    #  而 V15 把这一步挪到了**开机路上** ⇒ UI 线程此刻可能正在改同一批 List
    #  ⇒ List<T> 枚举当场抛「集合已修改」。★ 更坏的是它会一路冒到 RunAsync 的 catch,
    #    被判成 Reconnecting、界面显示「与中枢的同步连接断了」——
    #    **一件本地的事被报成了网络的事**,而人会照着那句话去查网络。
    #  ⇒ 失败要长得和成功不一样,但也不能长得像**另一种**失败。
    # ══════════════════════════════════════════════════════════════════
    _rec = _c[_c.find("public async Task ReconcileAsync"):]
    _rec = _rec[:_rec.find("public async Task<SyncPushResult?> FlushAsync")] if "FlushAsync" in _rec else _rec
    check("★ 元断言:取到了 ReconcileAsync 的源码", len(_rec) > 200, len(_rec))
    check("★★★ FullSet 先落成一份表再进锁,且枚举失败就地接住 —— "
          "不接的话,一次本地列表变动会被报成「与中枢的连接断了」",
          "FullSet?.Invoke()?.ToList()" in _rec and "InvalidOperationException" in _rec,
          _rec[:0])
    check("★ 而且**不是** catch-all —— 宽到 catch-all 会把宿主真正的 bug 一起吞掉",
          "catch (InvalidOperationException" in _rec and "catch {" not in _rec)

    # ══════════════════════════════════════════════════════════════════
    #  ── ③ 补全量的触发**不在** data 分支里(心跳也要能救回来)────────
    #
    #  ★★★ 这条判据的第一版**红测时没红**,而它暴露的是判据本身的洞
    #    (与 V6 那次 V2 同一族,记在这儿):
    #      第一版写的是 `_c.rfind("if (_needFullPull)") > data 分支的收尾`。
    #      红测往 data 分支**里面**加了一处 ⇒ 外面那处还在 ⇒ `rfind` 找到外面那个 ⇒ **绿**。
    #    那次绿其实是对的(多一处不改变行为),但同一个判据有一个**真的**假绿:
    #      `rfind` 扫的是**整份文件**。哪天有人在 `PullFullAsync` 里也写一句
    #      `if (_needFullPull)`,而 OnLine 里那处被挪进了 data 分支 ——
    #      `rfind` 会找到文件末尾那个,判据照样绿,而洞已经回来了。
    #  ⇒ 收紧两处:① 只在 **OnLine 的方法体**里找;② 看**全部**出现位置,
    #    要求"至少有一处在 data 分支外面",而不是"最后一处在外面"。
    # ══════════════════════════════════════════════════════════════════
    _i_online = _c.find("Task OnLine(string line)")
    _online = _c[_i_online:_block_end(_c, _i_online) + 1] if _i_online >= 0 else ""
    check("★ 元断言:取到了 OnLine 的方法体(取不到 ⇒ 下面两条是零断言)",
          len(_online) > 200 and "_needFullPull" in _online, len(_online))
    _i_data = _online.find("if (line.StartsWith(")
    _end_data = _block_end(_online, _i_data) if _i_data >= 0 else -1
    _occ = [_m.start() for _m in re.finditer(r"if \(_needFullPull\)", _online)]
    check("★ 元断言:三个锚点都找得到(零命中判红)",
          _i_data >= 0 and _end_data > _i_data and len(_occ) > 0,
          f"data={_i_data} end={_end_data} occ={_occ}")
    check("★★★ OnLine 里**至少有一处**补全量的触发落在 data 分支**外面** —— "
          "全都在里面的话,只有【下一帧数据】能救它;而首帧没吃下之后,"
          "下一帧数据要等到有人改东西才会来:家里没人动的那段时间,"
          "心跳照到、连接活着、界面说「已同步」,而本机一条共享数据都没有",
          bool(_occ) and any(_i > _end_data > 0 for _i in _occ),
          f"occ={_occ} data 分支结束于 {_end_data}")

    # ── ④ 全量没吃下就**不推**(先拉后推,挡的是"拉失败了"那一格)──
    check("★★★ 全量没吃下就不推 —— 先拉后推此前只挡住了『顺序反了』,"
          "没挡住『拉失败了』:Absorb 的 catch 是真的会走到的(那次 ObjectDisposedException),"
          "墓碑没落地却照样推,对方删掉的东西就在他那边复活",
          "_pullFirst && !_needFullPull" in _c)

    # ── ⑤ 欠着一次对齐时,界面上**有位置**说这件事 ──────────────────
    _status = _cs[_cs.find("public string StatusLine()"):]
    check("★ 元断言:取到了 StatusLine 的源码", len(_status) > 100)
    check("★★★ 欠着全量对齐时界面要说出来 —— 「未同步必须看得见」此前只覆盖了**推**那一侧;"
          "拉这一侧一个位置都没有,而『我这边少了对方的东西』正是副机冷启动的表现",
          "_needFullPull" in _status[:_status.find("public void Dispose")
                                     if "public void Dispose" in _status else len(_status)])


# ══════════════════════════════════════════════════════════════════════
#  ★★★ D92 硬前置:跨进程响应契约,两侧成对(sync + chat 切片 · V6)
#
#  D92:每一个跨进程响应契约必须有一条成对断言 —— 服务端钉顶层键集合、
#  客户端钉「拿这个形状能解析出目标字段」;缺配对即判红。
#
#  ★★ A1 那族缺陷的形状是**两边各自都绿,断的是中间那根线**:
#    服务端只证明"我发的是这个形状",客户端只证明"这个形状我读得懂" ——
#    任何一条单独存在都抓不住它。
#
#  ★ 表的形状照抄 `test_gpu_broker.py` 的 `CROSS_PROCESS_CONTRACTS`,
#    **故意同名同结构**:90-ops 那条广度元规则要能用同一个正则读两边
#    (今天它只读 test_gpu_broker.py —— 见本轮决议包里点名的那条)。
# ══════════════════════════════════════════════════════════════════════
print("\n=== D92 硬前置:sync / chat 切片的跨进程契约,两侧成对 ===")

#: 契约登记表。key = 契约号(客户端那半边必须原样出现这个字符串)。
CROSS_PROCESS_CONTRACTS = {
    "CONTRACT:sync.snapshot":     ("GET /v1/sync/snapshot 200",
                                   {"generation", "since_rev", "data", "counts"}),
    "CONTRACT:sync.push":         ("POST /v1/sync/push 200",
                                   {"accepted", "total", "results", "generation"}),
    "CONTRACT:sync.events.frame": ("GET /v1/sync/events 的每一帧 data:",
                                   {"generation", "since_rev", "data", "counts", "online"}),
    "CONTRACT:chat.stream.frame": ("POST /v1/chat/completions 的每一帧 data:",
                                   {"choices"}),
    "CONTRACT:models.list":       ("GET /v1/models 200",
                                   {"object", "data"}),
}

#  ★ 与本文件第 6 节同款:把 classify_caller 换成桩再打。
#    ★★ 不这么做的话每条都是 401,而 401 的响应体是 {error, detail} ——
#      键集合断言会拿"错误体"当"契约体"去比,红是红了,**红的理由却是假的**
#      (ASSERTION-PITFALLS 第 9 条:判据失败时给出的理由必须是它真的验过的那件事)。
#    ★ 用完必还(下面 finally)—— 不还的话后面每一条断言都在对着一个测试用的桩说话。
_observed = {}
_cc_saved2, _lan_saved2 = gateway.classify_caller, gateway.resolve_lan_principal
gateway.classify_caller = lambda r: "lan-edge"
gateway.resolve_lan_principal = lambda fp: {"tier": "lan-device", "device_id": "DEV-" + fp[:6]}
_H = {"x-localai-cert-sha256": "aa"}
sync_policy.reset_quota()
with TestClient(gateway.app, client=("127.0.0.1", 5555)) as _cc:
    _r_snap = _cc.get("/v1/sync/snapshot", headers=_H)
    check("★ GET /v1/sync/snapshot 200", _r_snap.status_code == 200, _r_snap.status_code)
    _observed["CONTRACT:sync.snapshot"] = set(_r_snap.json())

    _r_push = _cc.post("/v1/sync/push", headers=_H, json={"items": [
        {"kind": "todos", "record": {"id": "c1", "scope": "家庭", "title": "契约用"}}]})
    check("★ POST /v1/sync/push 200", _r_push.status_code == 200, _r_push.status_code)
    _observed["CONTRACT:sync.push"] = set(_r_push.json())

    _r_models = _cc.get("/v1/models", headers=_H)
    check("★ GET /v1/models 200", _r_models.status_code == 200, _r_models.status_code)
    _observed["CONTRACT:models.list"] = set(_r_models.json())

    # ══════════════════════════════════════════════════════════════════
    #  ★★★ V15:`CONTRACT:sync.snapshot` 那条成对断言的**复核**。
    #
    #  任务原话:接上消费者之后要核实它「仍然成立,别让它从『钉着一条没人走的路』
    #  变成『钉错了』」。逐条核:
    #    · 服务端那半边(顶层键集合)——**没变**,上面那条循环照跑;
    #    · 客户端那半边(PullFullAsync → Absorb)——**还在**,而且触发点从 1 个变成 3 个
    #      (上线/重连 · 断层 · 解析失败),上一节逐条钉过;
    #    · ★★ 但**顶层键集合不够**了。这条路由此前只在丢帧时走一次,
    #      钉住"顶层长这样"够用;现在**副机冷启动的全部共享数据都从这里来** ——
    #      `data.sessions[i]` 少一个 `id`、或者 `data` 整个空掉,顶层键集合**照样绿**,
    #      而副机开机看到的是一片空白。⇒ 把消费者真正读到的那几层一起钉。
    #  ★ 钉的是 **Absorb 真的会去取的那些字段**(见 SyncClient.Absorb):
    #    generation · since_rev · data.{sessions,todos,messages} 三个数组 · 每条的 id。
    # ══════════════════════════════════════════════════════════════════
    _r_snap2 = _cc.get("/v1/sync/snapshot", headers=_H)
    check("★ 复核用的那次 GET 也是 200", _r_snap2.status_code == 200, _r_snap2.status_code)
    _sn = _r_snap2.json() if _r_snap2.status_code == 200 else {}
    check("★★ 全量的 since_rev 是 0 —— 客户端上线拉的就是它;"
          "它一旦变成增量,冷启动的机器就只拿到『最近改过的那几条』,而那正是"
          "「看着连上了、实际少一大半」",
          _sn.get("since_rev") == 0, _sn.get("since_rev"))
    check("★★ generation 是数字(客户端拿它做断层判据的起点)",
          isinstance(_sn.get("generation"), int), _sn.get("generation"))
    _dat = _sn.get("data") if isinstance(_sn.get("data"), dict) else {}
    check("★★★ data 里**三个集合一个不少**(客户端逐个 kind 去取;"
          "少一个 = 那一类数据在副机上永远不出现,而顶层键集合照样绿)",
          set(_dat) == set(sync_store.KINDS), sorted(_dat))
    check("★★ 每个集合都是数组", all(isinstance(_dat.get(k), list) for k in sync_store.KINDS))
    _all_rows = [r for k in sync_store.KINDS for r in (_dat.get(k) or [])]
    check("★★ 元断言:这次快照里**确实有记录**(空的话下面那条逐项断言静默变成零断言)",
          len(_all_rows) > 0, _dat)
    check("★★★ 每一条都带 id —— 客户端合并靠它认同一条记录;"
          "缺了的话远端记录会被当成新记录反复插进去",
          all(isinstance(r.get("id"), str) and r["id"] for r in _all_rows),
          [r for r in _all_rows if not r.get("id")])
    check("★★ 每一条都带 rev(增量与断层判据的来源)",
          all(isinstance(r.get("rev"), int) for r in _all_rows))

#  ★ 这两条**在下面单独打**,不能进这个循环:
#    · chat 那条要注入上游(不打真模型);
#    · SSE 那条要驱动真生成器,而那段在本循环**之后** ——
#      放进来的话它会因为"还没观测到"而红,**而红的理由是假的**(顺序问题,不是缺配对)。
#      ★ 一条理由是假的红,比不红更费人:人会照着假理由去改一个没坏的东西
#        (ASSERTION-PITFALLS 第 9 条)。
_LATER = {"CONTRACT:chat.stream.frame", "CONTRACT:sync.events.frame"}
for _cid, (_what, _keys) in CROSS_PROCESS_CONTRACTS.items():
    if _cid in _LATER:
        continue
    if _cid not in _observed:
        check(f"★★ {_cid} 被观测到了(没观测到 ⇒ 下面那条键集合断言是零断言)", False, _what)
        continue
    _got = _observed[_cid]
    check(f"★★★ {_cid}({_what})顶层键集合**恰好**是登记的那一组 —— "
          "「多一个键」和「换了一个键」都要红,数量断言拦不住后者",
          _got == _keys, f"多 {sorted(_got - _keys)} 少 {sorted(_keys - _got)}")

# ══════════════════════════════════════════════════════════════════════
#  ★★ SSE 那一帧:**直接驱动真的那个异步生成器**,不用 TestClient。
#
#  ★ 为什么不用 TestClient(本文件第 7 节已经为别的理由躲开过它一次):
#    进程内传输**永远不会让 `request.is_disconnected()` 变真**,而 `gen()` 的
#    退出条件正是它 ⇒ 生成器无限循环下去、每 15 秒吐一个心跳,
#    `with` 退出时等它收尾 ⇒ **整套测试永久挂住**(实测:120 秒没有任何输出)。
#    ★ 挂住比判红更坏:运行器只看到"没有汇总行",看不出是哪条没守住。
#
#  ⇒ 造一个「第一次问说还连着、第二次问说断了」的请求:
#    生成器吐完首帧、走 `break`、进 `finally` 把自己从在线名单摘掉 —— 全程走**真代码**。
#    ★ 这不是"另建一份模型来验":`gen()` 是 gateway 里那个真的,一行没换。
# ══════════════════════════════════════════════════════════════════════
import asyncio as _aio


class _OneFrameReq:
    """问第一次:还连着。问第二次:断了。★ 让真生成器**自然收尾**,而不是被掐死。"""

    def __init__(self):
        self.headers = {"x-localai-cert-sha256": "aa"}
        self.query_params = {}
        self.client = None
        self._asked = 0

    async def is_disconnected(self):
        self._asked += 1
        return self._asked > 1


async def _first_sse_frame():
    resp = await gateway.sync_events(_OneFrameReq())
    async for chunk in resp.body_iterator:
        if "data: " in chunk:
            return json.loads(chunk.split("data: ", 1)[1].strip())
    return None


_frame = _aio.run(_first_sse_frame())
check("★★ /v1/sync/events **真的取到了一帧**(取不到 ⇒ 下面整段是零断言)",
      _frame is not None)
if _frame is not None:
    _observed["CONTRACT:sync.events.frame"] = set(_frame)
    _got = _observed["CONTRACT:sync.events.frame"]
    _want = CROSS_PROCESS_CONTRACTS["CONTRACT:sync.events.frame"][1]
    check("★★★ CONTRACT:sync.events.frame 顶层键集合**恰好**是登记的那一组",
          _got == _want, f"多 {sorted(_got - _want)} 少 {sorted(_want - _got)}")
    # ★★★ 首帧必须是**全量**:客户端的重连对齐**完全靠它**
    #   (SyncClient 的 _pullFirst 等的就是这一帧,里面带着删除的墓碑;
    #    先推后拉会让另一台删掉的东西在这边复活)。
    check("★★★ 首帧是**全量**(since_rev=0)—— 客户端的重连对齐完全建立在这条上;"
          "它一旦变成增量,重连后就再也对不齐,而**没有任何东西会红**",
          _frame.get("since_rev") == 0, _frame.get("since_rev"))
    # ★★ 而**首帧之后是增量** —— 这一条是 V6 那条 bug 的判据来源:
    #   客户端原本写着「整帧丢掉,下一帧会带全量」,那是错的。
    _ev_src = assert_helpers.code_only(gateway.sync_events)
    check("★★★ 首帧之后发的是**增量**(`snapshot(since_rev=…)`)—— "
          "客户端据此必须有断层检测与补全量的路径;"
          "「丢一帧下一帧会补回来」是**一句错话**,而它此前就写在客户端注释里",
          "since_rev=last" in _ev_src.replace(" ", ""), _ev_src[:0])

gateway.classify_caller, gateway.resolve_lan_principal = _cc_saved2, _lan_saved2
check("★★ 全局 classify_caller 已还回去 —— 不还的话后面每条断言都在对着桩说话",
      gateway.classify_caller is _cc_saved2)

# ── /v1/models:OpenAI 兼容面,逐项形状也要钉 ──────────────────────────
#  ★★ 这条契约的**真实消费者在仓外**:`90-ops/install-openwebui.ps1:81` 把
#    `OPENAI_API_BASE_URL` 指到 `http://127.0.0.1:8080/v1`,于是 Open WebUI 会读它。
#    我们自己的客户端**不解析它的响应体**(`HubClient.ProbeAsync` 只拿它当探活,
#    结果直接丢掉;模型清单走的是 `/v1/gpu/components`)。
#  ⇒ 所以这条的"客户端半边"钉的是**协议一致性**,不是"我们能解析" ——
#    见本轮决议包里对它的处置说明。**不假装我们解析了它。**
_md = _r_models.json()
check("★★ /v1/models 是 OpenAI 的 list 形状(object=='list')",
      _md.get("object") == "list", _md.get("object"))
check("★★ data 是数组", isinstance(_md.get("data"), list))
if isinstance(_md.get("data"), list) and _md["data"]:
    _item = set(_md["data"][0])
    check("★★★ 每一项的键集合**逐字钉死** —— Open WebUI 按 OpenAI 协议读 id/object,"
          "少一个它就整条列不出模型",
          _item == {"id", "object", "owned_by", "kind", "contract"}, sorted(_item))
    check("★ 每一项 object=='model'(OpenAI 协议)",
          all(x.get("object") == "model" for x in _md["data"]))
else:
    # ★ 零命中判红:空列表时上面那条会**静默不跑**,而"没有模型"与"形状对了"
    #   在输出上长得一模一样(ASSERTION-PITFALLS 第 4 条推论)。
    check("★★ /v1/models 至少列出一个模型(空列表会让上面那条逐项断言静默消失)",
          False, _md.get("data"))

# ── chat 的每一帧 ────────────────────────────────────────────────────
#  ★★★ 注入上游,**不打真模型**:一条会因为"模型今天没起"而红的断言,
#    测的就不是它自称在测的东西(ASSERTION-PITFALLS 第 5 条,已踩 2 次)。
print("\n=== CONTRACT:chat.stream.frame —— 全项目最热的一条路径 ===")


class _FakeStream:
    """只在测试里存在;生产的 _client 一个字没改。"""

    def __init__(self, chunks):
        self._chunks = chunks

    async def aiter_lines(self):
        for c in self._chunks:
            yield c

    async def __aenter__(self):
        return self

    async def __aexit__(self, *a):
        return False


_FRAME = ('{"id":"x","object":"chat.completion.chunk","created":1,"model":"m",'
          '"choices":[{"index":0,"delta":{"content":"你好"},"finish_reason":null}]}')
_frame_obj = json.loads(_FRAME)
check("★★★ CONTRACT:chat.stream.frame 顶层键集合含 choices(客户端只认它)",
      set(_frame_obj) >= CROSS_PROCESS_CONTRACTS["CONTRACT:chat.stream.frame"][1],
      sorted(_frame_obj))
check("★★★ 增量文本在 **choices[0].delta.content** —— "
      "客户端 ChatClient.ParseDeltaPayload 就是照这个位置取的;"
      "它一旦挪位置,对话**一个字都不出**而且不报错(与'模型没在跑'长得一模一样)",
      _frame_obj["choices"][0]["delta"]["content"] == "你好")
check("★★ **反向**:顶层【没有】content —— 顶层要是也有一份,"
      "客户端就算写错位置也能碰巧读到,而真正的形状漂移会被这份兜底掩盖",
      "content" not in _frame_obj)

# ★ 网关**确实**把上游的帧原样透传(不是我们自己拼的形状)——
#   这条钉住"帧形状由上游 OpenAI 协议决定",所以上面那份样本是有代表性的。
_cc_src = _nodoc(gateway.chat_completions) if "_nodoc" in dir() else \
    inspect.getsource(gateway.chat_completions)
check("★★ chat 路径是**透传**上游的 SSE 行(帧形状由 OpenAI 协议定,不是我们重拼的)",
      "aiter_lines" in _cc_src or "aiter_raw" in _cc_src or "iter_lines" in _cc_src)

# ── 元断言:每一条契约在**客户端**那半边都必须有配对 ──────────────────
#   ★ 缺配对即判红。找不到客户端源码也判红 —— 「查不了」不等于「没问题」。
_CLIENT_FILES = {
    "CONTRACT:sync.snapshot":     "Services/SyncClient.cs",
    "CONTRACT:sync.push":         "Services/SyncClient.cs",
    "CONTRACT:sync.events.frame": "Services/SyncClient.cs",
    "CONTRACT:chat.stream.frame": "Services/ChatClient.cs",
    "CONTRACT:models.list":       None,     # ★ 消费者在仓外,见上方说明
}
_APP = pathlib.Path(__file__).resolve().parents[2] / "20-client-win" / "app"
_selftest = _APP / "Selftest.cs"
_st_src = _selftest.read_text(encoding="utf-8") if _selftest.exists() else None
check("★★★ 能读到客户端自检源码(读不到 ⇒ 配对无从核对 ⇒ 判红,不当作没问题)",
      _st_src is not None, str(_selftest))
if _st_src is not None:
    check("★ 且元断言本身不是空转(确实读到了内容)", len(_st_src) > 1000)
    # ══════════════════════════════════════════════════════════════════
    #  ★★★ 判据要落在**断言消息**里,不是"文件里出现过这个字符串"。
    #
    #  红测 V2 当场证伪了宽松版:把契约号从**断言消息**里删掉、只留下分节注释
    #  `// ── CONTRACT:chat.stream.frame ──`,`_cid in _st_src` **照样为真** ——
    #  一条断言的内容被换掉了,而元断言一声不吭。
    #  ⇒ 先去掉 `//` 行注释再判:注释是**标签**,字符串字面量才是**会被打印出来的那条断言**。
    #  ★ 这正是 ASSERTION-PITFALLS 第 1 条那套"去注释再判"的**反向**用法 ——
    #    那边是怕注释把反向断言弄红,这边是怕注释把正向断言**弄绿**。
    # ══════════════════════════════════════════════════════════════════
    _st_code = "\n".join(l for l in _st_src.split("\n") if not l.lstrip().startswith("//"))
    check("★★ 去注释器没有把整份文件吃掉(否则下面整组静默变成零断言)",
          len(_st_code) > len(_st_src) * 0.5, f"{len(_st_code)}/{len(_st_src)}")
    for _cid in CROSS_PROCESS_CONTRACTS:
        check(f"★★★ 元断言:{_cid} 在客户端**断言消息**里有配对(缺配对即判红)",
              _cid in _st_code,
              "Selftest.cs 的断言消息里找不到这个契约号 —— "
              "只写在注释里不算:注释可以留着而断言被换掉,那正是「写着有防护、实际没有」")
    # ★★ 光有契约号不够:**解析器本身**得在那儿。
    #   有人把断言体删空、只留下那行标记,上面那条照样绿。
    for _cid, _rel in _CLIENT_FILES.items():
        if _rel is None:
            continue
        _f = _APP / _rel
        _src = _f.read_text(encoding="utf-8") if _f.exists() else ""
        check(f"★★ {_cid} 的客户端解析器所在文件读得到({_rel})", bool(_src), str(_f))
        check(f"★★ {_cid} 的契约号确实钉在**解析器那一侧**({_rel}),不只在自检里",
              _cid in _src, f"{_rel} 里找不到 {_cid}")

print(f"\n=== 内网同步:{_pass} PASS · {_fail} FAIL ===")
sys.exit(1 if _fail else 0)
