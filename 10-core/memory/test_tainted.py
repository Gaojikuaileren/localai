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
    CallerTier, Backend, equals_plaintext,
    unseal_for_storage, unseal_for_embedding, unseal_for_client, unseal_for_prompt,
)
from tainted import _ALLOWED_CALLERS   # 测试要断言 allowlist 的形状

SECRET = "我妹妹叫小雨CANARY7Q4X"
_p = _f = 0


def check(name, cond, extra=""):
    global _p, _f
    if cond:
        _p += 1
    else:
        _f += 1
        print(f"  FAIL: {name} {extra}")


def blocks(fn, name, expect=None):
    """断言某个操作会被拦下,且异常消息里不含正文。
    expect 给定时,还要求异常类型正好是它(用于区分「类型不对」与「被策略拒」)。"""
    try:
        r = fn()
        check(name, False, f"→ 没拦住,得到 {r!r}")
    except (MemoryLeakError, TypeError, AttributeError) as e:
        ok = SECRET not in str(e)
        if expect is not None:
            ok = ok and isinstance(e, expect)
        check(name, ok, f"{type(e).__name__}: {str(e)[:60]}")


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
v3 = unseal_for_client(t, caller=CallerTier.TRUSTED_LOCAL)
check("③ 回客户端解封拿到正文", v3 == SECRET)
LOCAL_BACKEND = Backend(name="assistant.fast", egress=False)
v4 = unseal_for_prompt(t, backend=LOCAL_BACKEND)
check("④ 进 prompt 解封拿到正文", v4 == SECRET)
check("★ 四次都记了账", len(current_ledger()) == led0 + 4, f"{len(current_ledger())-led0}")
check("账目含四种用途",
      {"storage", "embedding", "client", "prompt"} <= current_ledger().purposes)
check("★ 不存在通用 unseal()", not hasattr(sys.modules["tainted"], "unseal"))

print("=== 7. S2 内容的结构性隔离 ===")
s2 = seal("IBAN DE89370400440532013000", sensitivity="S2", source="user_typed")
blocks(lambda: unseal_for_prompt(s2, backend=LOCAL_BACKEND), "★ S2 永不进 prompt")
blocks(lambda: unseal_for_client(s2, caller=CallerTier.CHANNEL_RELAY),
       "★ S2 不交给外联通道")
blocks(lambda: unseal_for_client(s2, caller=CallerTier.LAN_DEVICE),
       "★ S2 不交给局域网设备(无提级路径)")
check("S2 可交给本机可信调用方(面板要显示)",
      unseal_for_client(s2, caller=CallerTier.TRUSTED_LOCAL).startswith("IBAN"))

print("=== 7b. ★★ 出境判据是 backend.egress,与 sensitivity 无关 ===")
# 2026-07-28 修正:原实现只判 `if t.sensitivity == "S2"`,那是 §6.9.3 的要求;
# §4.6.1 的原文判据是 `backend.egress == true 时抛`。后果是
#   unseal_for_prompt(seal(记忆,'S0'), backend='escalate.cloud') **静默成功**
# —— 而本文件当时把它断言为「正常拿到正文」。一条 S0 记忆送进云端,
# 同样违反 §5.6.2 的 L5「记忆库内容,永久禁止(出境)」。
CLOUD = Backend(name="escalate.cloud", egress=True)
blocks(lambda: unseal_for_prompt(t, backend=CLOUD),
       "★★ S0 记忆也不得进出境后端(与敏感度无关)")
blocks(lambda: unseal_for_prompt(s2, backend=CLOUD), "★ S2 更不行")
check("本地后端仍然放行(别把好人也挡了)",
      unseal_for_prompt(t, backend=LOCAL_BACKEND) == SECRET)

print("=== 7c. ★ 档位与后端都不能是裸字符串 ===")
# 裸字符串会让判据退化成 denylist:新增一档默认落在放行一侧。
# 与 provenance denylist、E1 override 是同一族缺陷。
blocks(lambda: unseal_for_client(t, caller="trusted-local"),
       "★ caller 传字符串必须报错", TypeError)
blocks(lambda: unseal_for_prompt(t, backend="assistant.fast"),
       "★ backend 传字符串必须报错", TypeError)
