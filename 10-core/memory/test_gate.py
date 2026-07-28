"""Memory Gate 测试。跑:python test_gate.py

重点是【写侧提权路径】—— §4.4.2 审查 S1 指出:客户端自报 user_explicit 可铸造
confidence=1.0 的「用户事实」并自动 supersede 真实事实,而 L4 直通代码执行。
这是写侧最短的提权链,所以本文件大半在试图铸造 1.0。
"""
import sys

from fastapi import FastAPI
from fastapi.exceptions import RequestValidationError
from fastapi.testclient import TestClient
from pydantic import ValidationError

import gate
from gate import (CandidateIn, GateReject, mint_confidence, classify_sensitivity,
                  issue_ticket, sanitize_validation_errors, sanitized_validation_handler,
                  audit_log)

SECRET = "我妹妹叫小雨CANARY7Q4X"
_p = _f = 0


def check(name, cond, extra=""):
    global _p, _f
    if cond:
        _p += 1
    else:
        _f += 1
        print(f"  FAIL: {name} {extra}")


print("=== 1. ★ 白名单剥离:自报字段在类型上就进不来 ===")
for bad in ["user_explicit", "source_confidence", "write_seq", "asserted_at",
            "sensitivity_domain", "attestation_kind", "device_id", "origin_device_id"]:
    try:
        CandidateIn(body="x", provenance="user_typed", **{bad: 1})
        check(f"{bad} 被拒", False, "竟然接受了")
    except ValidationError:
        check(f"{bad} 被拒", True)
check("CandidateIn 只有三个字段",
      set(CandidateIn.model_fields) == {"body", "provenance", "session_id"},
      f"{set(CandidateIn.model_fields)}")

print("=== 2. ★★ FastAPI 校验错误不得泄露被拒字段的【值】===")
errs = [{"type": "extra_forbidden", "loc": ["body", "user_explicit"], "input": True,
         "msg": "...", "url": "https://..."},
        {"type": "string_too_long", "loc": ["body", "body"], "input": SECRET, "msg": "..."}]
clean = sanitize_validation_errors(errs)
s = str(clean)
check("★ 净化后不含正文", SECRET not in s, s)
check("★ 净化后不含 input 键", "input" not in s, s)
check("★ 净化后不含 ctx/url", "url" not in s and "ctx" not in s, s)
check("保留了字段名(排查要用)", "user_explicit" in s)
check("保留了错误类型", "extra_forbidden" in s)

# 端到端:挂上处理器后的真实响应
app = FastAPI()
app.add_exception_handler(RequestValidationError, sanitized_validation_handler)


@app.post("/c")
async def c(x: CandidateIn):
    return {"ok": True}


cli = TestClient(app, raise_server_exceptions=False)
r = cli.post("/c", json={"body": SECRET, "provenance": "user_typed",
                         "user_explicit": True, "source_confidence": 1.0})
check("状态码 400(§4.4.2 要求,非 FastAPI 默认 422)", r.status_code == 400, str(r.status_code))
check("★ 响应体不含正文", SECRET not in r.text, r.text[:160])
check("★ 响应体不含 input 值", '"input"' not in r.text, r.text[:160])
check("响应体点名了被拒字段", "user_explicit" in r.text)
# 更危险的一例:body 本身超长 → 默认实现会把整段正文放进 input
r2 = cli.post("/c", json={"body": SECRET * 200, "provenance": "user_typed"})
check("★ body 超长时也不泄露正文", SECRET not in r2.text, r2.text[:160])
check("审计只记了字段名", any("user_explicit" in a.get("fields", []) for a in audit_log()))
check("★ 审计里没有值", not any(SECRET in str(a) for a in audit_log()))

print("=== 3. ★★ 置信度 1.0 只能由票据产生(写侧最短提权链)===")
sid, cand = "s1", "我妹妹叫小雨"
c1, a1 = mint_confidence(provenance="user_typed", ticket_id=None, session_id=sid, candidate=cand)
check("★ 无票据 → 拿不到 1.0", c1 < 1.0, f"{c1}")
check("无票据 → assistant_infer/0.6", (c1, a1) == (0.6, "assistant_infer"), f"{c1},{a1}")

tk = issue_ticket(sid, cand)
c2, a2 = mint_confidence(provenance="user_typed", ticket_id=tk, session_id=sid, candidate=cand)
check("有票据 → 1.0 + panel_ticket", (c2, a2) == (1.0, "panel_ticket"), f"{c2},{a2}")

