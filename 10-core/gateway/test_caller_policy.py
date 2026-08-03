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

print("=== 解析不到身份:两条路径都 fail-closed(2026-08-03 改)===")
# ★ 本节的旧断言是「classify fail-open → trusted-local」,记录的是 D30 的原始判据:
#   denylist —— 不在 LOCAL_DENY_ACCOUNTS 就给 trusted-local,解析不到也给。
#   实测推翻了它的前提:本机已存在 CodexSandboxOffline / CodexSandboxOnline
#   两个外部 AI 沙箱账户(Enabled · 在 Users 组 · 不在拒绝表),按旧判据它们就是最高档,
#   而 trusted-local 是 E1_OVERRIDE_ALLOWED_TIERS 与 _ALLOWED_CALLERS["S2"] 的唯一成员。
#   ⇒ 判据改为 allowlist(config/caller-accounts.toml),此断言随之改写。
#   ★ 降档【不断连】:unregistered-local 仍能用 chat(等价于直连 llama-server,不构成回归),
#     它失去的只是 E1 解除权与 S2 正文权。
set_ident(None)
check("classify 解析不到 → unregistered-local(不再是 trusted-local)",
      gateway.classify_caller(Req("127.0.0.1")) == "unregistered-local")
check("★ 解析不到的调用方拿不到 E1 解除权",
      gateway.classify_caller(Req("127.0.0.1")) not in gateway.E1_OVERRIDE_ALLOWED_TIERS)
check("require_trusted_local fail-closed → None", gateway.require_trusted_local(Req("127.0.0.1")) is None)

print("=== 未登记的本机账户(如外部 AI 沙箱):降档,不放行到最高档 ===")
set_ident(("HONGKONGPINGPON\\CodexSandboxOnline", "CodexSandboxOnline"))
check("CodexSandboxOnline → unregistered-local",
      gateway.classify_caller(Req("127.0.0.1")) == "unregistered-local")
check("★ 它拿不到 E1 解除权",
      gateway.classify_caller(Req("127.0.0.1")) not in gateway.E1_OVERRIDE_ALLOWED_TIERS)
check("★ 它进不了记忆敏感路径", gateway.require_trusted_local(Req("127.0.0.1")) is None)
set_ident(("X\\某个将来新建的账户", "某个将来新建的账户"))
check("★★ 任意新账户默认落【降档】侧(allowlist 形状)",
      gateway.classify_caller(Req("127.0.0.1")) == "unregistered-local")

print("=== 大小写不敏感 ===")
set_ident(("X\\AI-Asset", "AI-Asset"))
check("AI-Asset(大写)也拒", gateway.classify_caller(Req("127.0.0.1")) == "denied-account")

print("=== 远程(非回环):走 WebAuthn ===")
set_ident(("X\\whoever", "whoever"))
check("非回环 → remote-unauthenticated", gateway.classify_caller(Req("100.64.0.5")) == "remote-unauthenticated")
check("远程 require_trusted_local → None", gateway.require_trusted_local(Req("100.64.0.5")) is None)

# ★ IPv6 回环旁路回归(2026-07-28 审查发现):曾把 ::1 当可信回环,而身份解析只查 IPv4 表
#   → 对 ::1 恒解析不到 → fail-open 成 trusted-local,等于对 IPv6 整体关掉 D30 且无日志。
print("=== ::1 不得被当作可信回环(身份不可解析 → 必须 fail-closed)===")
set_ident(None)                                   # 模拟:IPv6 调用方解析不到身份
check("::1 不是 trusted-local", gateway.classify_caller(Req("::1")) != "trusted-local")
check("::1 → remote-unauthenticated", gateway.classify_caller(Req("::1")) == "remote-unauthenticated")
check("::1 require_trusted_local → None", gateway.require_trusted_local(Req("::1")) is None)
set_ident(("X\\ai-asset", "ai-asset"))            # 即使能解析出隔离账户也不该走 ::1 放行路径
check("::1 + ai-asset 仍不放行", gateway.classify_caller(Req("::1")) != "trusted-local")

print(f"\n=== {_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
