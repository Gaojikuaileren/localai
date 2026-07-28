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
