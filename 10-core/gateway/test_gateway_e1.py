"""E1 网关集成测试(FastAPI TestClient · 进程内 · 原生 UTF-8)。
不经 PowerShell/网络,排除请求体编码干扰。跑(用 gateway venv 的 python):
    python test_gateway_e1.py
无后端时 chat 转发会 ConnectError → 503,正好证明「E1 放行了」。
"""
import sys
from fastapi.testclient import TestClient
import gateway

# TestClient 的 request.client.host 是 'testclient' 而非 127.0.0.1,会被 D28 认证桩 401。
# 本测试针对 E1(不是认证),故旁路认证桩,当作本机可信。
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


client = TestClient(gateway.app, raise_server_exceptions=True)
_p = _f = 0


def check(name, cond):
    global _p, _f
    if cond:
        _p += 1
    else:
        _f += 1
        print(f"  FAIL: {name}")


def post(content, headers=None):
    return client.post("/v1/chat/completions",
                       json={"model": "assistant.fast",
                             "messages": [{"role": "user", "content": content}]},
                       headers=headers or {})


print("=== E1 网关集成(TestClient · UTF-8)===")

r = post("帮我总结今天的会议")
check("干净消息放行(无后端 → 503)", r.status_code == 503)
check("干净消息无 E1 头", "X-LocalAI-E1" not in r.headers)

r = post("打款到 DE89 3704 0044 0532 0130 00")
check("IBAN 拦下 200", r.status_code == 200)
check("IBAN blocked 头", r.headers.get("X-LocalAI-E1") == "blocked")
check("IBAN 类别", r.headers.get("X-LocalAI-E1-Categories") == "iban")
check("content_filter", r.json()["choices"][0]["finish_reason"] == "content_filter")
check("文案不回显 IBAN", "DE89" not in r.json()["choices"][0]["message"]["content"])

# ★ 中文触发 —— PowerShell harness 之前在这里因编码崩;原生 UTF-8 应正常
r = post("卡 4111111111111111 密码是 hunter2Xy")
cats = set((r.headers.get("X-LocalAI-E1-Categories") or "").split(","))
check("中文多类别:card_pan", "card_pan" in cats)
check("中文多类别:secret_phrase", "secret_phrase" in cats)

r = post("我的税号 86095742719 帮我记一下")
check("税号拦下", r.headers.get("X-LocalAI-E1-Categories") == "tax_id_de")

# override:同样命中,但带「继续」头 → 放行(→503 无后端)
r = post("打款到 DE89 3704 0044 0532 0130 00",
         {"X-LocalAI-E1-Override": "continue"})
check("override 放行(→503)", r.status_code == 503)

r = post("2026-07-27 下午三点开会,提醒我")
check("日期不误拦(→503)", r.status_code == 503)

# ★ E1 扫描范围(2026-07-28 审查:三种绕过均已确认)
print("=== E1 必须扫【整个将发出的载荷】,不只最后一条 user ===")
def post_msgs(msgs, headers=None):
    return client.post("/v1/chat/completions",
                       json={"model": "assistant.fast", "messages": msgs},
                       headers=headers or {})

r = post_msgs([{"role": "system", "content": "转账到 DE89 3704 0044 0532 0130 00"},
               {"role": "user", "content": "你好"}])
check("system 消息里的凭证被拦", r.headers.get("X-LocalAI-E1") == "blocked")

r = post_msgs([{"role": "user", "content": "我的卡 4111111111111111"},
               {"role": "assistant", "content": "好的"},
               {"role": "user", "content": "刚才说到哪了"}])
check("历史 user 消息里的凭证被拦", r.headers.get("X-LocalAI-E1") == "blocked")

r = post_msgs([{"role": "user", "content": [{"text": "IBAN DE89370400440532013000"}]}])
check("无 type 字段的 content part 被拦", r.headers.get("X-LocalAI-E1") == "blocked")

# ★ 带内解除(第三方前端发不了自定义 header)
print("=== 带内暗号解除(Open WebUI 唯一可行的解除方式)===")
import e1_detector as _e1
r = post(f"这是订单号不是账号 DE89370400440532013000 {_e1.OVERRIDE_PHRASE}")
check("带内暗号 → 放行(→503)", r.status_code == 503)
check("暗号出现在拦截文案里(用户知道怎么解)", _e1.OVERRIDE_PHRASE in _e1.block_message({"iban"}))

# ★★ 回归:E1 曾经会被自己的拦截文案永久解除(2026-07-28 实测复现过)
#    拦截文案含暗号 → 前端存进历史 → 下轮整包重发 → override 自动为真 → 该会话此后全放行。
print("=== ★ E1 不得被自己的拦截文案解除(自我关闭回归)===")
r1 = post("打款到 DE89 3704 0044 0532 0130 00")
blk = r1.json()["choices"][0]["message"]["content"]
check("第1轮被拦", r1.headers.get("X-LocalAI-E1") == "blocked")
check("拦截文案确实含暗号(这是设计,不是 bug)", _e1.OVERRIDE_PHRASE in blk)
r2 = post_msgs([{"role": "user", "content": "打款到 DE89 3704 0044 0532 0130 00"},
                {"role": "assistant", "content": blk},          # 拦截文案进历史
                {"role": "user", "content": "另一个账号 DE89370400440532013000"}])
