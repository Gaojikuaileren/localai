"""隐私类型层测试。跑:python test_tainted.py

★ 每一组都对应对抗性核验点名的一条绕过路径(2026-07-28,隐私维度 18 条 FAIL)。
  这个类的价值完全取决于「有没有一条路能把正文悄悄变回 str」——
  所以本文件的重点是**逐条尝试绕过**,而不是验证正常用法。
"""
import io
import json
import logging
import sys

from tainted import (
    TaintedText, seal, MemoryLeakError, safe_meta, current_ledger,
    unseal_for_storage, unseal_for_embedding, unseal_for_client, unseal_for_prompt,
)

SECRET = "我妹妹叫小雨CANARY7Q4X"
_p = _f = 0


def check(name, cond, extra=""):
    global _p, _f
    if cond:
        _p += 1
    else:
        _f += 1
        print(f"  FAIL: {name} {extra}")


def blocks(fn, name):
    """断言某个操作会被拦下,且异常消息里不含正文。"""
    try:
        r = fn()
        check(name, False, f"→ 没拦住,得到 {r!r}")
    except MemoryLeakError as e:
        check(name, SECRET not in str(e), "异常消息里带了正文!")
    except (TypeError, AttributeError) as e:
        check(name, SECRET not in str(e), "异常消息里带了正文!")


t = seal(SECRET, sensitivity="S0", source="user_typed")

print("=== 1. 隐式转字符串的每一条路(原设计 r.text 的同族)===")
blocks(lambda: str(t),                      "str(t)")
blocks(lambda: f"{t}",                      "f-string")
blocks(lambda: "{}".format(t),              ".format()")
blocks(lambda: "%s" % t,                    "% 格式化")
blocks(lambda: "x" + t,                     "字符串拼接 str+t")
blocks(lambda: t + "x",                     "字符串拼接 t+str")
blocks(lambda: "".join([t]),                "join([t])  ★ 正是原设计的失效写法")
blocks(lambda: "\n".join(x for x in [t]),   "join(生成器)")
blocks(lambda: list(t),                     "list(t) 迭代")
blocks(lambda: t[0],                        "索引")
blocks(lambda: t[0:3],                      "切片")
blocks(lambda: bytes(t),                    "bytes(t)")
blocks(lambda: "妹妹" in t,                  "in 检查")
blocks(lambda: json.dumps({"a": t}),        "json.dumps  ★ 回 JSON 的常见写法")

print("=== 2. ★★ 直读属性 —— 原设计最致命的漏网 ===")
check("没有 .value 属性",  not hasattr(t, "value"))
check("没有 ._value 属性", not hasattr(t, "_value"))
check("没有 .text 属性",   not hasattr(t, "text"))
check("没有 __dict__(slots)", not hasattr(t, "__dict__"))
# 逐个 slot 检查:任何一个 slot 都不得直接持有正文
for s in TaintedText.__slots__:
    v = getattr(t, s, None)
    check(f"slot {s!r} 不含正文", not (isinstance(v, str) and SECRET in v), f"→ {v!r}")
# vars() 也不行
try:
    check("vars(t) 拿不到正文", SECRET not in str(vars(t)))
except TypeError:
    check("vars(t) 直接不可用(无 __dict__)", True)

print("=== 3. repr 必须安全:不泄露,且【不抛异常】(调试器/pytest/logging 都会调)===")
rp = repr(t)
check("repr 不含正文", SECRET not in rp, rp)
check("repr 不抛异常", isinstance(rp, str))
check("repr 含元数据便于排查", "sensitivity" in rp and "sealed" in rp)

print("=== 4. logging 不得泄露(异常/日志是最常见的意外出口)===")
buf = io.StringIO()
h = logging.StreamHandler(buf)
lg = logging.getLogger("tainted_probe"); lg.addHandler(h); lg.setLevel(logging.DEBUG)
lg.info("memory=%r", t)          # %r → repr,安全
lg.info("meta=%s", safe_meta(t))
try:
    lg.info("memory=%s", t)      # %s → str,应抛
except MemoryLeakError:
    pass
out = buf.getvalue()
check("★ 日志里没有正文", SECRET not in out, out[:120])

print("=== 5. 异常消息不得带正文(否则这个类自己就是泄漏点)===")
try:
    str(t)
except MemoryLeakError as e:
    check("异常消息不含正文", SECRET not in str(e))
    check("异常消息有指导性", "解封函数" in str(e))

print("=== 6. ★ 四个具名解封点都在、都记账 ===")
led0 = len(current_ledger())
v1 = unseal_for_storage(t, table="l3_fact")
check("① 写库解封拿到正文", v1 == SECRET)
v2 = unseal_for_embedding(t)
check("② 向量化解封拿到正文", v2 == SECRET)
v3 = unseal_for_client(t, caller="trusted-local")
check("③ 回客户端解封拿到正文", v3 == SECRET)
v4 = unseal_for_prompt(t, backend="assistant.fast")
check("④ 进 prompt 解封拿到正文", v4 == SECRET)
check("★ 四次都记了账", len(current_ledger()) == led0 + 4, f"{len(current_ledger())-led0}")
check("账目含四种用途",
      {"storage", "embedding", "client", "prompt"} <= current_ledger().purposes)
check("★ 不存在通用 unseal()", not hasattr(sys.modules["tainted"], "unseal"))

print("=== 7. S2 内容的结构性隔离 ===")
s2 = seal("IBAN DE89370400440532013000", sensitivity="S2", source="user_typed")
blocks(lambda: unseal_for_prompt(s2, backend="assistant.fast"), "★ S2 永不进 prompt")
blocks(lambda: unseal_for_client(s2, caller="mobile-remote"),   "★ S2 不交给远程调用方")
check("S2 可交给本机可信调用方(面板要显示)",
      unseal_for_client(s2, caller="trusted-local").startswith("IBAN"))

print("=== 8. embedding 端点必须是回环(将来改云端要走出境闸门)===")
blocks(lambda: unseal_for_embedding(t, endpoint="api.openai.com"), "★ 非回环 embedding 被拒")
check("回环端点允许", unseal_for_embedding(t, endpoint="127.0.0.1:18084") == SECRET)

print("=== 9. 不可变(防止把句柄换成别的正文)===")
blocks(lambda: setattr(t, "_handle", "x"), "改 _handle")
blocks(lambda: setattr(t, "sensitivity", "S0"), "改 sensitivity")

print("=== 10. 元信息可用,且取长度是个显式决定(不给 __len__)===")
check("length 可用", t.length == len(SECRET))
check("★ 不实现 __len__(len() 是顺手,.length 是决定)",
      not hasattr(TaintedText, "__len__"))
check("safe_meta 不含正文", SECRET not in json.dumps(safe_meta(t), ensure_ascii=False))

print("=== 11. seal 幂等 / 类型检查 ===")
check("seal(TaintedText) 幂等", seal(t, sensitivity="S0", source="x") is t)
try:
    seal(123, sensitivity="S0", source="x"); check("非 str 应拒", False)
except TypeError:
    check("非 str 应拒", True)
check("seal(None) → 空", seal(None, sensitivity="S0", source="x").length == 0)

print("=== 12. 相等性可用但不泄露 ===")
a = seal("同样的内容", sensitivity="S0", source="x")
b = seal("同样的内容", sensitivity="S0", source="y")
check("同内容相等", a == b)
check("与 str 比较不相等也不崩", (a == "同样的内容") is False or (a == "同样的内容") is NotImplemented)

print(f"\n=== {_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
