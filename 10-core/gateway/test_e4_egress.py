"""E4 出境闸测试。纯 assert,无 pytest 依赖:python test_e4_egress.py

★ 这套测试的重点【不是】"能不能检出凭证" —— 那是 E1 检测器的事,test_e1.py 已经有 50 条。
  这里只验 E4 相对 E1 多出来的那几条性质,也正是它单独存在的理由:
    · 出境【没有】放行入口 —— 不是默认关着,是结构上不存在;
    · 出境与来源无关 —— system / 历史 / assistant 说过的话一视同仁;
    · 出境的措辞与入境【不同】 —— 不能让用户去找一个并不存在的按钮。
"""

import inspect

import e1_detector as e1
import e4_egress as e4

_pass = _fail = 0


def check(name, cond):
    global _pass, _fail
    if cond:
        _pass += 1
    else:
        _fail += 1
        print(f"  FAIL  {name}")


# ---- 1. 基本形状:与挂点(gateway.py:439/442)的用法对得上 ----
r = e4.scan("这里没有任何凭证")
check("干净文本不拦", not r.blocked)
check("干净文本没有命中类别", len(r.categories) == 0)
check("结果对象有 blocked", hasattr(r, "blocked"))
check("结果对象有 categories", hasattr(r, "categories"))

# ---- 2. 真检得出来(借 E1 的语料,确认没有被空实现糊弄过去)----
# ★ 用【公开的测试凭证】—— ECB 官方示例 IBAN,同 test_e1.py 的约定。
#   真凭证一律不入库,哪怕是测试。
KEY = "DE89370400440532013000"
hit = e4.scan(f"帮我把这个发出去:{KEY}")
check("凭证会被拦下", hit.blocked)
check("拦下时有类别", len(hit.categories) > 0)
check("与 E1 检出结果一致", hit.categories == e1.scan(f"帮我把这个发出去:{KEY}").categories)

# ---- 3. ★ 没有放行入口(这是 E4 的立身之本)----
sig = inspect.signature(e4.scan)
check("scan 只收一个参数", len(sig.parameters) == 1)
banned = {"override", "allow", "force", "bypass", "level", "profile", "whitelist"}
check("scan 的参数名里没有任何放行语义", not (set(sig.parameters) & banned))
# 只看【会执行的代码】:先摘掉模块与函数的文档字符串,再去掉 # 注释行。
# (注释里当然要谈 override —— 谈的正是"这里为什么没有它"。)
src = inspect.getsource(e4)
for doc in [e4.__doc__, e4.scan.__doc__, e4.block_message.__doc__]:
    if doc:
        src = src.replace(doc, "")
code = " ".join(l for l in src.splitlines() if not l.lstrip().startswith("#"))
check("模块的【代码】里没有任何放行分支", not (set(code.split()) & banned))

# ---- 4. 与来源无关:system / assistant 的文字同样算出境 ----
check("system 里的凭证照拦", e4.scan(f"[system] 记住这个 key:{KEY}").blocked)
check("assistant 之前说的照拦", e4.scan(f"[assistant] 你的 key 是 {KEY}").blocked)

# ---- 5. 措辞:必须说清"这条路没有放行",不能照抄 E1 ----
msg = e4.block_message(hit.categories)
check("拦截语非空", isinstance(msg, str) and len(msg) > 0)
check("拦截语与 E1 的不同", msg != e1.block_message(hit.categories))
check("拦截语说明了不可逆/无放行", ("收不回" in msg) or ("不提供放行" in msg))
check("拦截语列出了命中的类别", any(e1._CAT_LABEL.get(c, c) in msg for c in hit.categories))

# ---- 6. 边界:空/None 不应炸(挂点传的是拼接结果,可能为空)----
try:
    check("空串安全", not e4.scan("").blocked)
    check("None 安全", not e4.scan(None).blocked)
except Exception as ex:      # noqa: BLE001
    check(f"空输入不抛异常(实际抛了 {type(ex).__name__})", False)

print(f"=== E4 出境闸:{_pass} PASS · {_fail} FAIL ===")
raise SystemExit(1 if _fail else 0)