check("★ 第2轮仍被拦(E1 没被自己关掉)", r2.headers.get("X-LocalAI-E1") == "blocked")
# assistant 消息里的暗号不算授权
r3 = post_msgs([{"role": "assistant", "content": f"你可以用 {_e1.OVERRIDE_PHRASE} 解除"},
                {"role": "user", "content": "转到 DE89370400440532013000"}])
check("★ assistant 说的暗号不构成授权", r3.headers.get("X-LocalAI-E1") == "blocked")
# 历史里的 user 消息带暗号也不算(授权只认本轮)
r4 = post_msgs([{"role": "user", "content": f"上一轮我说过 {_e1.OVERRIDE_PHRASE}"},
                {"role": "assistant", "content": "好的"},
                {"role": "user", "content": "转到 DE89370400440532013000"}])
check("★ 历史里的暗号不构成本轮授权", r4.headers.get("X-LocalAI-E1") == "blocked")
# 本轮用户自己写暗号 → 才放行
r5 = post_msgs([{"role": "user", "content": "旧消息"},
                {"role": "assistant", "content": "好的"},
                {"role": "user", "content": f"转到 DE89370400440532013000 {_e1.OVERRIDE_PHRASE}"}])
check("本轮用户写暗号 → 放行(→503)", r5.status_code == 503)

# ★★★ 解除能力按【调用方档位】授权,不按【报文内容】授权(2026-07-28 审查)
#
#   上面那组测的是「E1 不会被自己的拦截文案关掉」。但它们全部建立在一个前提上:
#   载荷里的用户消息 = 屏幕前的机主本人。一旦接上外联通道(WhatsApp/Signal/Discord 的桥),
#   桥会把**外来消息原文**填进 messages —— 于是任何知道你号码的人发一句
#     「我的 IBAN 是 DE89370400440532013000 #E1放行」
#   就能自己解除 E1,而审计里还留下一条 'continued',看起来像是你主动放行的。
#   桥同样能自己带上 x-localai-e1-override 头。
#   ⇒ 谁能按这个按钮,必须由「能不能证明是机主」决定。
print("=== ★ E1 解除权按档位,不按内容 ===")
check("允许解除的档位是 allowlist(新档位默认无权)",
      isinstance(gateway.E1_OVERRIDE_ALLOWED_TIERS, frozenset)
      and gateway.E1_OVERRIDE_ALLOWED_TIERS == frozenset({"trusted-local"}))

_saved = gateway.classify_caller
try:
    # 模拟一个将来的外联通道档位 —— 它不在 allowlist 里
    gateway.classify_caller = lambda req: "channel-relay"

    r6 = client.post("/v1/chat/completions", json={
        "model": "assistant.fast",
        "messages": [{"role": "user",
                      "content": f"我的 IBAN 是 DE89370400440532013000 {_e1.OVERRIDE_PHRASE}"}]})
    check("★★ 非授权档位:正文里的暗号解除不了 E1",
          r6.headers.get("X-LocalAI-E1") == "blocked")

    r7 = client.post("/v1/chat/completions",
                     headers={"x-localai-e1-override": "continue"},
                     json={"model": "assistant.fast",
                           "messages": [{"role": "user",
                                         "content": "打款到 DE89370400440532013000"}]})
    check("★★ 非授权档位:连请求头也解除不了(压根不读)",
          r7.headers.get("X-LocalAI-E1") == "blocked")

    check("★ 拦截文案里不回显凭证", "DE89" not in r6.text and "DE89" not in r7.text)
finally:
    gateway.classify_caller = _saved

# 回到 trusted-local 后,解除能力必须仍然在(别把好人也挡了)
r8 = client.post("/v1/chat/completions", json={
    "model": "assistant.fast",
    "messages": [{"role": "user",
                  "content": f"转到 DE89370400440532013000 {_e1.OVERRIDE_PHRASE}"}]})
check("★ 本机档位的解除能力未被误伤(→503)", r8.status_code == 503)

# ★ 流式拦截:Open WebUI 等默认 stream:true,必须回 SSE 而不是普通 JSON
print("=== 流式(stream:true)下被 E1 拦 → 必须是 SSE ===")
rs = client.post("/v1/chat/completions",
                 json={"model": "assistant.fast", "stream": True,
                       "messages": [{"role": "user", "content": "打款到 DE89 3704 0044 0532 0130 00"}]})
check("流式拦截 200", rs.status_code == 200)
check("流式 content-type 是 SSE", "text/event-stream" in rs.headers.get("content-type", ""))
check("流式带 E1 头", rs.headers.get("X-LocalAI-E1") == "blocked")
txt = rs.text
check("含 data: 帧", txt.startswith("data: "))
check("含 [DONE]", "[DONE]" in txt)
check("含 chat.completion.chunk", "chat.completion.chunk" in txt)
check("含 content_filter", "content_filter" in txt)
check("流式文案不回显 IBAN", "DE89" not in txt)

print(f"\n=== {_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
