"""P3b S3 · LAN Edge / 成员表策略测试(mock 成员表 + TestClient · 进程内 UTF-8)。
跑(用 gateway 的 python):python test_lan_edge_policy.py

核心断言:带证书指纹头的请求 = LAN 设备,即便 classify_caller 因 fail-open 成 trusted-local,
也被封顶 LAN_DEVICE —— 拿不到 trusted-local 的能力(尤其解除 E1)。这正是审计对 S3 关切的洞。
"""
import sys

# ★★ 编码双保险(与 P4-S0 同源):干净的 cp936 控制台编不出 ⇒ / ✓ / ★ 之类的字符,
#   而 print 一抛异常会把整套脚本掀翻 —— 于是【一条断言变红】表现成【整套崩溃】,
#   运行器只看到"没有汇总行",看不出是哪条没守住。
#   S0 当年修的是 vram_gate 的生产路径,测试脚本这边一直没修 —— 2026-08-05 补上。
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass
import gateway
from fastapi.testclient import TestClient
import e1_detector as _e1

_p = _f = 0


def check(name, cond):
    global _p, _f
    if cond:
        _p += 1
    else:
        _f += 1
        print(f"  FAIL: {name}")


# --- resolve_lan_principal:指纹反查(mock 成员表)---
print("=== 指纹 → LAN_DEVICE 反查(fail-closed)===")
gateway.membership.active_device = lambda fp: {"device_id": "dev-1", "generation": 3} if fp == "GOODFP" else None
p = gateway.resolve_lan_principal("GOODFP")
check("已激活指纹 → LAN_DEVICE + 成员表 device_id", p and p["tier"] == "lan-device" and p["device_id"] == "dev-1")
check("未知指纹 → None(fail-closed)", gateway.resolve_lan_principal("BADFP") is None)

# --- 路由归类元测试 ---
print("=== 路由默认归类元测试(新增未归类=失败)===")
unc = gateway.unclassified_routes()
check(f"所有路由已显式归类(未归类:{unc})", unc == [])

client = TestClient(gateway.app)

# --- /health 收窄 ---
rh = client.get("/health")
check("/health 只回 status,不泄露别名清单", rh.json() == {"status": "ok"})

# --- LAN 设备行为(桩 classify_caller=trusted-local,模拟最坏的 fail-open)---
gateway.classify_caller = lambda req: "trusted-local"

# ══════════════════════════════════════════════════════════════════════
#  ★★★ 2026-08-05:把「后端不可达」【注入】,不再依赖它碰巧没起。
#
#  本文件原来的判据(见开头 docstring)是:「无后端时 chat 转发会 ConnectError → 503,
#  正好证明 E1 放行了」。**那个前提是环境,不是代码。**
#  当晚模型第一次真的接进来、llama-server 起在 18081 之后,这些 503 变成了 200,
#  两个套件当场红/崩。
#
#  ★ 比显存闸那条(ASSERTION-PITFALLS 第 5 条)更刺眼的地方:
#    这条断言**整天是绿的,恰恰因为产品还不能用**。
#    它把「后端没起」当成了判据的一部分 —— 于是产品做成的那一刻它就坏了。
#    ⇒ 一条断言若会因为"功能终于能用了"而变红,它测的就不是它自称在测的东西。
#
#  修法与 vram_gate 同款:注入,不读环境。让上游调用**恒定**不可达,
#  于是 503 依然精确表示「E1 放行了、转发被尝试了」,而与谁在跑无关。
# ══════════════════════════════════════════════════════════════════════
import httpx as _httpx


class _AlwaysUnreachable:
    """恒定不可达的上游。★ 只在测试里存在 —— 生产的 _client 一个字没改。"""

    def build_request(self, *a, **k):
        raise _httpx.ConnectError("注入:上游恒定不可达(测试用)")

    async def send(self, *a, **k):
        raise _httpx.ConnectError("注入:上游恒定不可达(测试用)")

    async def post(self, *a, **k):
        raise _httpx.ConnectError("注入:上游恒定不可达(测试用)")