# 一次性
c3, _ = mint_confidence(provenance="user_typed", ticket_id=tk, session_id=sid, candidate=cand)
check("★ 票据一次性(重放拿不到 1.0)", c3 < 1.0, f"{c3}")

# 绑定候选哈希:展示后被改 → 拒绝
tk2 = issue_ticket(sid, cand)
c4, _ = mint_confidence(provenance="user_typed", ticket_id=tk2, session_id=sid,
                        candidate="我妹妹叫别的名字")
check("★ 候选被改 → 票据失效(防「拿旧票据套新内容」)", c4 < 1.0, f"{c4}")

# 绑定会话
tk3 = issue_ticket(sid, cand)
c5, _ = mint_confidence(provenance="user_typed", ticket_id=tk3, session_id="别的会话",
                        candidate=cand)
check("★ 换会话 → 票据失效", c5 < 1.0, f"{c5}")

# 派生来源即便有票据也封顶 0.4
tk4 = issue_ticket(sid, cand)
for prov in ("tool_result", "rag_chunk", "web_content"):
    c6, a6 = mint_confidence(provenance=prov, ticket_id=tk4, session_id=sid, candidate=cand)
    check(f"★ {prov} 即便带票据也封顶 0.4", (c6, a6) == (0.4, "derived"), f"{c6},{a6}")

print("=== 4. 服务端定级 + E3 凭证拦截 ===")
lvl, hits = classify_sensitivity("我妹妹叫小雨")
check("普通内容 → S0", lvl == "S0" and not hits)
lvl2, hits2 = classify_sensitivity("我的账号 DE89370400440532013000")
check("★ 凭证命中 → 强制 S2", lvl2 == "S2" and "iban" in hits2, f"{lvl2},{hits2}")
# E3 不用 high_entropy(误报高,会把正常写入打死)
lvl3, hits3 = classify_sensitivity("token sk-Ab3Xy9Qw2Mn7Pl4Rt6Vb8Zc1Df5Gh0Jk3")
check("★ E3 排除 high_entropy", "high_entropy" not in hits3, f"{hits3}")

print("=== 5. ★ 拒绝时不落盘、不记正文(§6.9.8)===")
n0 = len(audit_log())
try:
    gate.submit_fact(None, candidate=CandidateIn(body="转账到 DE89370400440532013000",
                                                 provenance="user_typed", session_id="s9"),
                     subject_norm="我", predicate_norm="账号", object_text="DE89370400440532013000")
    check("凭证候选被拒", False, "竟然没拒")
except GateReject as ex:
    check("凭证候选被拒", True)
    check("★ 拒绝异常里不含凭证串", "DE89" not in str(ex), str(ex))
new = audit_log()[n0:]
check("记了拒绝事件", any(a.get("event") == "gate_rejection" for a in new))
check("★ 拒绝记录只有类别/时间/会话,无正文",
      not any("DE89" in str(a) for a in new), str(new))
check("拒绝记录含类别", any("iban" in a.get("categories", []) for a in new))

print("=== 6. 架构:mint_confidence 是唯一能返回 1.0 的地方 ===")
import ast
import inspect
# ★ 用 AST 而不是文本匹配 —— 「哪个函数里有 return 1.0」是语法问题。
#   上一版用正则扫源码,把文档字符串和注释也算了进去(和 test_repo 犯过的是同一类错)。
tree = ast.parse(inspect.getsource(gate))
minting: list[str] = []
for fn in [n for n in ast.walk(tree) if isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef))]:
    for node in ast.walk(fn):
        if not isinstance(node, ast.Return) or node.value is None:
            continue
        # 展开 return 的所有常量(含 tuple),找有没有 1.0
        consts = [node.value] if not isinstance(node.value, ast.Tuple) else list(node.value.elts)
        for c in consts:
            # ★ 必须判 float:Python 里 `True == 1.0` 为真(bool 是 int 的子类),
            #   不判类型就会把 `return True` 的函数误算成「铸造了 1.0 置信度」。
            if isinstance(c, ast.Constant) and isinstance(c.value, float) and c.value == 1.0:
                minting.append(fn.name)
check("★ 只有 mint_confidence 会 return 1.0", set(minting) == {"mint_confidence"},
      f"实际: {sorted(set(minting))}")
check("确实存在这条 return(不是零命中的假通过)", len(minting) >= 1)

print(f"\n=== {_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
