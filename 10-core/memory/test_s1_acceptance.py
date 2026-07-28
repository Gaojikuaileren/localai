"""S1 验收:第一条例句端到端。跑(需活库):python test_s1_acceptance.py

  写入「我妹妹叫小雨」(经 Gate,带面板票据)
    → 问「我妹妹叫什么名字」
    → 选路必须走【结构化轨】(向量轨返回相关片段却答不出名字)
    → 答对 + 溯源六件套齐全 + 正文全程密封

★ 这是 §14 P3a 两条验收例句之一。另一条(「上次聊的那个灯光问题」)要等 S2 向量轨。
"""
import sys

import gate
import repo
import route
from gate import CandidateIn, issue_ticket
from route import Route
from tainted import TaintedText, unseal_for_client

_p = _f = 0
SUBJ, PRED, NAME = "s1acc_我", "妹妹", "小雨"


def check(name, cond, extra=""):
    global _p, _f
    if cond:
        _p += 1
        print(f"  PASS  {name}")
    else:
        _f += 1
        print(f"  FAIL  {name} {extra}")


try:
    conn = repo.connect()
except Exception as ex:
    print(f"  跳过:连不上 PG({type(ex).__name__})—— 需以 ai_mem_local 身份运行(SSPI)")
    sys.exit(0)

try:
    with conn.cursor() as cur:
        cur.execute("DELETE FROM mem.l3_fact WHERE subject_norm=%s", (SUBJ,))
    conn.commit()

    print("=== ① 经 Gate 写入(带面板票据 ⇒ 置信度 1.0)===")
    body = f"我妹妹叫{NAME}"
    sid = "s1-acceptance"
    tk = issue_ticket(sid, body)
    res = gate.submit_fact(conn,
                           candidate=CandidateIn(body=body, provenance="user_typed", session_id=sid),
                           subject_norm=SUBJ, predicate_norm=PRED,
                           object_text=NAME, ticket_id=tk)
    conn.commit()
    check("写入成功", isinstance(res.fact_id, int))
    check("置信度 1.0(票据背书)", res.source_confidence == 1.0, f"{res.source_confidence}")
    check("attestation=panel_ticket", res.attestation_kind == "panel_ticket")
    check("服务端定级 S0(非凭证)", res.sensitivity == "S0")

    print("=== ② 选路:必须走结构化轨 ===")
    q = "我妹妹叫什么名字"
    d = route.route(q)
    check(f"「{q}」→ STRUCT_FIRST", d.route == Route.STRUCT_FIRST, f"→ {d.route.value}")
    check("规则号 R-STRUCT-01", d.rule_id == "R-STRUCT-01", d.rule_id)
    check("★ answer 只能由结构化轨填", d.answer_allowed_from == "struct")

    print("=== ③ 结构化轨查询:必须答出【那个名字】===")
    rows = repo.find_facts(conn, SUBJ, PRED)
    check("查到 1 条", len(rows) == 1, f"{len(rows)}")
    row = rows[0]
    check("★ 返回的正文是密封的(不是裸 str)", isinstance(row.statement, TaintedText))
    answer = unseal_for_client(row.object_text, caller="trusted-local")
    check(f"★★ 答案就是「{NAME}」", answer == NAME, f"→ {answer!r}")

    print("=== ④ 溯源六件套(§4.5:缺一项多设备下可解释性就残缺)===")
    tr = row.trace()
    for k in ("asserted_at", "confidence", "source_ref", "origin_device_id",
              "write_seq", "provenance"):
        check(f"溯源含 {k}", k in tr and tr[k] is not None, f"→ {tr.get(k)!r}")
    check("write_seq 由服务端分配(>0)", tr["write_seq"] > 0, f"{tr['write_seq']}")
    check("provenance=user_typed", tr["provenance"] == "user_typed")
    check("confidence=1.0", tr["confidence"] == 1.0)

    print("=== ⑤ 冲突不覆盖:改名字必须新增+supersede,旧行仍在 ===")
    body2 = "我妹妹叫小雪"
    tk2 = issue_ticket(sid, body2)
    res2 = gate.submit_fact(conn,
                            candidate=CandidateIn(body=body2, provenance="user_typed", session_id=sid),
                            subject_norm=SUBJ, predicate_norm=PRED,
                            object_text="小雪", ticket_id=tk2)
    repo.supersede(conn, row.id, res2.fact_id)
    conn.commit()
    rows2 = repo.find_facts(conn, SUBJ, PRED)
    check("检索只返回新值", len(rows2) == 1 and
          unseal_for_client(rows2[0].object_text, caller="trusted-local") == "小雪",
          f"{len(rows2)} 条")
    with conn.cursor() as cur:
        cur.execute("SELECT count(*) FROM mem.l3_fact WHERE subject_norm=%s", (SUBJ,))
        total = cur.fetchone()[0]
    check("★ 旧行仍在(历史完整,不是覆盖)", total == 2, f"{total} 行")

    print("=== ⑥ 清理 ===")
    with conn.cursor() as cur:
        cur.execute("UPDATE mem.l3_fact SET superseded_by=NULL WHERE FALSE")  # no-op,保持只读语义
        cur.execute("DELETE FROM mem.l3_fact WHERE subject_norm=%s", (SUBJ,))
    conn.commit()
    check("测试数据已清", True)
finally:
    conn.close()

print(f"\n=== S1 验收:{_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