gateway._client = _AlwaysUnreachable()
# ★ 元断言:注入要是没生效(比如将来改名了),下面那一堆 503 会退回依赖环境。
assert not isinstance(gateway._client, _httpx.AsyncClient), "上游注入没生效"

print("=== 带指纹头 = LAN 设备:封顶 LAN_DEVICE,拿不到 trusted-local ===")


def post(content, headers=None):
    return client.post("/v1/chat/completions",
                       json={"model": "assistant.fast", "messages": [{"role": "user", "content": content}]},
                       headers=headers or {})


r = post("你好", {"x-localai-cert-sha256": "UNKNOWN"})
check("未知指纹的 LAN 请求 → 401", r.status_code == 401)

r = post("总结今天的会议", {"x-localai-cert-sha256": "GOODFP"})
check("已知指纹、干净 chat → 转发(无后端 503)", r.status_code == 503)

r = post(f"我的 IBAN 是 DE89370400440532013000 {_e1.OVERRIDE_PHRASE}", {"x-localai-cert-sha256": "GOODFP"})
check("★ LAN 设备正文带暗号也解除不了 E1", r.headers.get("X-LocalAI-E1") == "blocked")
check("★ 拦截文案不回显 IBAN", "DE89" not in r.text)

r = post("打款到 DE89370400440532013000", {"x-localai-cert-sha256": "GOODFP", "x-localai-e1-override": "continue"})
check("★ LAN 设备的 override 请求头也无效", r.headers.get("X-LocalAI-E1") == "blocked")

# 对照:本机 trusted-local(无指纹头)暗号仍可解除 —— 别把好人误伤
r = post(f"打款到 DE89370400440532013000 {_e1.OVERRIDE_PHRASE}")
check("对照:本机 trusted-local 暗号仍可解除(→503)", r.status_code == 503)

# ══════════════════════════════════════════════════════════════════════════
#  ★★★★★ V32 · 吊销要能掐掉**在途的流**(网关这一侧 · 清单 S6③)
#
#  ★★★ 这组判据**能不能为假**是本车道的成败点。一条「吊销后流断了」的断言,
#    若它测的是「**下一次**请求被 401」,那它测的是别的东西 —— 上面那条
#    「未知指纹的 LAN 请求 → 401」在 V32 之前**本来就绿**。
#  ⇒ 下面**真的起一条流**(驱动 `chat_completions` 里那个真的 `gen()`,一行没换)、
#    **真的在流中把成员表改成已吊销**、**真的断言这一条流当场结束**。
#
#  ★ 判据形状借自 `20-client-win/spikes/s1-revocation/Program.cs` 的 Test A
#    (P3b S1 / Spike 7,D43)。尖刀在**同一个进程**里起流+吊销+断言中止,
#    这里同款:上游用桩(不打真模型),成员表用桩(第 1 节就已经是桩)。
#  ★★ **不打真模型**:一条会因为"模型今天没起"而红的断言,测的不是它自称在测的东西
#    (ASSERTION-PITFALLS 第 5 条)。
# ══════════════════════════════════════════════════════════════════════════
print("=== V32:吊销掐掉在途流(网关侧)===")

_TOTAL_CHUNKS = 20          # 桩上游总共吐这么多块
_REVOKE_AFTER = 3           # 成员表在第几次复查之后翻脸


class _FakeUpstreamStream:
    """桩上游:吐 _TOTAL_CHUNKS 块。★ 只在测试里存在,生产的 `_client` 一个字没改。"""

    def __init__(self):
        self.status_code = 200
        self.headers = {}
        self.closed = False

    async def aiter_raw(self):
        for i in range(_TOTAL_CHUNKS):
            yield (f'data: {{"choices":[{{"index":0,"delta":{{"content":"t{i}"}}}}]}}'
                   "\n\n").encode("utf-8")

    async def aread(self):
        return b""

    async def aclose(self):
        self.closed = True


class _FakeClient:
    def __init__(self):
        self.stream_obj = _FakeUpstreamStream()

    def build_request(self, *a, **kw):
        return object()

    async def send(self, req, stream=False):
        return self.stream_obj


