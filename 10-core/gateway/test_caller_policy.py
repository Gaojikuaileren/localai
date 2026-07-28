"""网关调用方策略测试(D30)。用 stub request + monkeypatch 身份解析,
只测策略判定(身份解析本身由 test_caller_identity.py 对真实连接验)。
跑:python test_caller_policy.py
"""
import sys
import gateway

_p = _f = 0


def check(name, cond):
    global _p, _f
    if cond:
        _p += 1
    else:
        _f += 1
        print(f"  FAIL: {name}")


class Req:
    def __init__(self, host, port=5000, headers=None):
        self.client = type("C", (), {"host": host, "port": port})()
        self.headers = headers or {}


def set_ident(acct):
    gateway.caller_identity.account_from_request = lambda req: acct


print("=== 隔离账户 ai-asset:必须拒 ===")
set_ident(("HONGKONGPINGPON\\ai-asset", "ai-asset"))
check("classify → denied-account", gateway.classify_caller(Req("127.0.0.1")) == "denied-account")
check("require_trusted_local → None", gateway.require_trusted_local(Req("127.0.0.1")) is None)

print("=== 隔离账户 ai-exec:必须拒 ===")
set_ident(("HONGKONGPINGPON\\ai-exec", "ai-exec"))
check("ai-exec → denied-account", gateway.classify_caller(Req("127.0.0.1")) == "denied-account")

print("=== 人类账户:放行 ===")
set_ident(("HONGKONGPINGPON\\Zori Ma", "Zori Ma"))
check("classify → trusted-local", gateway.classify_caller(Req("127.0.0.1")) == "trusted-local")
check("require_trusted_local → 返回身份", gateway.require_trusted_local(Req("127.0.0.1")) is not None)

print("=== ai-mem 自己(如 memory-service 调网关):放行 ===")
set_ident(("HONGKONGPINGPON\\ai-mem", "ai-mem"))
check("ai-mem → trusted-local", gateway.classify_caller(Req("127.0.0.1")) == "trusted-local")

print("=== 解析不到身份:chat fail-open / memory fail-closed ===")
set_ident(None)
check("classify fail-open → trusted-local", gateway.classify_caller(Req("127.0.0.1")) == "trusted-local")
check("require_trusted_local fail-closed → None", gateway.require_trusted_local(Req("127.0.0.1")) is None)

print("=== 大小写不敏感 ===")
set_ident(("X\\AI-Asset", "AI-Asset"))
check("AI-Asset(大写)也拒", gateway.classify_caller(Req("127.0.0.1")) == "denied-account")

print("=== 远程(非回环):走 WebAuthn ===")
set_ident(("X\\whoever", "whoever"))
check("非回环 → remote-unauthenticated", gateway.classify_caller(Req("100.64.0.5")) == "remote-unauthenticated")
check("远程 require_trusted_local → None", gateway.require_trusted_local(Req("100.64.0.5")) is None)

print(f"\n=== {_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