check("★ 新增档位默认无权(allowlist 形状)",
      all(tier not in _ALLOWED_CALLERS.get("S2", frozenset())
          for tier in (CallerTier.LAN_DEVICE, CallerTier.CHANNEL_RELAY,
                       CallerTier.REMOTE_UNAUTH)))

# ★★ 两个「结构上取不到任何正文」的档位(D66 / §6.3 档位表 · §17.7)。
#    _ALLOWED_CALLERS 的形状是 {敏感度: 允许档位集},没有「把某档位登记为空集」的位置,
#    所以 tainted.py 里那句「由 test_tainted.py 的一条正面断言守着」指的就是下面这条。
#    ——【不要】为了"看起来登记过"而把它们加进任何集合;那会让这条断言变红。
from tainted import NO_PLAINTEXT_TIERS   # noqa: E402
check("★★ resident-observer / ext-operator 不出现在任何 allowlist 里",
      all(tier not in tiers
          for tiers in _ALLOWED_CALLERS.values()
          for tier in NO_PLAINTEXT_TIERS))
check("  └ 且这两档确实是枚举成员(不是裸字符串)",
      NO_PLAINTEXT_TIERS == {CallerTier.RESIDENT_OBSERVER, CallerTier.EXT_OPERATOR})
for _tier in NO_PLAINTEXT_TIERS:
    for _sens in ("S0", "S1", "S2"):
        blocks(lambda t=t, s=_sens, c=_tier: unseal_for_client(
                   seal(SECRET, sensitivity=s, source="user_typed"), caller=c),
               f"★ {_tier.value} 取 {_sens} 正文必须被拒", MemoryLeakError)

print("=== 7d. ★★ __eq__ 不得成为猜测-确认预言机 ===")
# 原实现比 _VAULT 里的两段明文,不经解封点、不记账、不看 sensitivity。
# 实测:seal('X','S0') == seal('X','S2') 为 True 且 ledger 增量为 0
# ⇒ 任何能调到 seal 的代码都能不留痕迹地逐条确认记忆内容,
#   住址/生日/健康这类低熵可枚举的内容尤其危险。
a = seal("我妹妹叫小雨", sensitivity="S0", source="user_typed")
b = seal("我妹妹叫小雨", sensitivity="S2", source="user_typed")
led_before = len(current_ledger())
check("★★ 同内容不同对象不再相等(预言机已关闭)", a != b)
check("★ 比较不产生任何账目变化(因为它压根没读内容)",
      len(current_ledger()) == led_before)
check("同一个对象仍然等于自己", a == a)
check("★ hash 不以明文为输入", hash(a) != hash(b))
# 真要比内容 → 走具名函数,它记账
same = equals_plaintext(a, b, reason="dedup-test")
check("equals_plaintext 能比出内容相同", same)
check("★ 而且它记了账", len(current_ledger()) == led_before + 2,
      f"{len(current_ledger()) - led_before}")

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

print("=== 12. 相等性:比句柄,不比内容(见第 7d 节)===")
# ★ 本节 2026-07-28 改写。原来断言的是「同内容相等」——
#   那正是被判定为预言机的那个行为,测试当时把它当成了正确性质。
c1 = seal("同样的内容", sensitivity="S0", source="x")
c2 = seal("同样的内容", sensitivity="S0", source="y")
check("★ 同内容的两次密封【不相等】(相等会泄露内容)", c1 != c2)
check("与 str 比较不相等也不崩",
      (c1 == "同样的内容") is False or (c1 == "同样的内容") is NotImplemented)
check("可安全放进 set/dict(hash 基于句柄,不基于明文)",
      len({c1, c2}) == 2 and len({c1, c1}) == 1)

print("=== 13. 保险库随对象释放(2026-07-31 审计:_VAULT 曾只增不减)===")
import gc  # noqa: E402
from tainted import _VAULT  # noqa: E402
_base = len(_VAULT)
def _leak_once():
    _t = seal("会随对象一起消失的正文", sensitivity="S0", source="x")
    check("seal 后保险库 +1", len(_VAULT) == _base + 1)
_leak_once()
gc.collect()
check("★ 对象 GC 后保险库回到基线(正文不再滞留进程内)", len(_VAULT) == _base)

print(f"\n=== {_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