_saved_client = gateway._client
_saved_recheck = gateway._LAN_REVOKE_RECHECK_S
_saved_active = gateway.membership.active_device

#  ★ 复查周期压到 0(每块都查)—— 让"吊销发生在流中间"这件事在测试里可控。
#    ★★ 生产那个 1.0 秒的值**另外单独钉**(见下面最后一条),不靠这里顺带验。
gateway._LAN_REVOKE_RECHECK_S = 0.0
_fake = _FakeClient()
gateway._client = _fake

_calls = {"n": 0}


def _active_then_revoked(fp):
    """前 _REVOKE_AFTER 次说"还在",之后说"已吊销" —— 模拟**流中间**被解除。"""
    _calls["n"] += 1
    if fp == "GOODFP" and _calls["n"] <= _REVOKE_AFTER:
        return {"device_id": "dev-1", "generation": 3}
    return None


try:
    gateway.membership.active_device = _active_then_revoked
    _sr = client.post("/v1/chat/completions",
                      json={"model": "assistant.fast", "stream": True,
                            "messages": [{"role": "user", "content": "讲个长故事"}]},
                      headers={"x-localai-cert-sha256": "GOODFP"})
    _txt = _sr.text
    _delivered = _txt.count('"delta"')

    # ── ① 前置:没有这两条,下面那条承重断言可以恒真 ──────────────────────
    check("★★ 前置:流**真的起来了**(吊销之前吐出过内容块)—— 少了这一条,"
          "「流提前结束」可能只是它压根没起来,而那样的判据恒真", _delivered > 0)
    check("★★ 前置:桩上游**本来会吐更多**(否则「提前结束」无从谈起)",
          _TOTAL_CHUNKS > _REVOKE_AFTER + 1)

    # ── ② ★★★★ 承重:这一条流**当场结束**,没有跑完 ──────────────────────
    # ★ 消息在**两种结果下都要读得通** —— 上一版写死"只吐了 N 块就停了",
    #   而红的时候 N 恰好等于总数,于是打印出"只吐了 20 块就停了,没有跑满 20 块"。
    #   一条**红时说不清自己为什么红**的断言,会让人去改一个没坏的东西。
    check(f"★★★★ 吊销**掐掉了正在跑的那条流**(实测吐了 {_delivered}/{_TOTAL_CHUNKS} 块;"
          "吐满 = 根本没掐掉)—— 这是 S6③ 那条真缺陷在网关侧的判据,"
          "它红就意味着吊销对在途流仍然无效(流会照常跑完)",
          0 < _delivered < _TOTAL_CHUNKS)

    # ── ③ ★★★ 不是**静默**断流:必须带着原因停 ───────────────────────────
    #  判词:给错原因的提示比不给提示更坏,而**没有原因**也是一种给错。
    check("★★★ 停之前补发了带原因的那一帧(event: error + "
          f"{gateway.LAN_REVOKED_TYPE})—— 静默断流会被客户端读成"
          "「上游说完了」,而这段回答其实是被掐断的",
          "event: error" in _txt and gateway.LAN_REVOKED_TYPE in _txt)

    #  ★ 帧的**形状**要能被消费者解析,不是"文本里出现过那个词"。
    _err_line = next((l for l in _txt.split("\n")
                      if l.startswith("data: ") and gateway.LAN_REVOKED_TYPE in l), None)
    check("★★ 那一帧是一行合法的 `data:` SSE 帧(找不到 ⇒ 下面整段是零断言)",
          _err_line is not None)
    if _err_line is not None:
        import json as _json
        _obj = _json.loads(_err_line[len("data: "):])
        # ★ 信封 {error:{type,message}} 与客户端 `ChatClient.ParseError` 读的一致;
        #   ★★ 与 lan-edge 的 `Edge.StreamRevokedType` **必须是同一个字符串**。
        check("★★★ 那一帧的信封是 {error:{type,message}} —— 客户端 ChatClient.ParseError 读的就是它",
              isinstance(_obj.get("error"), dict)
              and _obj["error"].get("type") == gateway.LAN_REVOKED_TYPE
              and isinstance(_obj["error"].get("message"), str)
              and len(_obj["error"]["message"]) > 20)
        check("★★★ 那句话明说了这段回答**没说完** —— 只说「已被解除」的话,"
              "用户仍然会把屏幕上那半截当成完整答案",
              "没有说完" in _obj["error"]["message"])

    # ── ④ 反向:成员表**一直有效**时,同一条路必须把流**吐满** ────────────
    #  ★★ 少了这一条,上面全部断言可以靠"这条路一直是断的"变绿 ——
    #    而"判据恒真"正是本仓反复吃亏的那个形状。
    _calls["n"] = 0
    gateway.membership.active_device = lambda fp: (
        {"device_id": "dev-1", "generation": 3} if fp == "GOODFP" else None)
    _fake.stream_obj = _FakeUpstreamStream()
    _sr2 = client.post("/v1/chat/completions",
                       json={"model": "assistant.fast", "stream": True,
                             "messages": [{"role": "user", "content": "讲个长故事"}]},
                       headers={"x-localai-cert-sha256": "GOODFP"})
    _full = _sr2.text.count('"delta"')
    check(f"★★★ 反向:设备**没被吊销**时同一条路把流吐满({_full}/{_TOTAL_CHUNKS})—— "
          "证明上面测到的「断」是**吊销造成的**,不是这条路本来就断",
          _full == _TOTAL_CHUNKS)
    check("★★★ 反向:没被吊销的流里**没有**那一帧错误帧(否则它就是条恒发的帧,"
          "而恒发的帧等于没有信息)",
          gateway.LAN_REVOKED_TYPE not in _sr2.text)
