"""错误响应的披露面测试。纯 assert,无 pytest 依赖:python test_error_disclosure.py

判据一句话:**错误响应不得携带调用方本来无权获得的内容。**

★ 为什么需要这一套(2026-08-04 方向 B 设计勘察顺带揪出来的):
  chat 路由的几条错误分支曾把内部信息直接放进回给调用方的 JSON:
    · 404 未知别名  → 枚举 REGISTRY 里**所有** chat 别名
    · 502 非法 JSON → `detail: r.text[:500]`,即**上游原始字节**
    · 上游 4xx/5xx  → 同样回 `detail` + `backend`
    · 503 未响应    → 回**后端 URL** 与 fallback
  而 D30 的「降档不断连」有意让 `unregistered-local` 仍能走 chat ——
  2026-08-03 实测本机就有两个未登记的外部 AI 沙箱账户。**对它们这些是侦察材料。**

★★ 本文件的重点是最后那条【反向全表断言】:
  正向逐条测只能守住今天这四条;而事故的形状永远是**新加的那条忘了管**。
  所以用 AST 走遍 chat_completions 里**每一个** return,谁带了披露字段就红。
"""

import ast
import inspect
import re

import gateway

_pass = _fail = 0


def check(name, cond):
    global _pass, _fail
    if cond:
        _pass += 1
    else:
        _fail += 1
        print(f"  FAIL  {name}")


# ---- 1. _short_echo:自报值回显必须有界且无控制字符 ----
check("回显截断到 64 字", len(gateway._short_echo("x" * 500)) == 64)
check("自定义上限生效", len(gateway._short_echo("x" * 500, 10)) == 10)
check("控制字符被剔掉", "\x00" not in gateway._short_echo("a\x00b") and "\n" not in gateway._short_echo("a\nb"))
check("None 安全", gateway._short_echo(None) == "")
check("正常短串原样", gateway._short_echo("assistant.fast") == "assistant.fast")

# ---- 2. 诊断落服务端日志的入口存在,且签名带 detail ----
check("有 log_upstream_problem", callable(getattr(gateway, "log_upstream_problem", None)))
_sig = inspect.signature(gateway.log_upstream_problem)
check("log_upstream_problem 收 detail(原文往这儿去,不往调用方去)", "detail" in _sig.parameters)
check("log_upstream_problem 收 backend(后端地址往这儿去)", "backend" in _sig.parameters)

# ---- 3. 源码级:四条曾经披露的分支现在不再披露 ----
SRC = inspect.getsource(gateway.chat_completions)


def _code_only(src: str) -> str:
    """只看会执行的代码:去掉 # 注释行与三引号文档串。

    ★ 必须这么做 —— 注释里当然会提到 `backend` 与 `detail`(讲的正是"为什么不回给调用方"),
      不摘掉的话这套断言会因为注释而假红/假绿。
    """
    src = re.sub(r'"""(?:.|\n)*?"""', "", src)
    return "\n".join(l for l in src.splitlines() if not l.lstrip().startswith("#"))


CODE = _code_only(SRC)

check("404 不再枚举别名全表", "for k,v in REGISTRY.items()" not in CODE.replace(" ", "")
      .replace("fork,vinREGISTRY.items()", "for k,v in REGISTRY.items()")
      or "可用别名见 /v1/models" in CODE)
check("404 指向 /v1/models 而不是列表", "/v1/models" in CODE)
check("不再把上游原文塞进响应(r.text[:500])", "r.text[:500]" not in CODE.replace(" ", ""))
check("不再把上游原文塞进响应(raw.decode(...)[:500])",
      not re.search(r"raw\.decode\([^)]*\)\[:\d+\]", CODE))
check("诊断改走服务端日志", "log_upstream_problem(" in CODE)

# ---- 4. ★★ 反向全表断言:chat_completions 里【每一个】返回都不得带披露字段 ----
#   这条才是承重的。逐条正向测只守得住今天这四条;新加一条 return 时,
#   正向测【不会响】—— 而"新增的那条忘了管"正是事故的形状。
DISCLOSING_KEYS = {"backend", "detail", "fallback", "upstream_url"}
ALLOWED_KEYS = {
    # 非内容型诊断:说清"上游怎么了",但不含上游说了什么、也不含它在哪
    "upstream_status", "upstream_content_type", "upstream_bytes", "hint",
}

tree = ast.parse(inspect.getsource(gateway.chat_completions))
offenders = []
for node in ast.walk(tree):
    if not isinstance(node, ast.Return) or node.value is None:
        continue
    for sub in ast.walk(node.value):
        if not isinstance(sub, ast.Dict):
            continue
        for k in sub.keys:
            if isinstance(k, ast.Constant) and isinstance(k.value, str):
                if k.value in DISCLOSING_KEYS:
                    offenders.append((getattr(node, "lineno", "?"), k.value))

check(f"★★ 反向全表:没有任何 return 带披露字段(实测 offenders={offenders})", not offenders)
check("披露字段表非空(否则上一条是重言式)", len(DISCLOSING_KEYS) > 0)
check("允许的非内容型诊断键是显式登记的", "upstream_status" in ALLOWED_KEYS)

# ---- 5. 红测自检:把判据本身反过来跑一遍,确认它真的能红 ----
#   ★ 不这么做的话,第 4 条可能因为 AST 走错了子树而永远绿 —— 那就是个假断言。
_probe = ast.parse('def f():\n    return JSONResponse(content={"error": {"backend": x}})\n')
_probe_hits = []
for node in ast.walk(_probe):
    if isinstance(node, ast.Return) and node.value is not None:
        for sub in ast.walk(node.value):
            if isinstance(sub, ast.Dict):
                for k in sub.keys:
                    if isinstance(k, ast.Constant) and k.value in DISCLOSING_KEYS:
                        _probe_hits.append(k.value)
check("★ 红测:同一判据对着一个【故意披露】的 return 必须命中", _probe_hits == ["backend"])

_probe2 = ast.parse('def f():\n    return JSONResponse(content={"error": {"alias": x}})\n')
_probe2_hits = []
for node in ast.walk(_probe2):
    if isinstance(node, ast.Return) and node.value is not None:
        for sub in ast.walk(node.value):
            if isinstance(sub, ast.Dict):
                for k in sub.keys:
                    if isinstance(k, ast.Constant) and k.value in DISCLOSING_KEYS:
                        _probe2_hits.append(k.value)
check("★ 红测:对着一个【干净】的 return 必须不命中(否则是全拦式假绿)", _probe2_hits == [])

print(f"=== 错误响应披露面:{_pass} PASS · {_fail} FAIL ===")
raise SystemExit(1 if _fail else 0)
