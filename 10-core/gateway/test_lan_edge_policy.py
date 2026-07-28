"""P3b S3 · LAN Edge / 成员表策略测试(mock 成员表 + TestClient · 进程内 UTF-8)。
跑(用 gateway 的 python):python test_lan_edge_policy.py

核心断言:带证书指纹头的请求 = LAN 设备,即便 classify_caller 因 fail-open 成 trusted-local,
也被封顶 LAN_DEVICE —— 拿不到 trusted-local 的能力(尤其解除 E1)。这正是审计对 S3 关切的洞。
"""
import sys
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