finally:
    # ★ 用完必还 —— 不还的话后面每一条断言都在对着测试用的桩说话。
    gateway._client = _saved_client
    gateway._LAN_REVOKE_RECHECK_S = _saved_recheck
    gateway.membership.active_device = _saved_active

check("★★ 桩已还回去(生产 _client / 复查周期 / 成员表都恢复原样)",
      gateway._client is _saved_client
      and gateway._LAN_REVOKE_RECHECK_S == _saved_recheck
      and gateway.membership.active_device is _saved_active)

#  ★★★ 生产那个复查周期**单独钉**:上面把它压到 0 才测得动,
#    而真正要守的是「它小于 D43 给在途中止的 2 秒 SLO」。
#    不钉的话,有人把它改成 60 秒,上面每一条照样绿 —— 而实机上要等一分钟。
check(f"★★★ 生产复查周期({gateway._LAN_REVOKE_RECHECK_S}s)小于 D43 的 2 秒在途中止 SLO —— "
      "把它调大不会让上面任何一条变红(它们把周期压到 0 了),所以这一条必须单独存在",
      0 < gateway._LAN_REVOKE_RECHECK_S < 2.0)

#  ★ 两侧那个词必须**一模一样**:两侧掐断的流说两句不同的话,副机没法把它们归成一件事。
#  ★★ 判据去读 **lan-edge 的源码**,不是在这儿再写一遍那个字符串 ——
#    再写一遍的话,两处一起漂移时这条断言**照样绿**(它测的是自己)。
import pathlib as _pl

_EDGE = _pl.Path(__file__).resolve().parents[2] / "10-core" / "lan-edge" / "Program.cs"
_edge_src = _EDGE.read_text(encoding="utf-8", errors="replace") if _EDGE.exists() else None
check("★★ 能读到 lan-edge 源码(读不到 ⇒ 两侧对不了词 ⇒ 判红,不当作没问题)",
      _edge_src is not None)
if _edge_src is not None:
    check("★★★ lan-edge 与网关用**同一个**中止原因词 —— 两侧各写一个字符串的话,"
          "改一处、另一处静默失配,而症状是同一件事在副机上说两句不同的话",
          f'StreamRevokedType = "{gateway.LAN_REVOKED_TYPE}"' in _edge_src)

print(f"\n=== {_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
