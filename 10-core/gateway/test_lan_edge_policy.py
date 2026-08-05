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

print(f"\n=== {_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
