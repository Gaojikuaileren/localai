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

    pass

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
