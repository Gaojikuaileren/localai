# -*- coding: utf-8 -*-
"""S3 验收:完整记忆闸(分级 · E3 · 队列 · 熔断 · 冲突)

验收句(与 S1「我妹妹叫什么名字」、S2「上次聊的那个灯光问题」同级):

    ★ 「网页上读到的东西,不能自己变成你的记忆。」

      一条 web_content 候选 → **停在待审队列**,不进 l3_fact
        → 确认时**必须带票据**,且「它将取代哪条现有事实」摆在你面前
        → 拒绝则不产生任何内容行
      而 E3 要扫**所有落库字段**,不只是 body。

跑(需活库):PYTHONPATH=. python test_s3_acceptance.py
"""
import ast
import inspect
import sys

import gate
import repo
from gate import CandidateIn, GateReject, GateCircuitOpen
from tainted import seal, unseal_for_client

TAG = "s3acc"
_p = _f = 0


def check(name, cond, extra=""):
    global _p, _f
    if cond:
        _p += 1
        print(f"  PASS  {name}")
    else:
        _f += 1
        print(f"  FAIL  {name} {extra}")


def clean():
    """用 postgres 清场 —— ai_mem_local 故意没有 DELETE 权限(§12.4)。"""
    import os
    old = os.environ.get("LOCALAI_PG_USER")
    os.environ["LOCALAI_PG_USER"] = "postgres"
    c = repo.connect()
    with c.cursor() as cur:
        # ★ 必须按外键依赖序删,链是:
        #     write_ticket.pending_id  → pending_review.id
        #     pending_review.supersedes_ref → l3_fact.id
        #   而 supersedes_ref 被 tg_pending_immutable 冻结,**不能先置 NULL 再删** ——
        #   冻结是对的(确认前不许改取代目标),代价就是清理必须守依赖序。
        cur.execute("""DELETE FROM mem.write_ticket
                        WHERE session_id LIKE %s
                           OR pending_id IN (SELECT id FROM mem.pending_review
                                              WHERE session_id LIKE %s)""",
                    (f"{TAG}%", f"{TAG}%"))
        cur.execute("""DELETE FROM mem.pending_review
                        WHERE session_id LIKE %s
                           OR supersedes_ref IN (SELECT id FROM mem.l3_fact
                                                  WHERE subject_norm LIKE %s)""",
                    (f"{TAG}%", f"{TAG}%"))
        cur.execute("DELETE FROM mem.l3_fact WHERE subject_norm LIKE %s", (f"{TAG}%",))
        cur.execute("DELETE FROM mem.gate_rejection WHERE session_id LIKE %s", (f"{TAG}%",))
        cur.execute("DELETE FROM mem.circuit_breaker WHERE name='pending_backlog'")
        cur.execute("DELETE FROM mem.quarantine WHERE src_table='pending_review'")
    c.commit()
    c.close()
    if old is None:
        os.environ.pop("LOCALAI_PG_USER", None)
    else:
        os.environ["LOCALAI_PG_USER"] = old


try:
    conn = repo.connect()
except Exception as ex:
    print(f"  跳过:连不上 PG({type(ex).__name__})")
    sys.exit(0)
# ★ 清场单独放在连接之后:此前它俩共用一个 except,于是清场失败会被误报成
#   「连不上 PG」并静默 exit 0 —— 一个绿色的假通过。清场失败必须响亮。
clean()


def cand(body, prov, sess=TAG):
    return CandidateIn(body=body, provenance=prov, session_id=sess)


try:
    print("=== ① ★ E3 必须扫【所有落库字段】,不只是 body ===")
    IBAN = "DE89370400440532013000"
    # S1 的绕过路径:把凭证放进 subject_norm —— 它是独立形参,不受 extra='forbid' 保护
    for field, kw in (("subject_norm", dict(subject_norm=IBAN, predicate_norm="p", object_text="o")),
                      ("predicate_norm", dict(subject_norm="s", predicate_norm=IBAN, object_text="o")),
                      ("object_text", dict(subject_norm="s", predicate_norm="p", object_text=IBAN)),
                      ("body", dict(subject_norm="s", predicate_norm="p", object_text="o"))):
        body = IBAN if field == "body" else f"{TAG} 普通内容"
        try:
            gate.submit(conn, candidate=cand(body, "user_typed"), **kw)
            check(f"★★ 凭证藏在 {field} 里必须被拦", False, "竟然放行并落盘")
        except GateReject:
            conn.rollback()
            check(f"★★ 凭证藏在 {field} 里必须被拦", True)

    # 拒绝必须落库(不是只在内存)
    with conn.cursor() as cur:
        cur.execute("SELECT count(*) FROM mem.gate_rejection WHERE session_id=%s", (TAG,))
        n_rej = cur.fetchone()[0]
    check("★ E3 拒绝记录落库(重启不丢)", n_rej >= 4, f"{n_rej}")
    with conn.cursor() as cur:
        cur.execute("SELECT bool_and(sensitivity_domain='S2') FROM mem.gate_rejection WHERE session_id=%s", (TAG,))
        check("拒绝记录标 S2", bool(cur.fetchone()[0]))

    print("=== ② provenance 封闭枚举(S1 那版校验写反了)===")
    for bad in ("iban", "high_entropy", "whatsapp", ""):
        try:
            gate.submit(conn, candidate=cand(f"{TAG} x", bad),
                        subject_norm=f"{TAG}s", predicate_norm="p", object_text="o")
            check(f"★ provenance={bad!r} 必须被拒", False, "竟然通过")
        except (GateReject, Exception):
            conn.rollback()
            check(f"★ provenance={bad!r} 必须被拒", True)

    print("=== ③ ★★ 验收句:网页读来的东西不能自己进库 ===")
    r = gate.submit(conn, candidate=cand(f"{TAG} 网上说柏林是德国首都", "web_content"),
                    subject_norm=f"{TAG}柏林", predicate_norm="是什么", object_text="德国首都")
    conn.commit()
    check("★★ web_content 进的是【队列】不是 l3_fact", isinstance(r, gate.GateQueued),
          f"{type(r).__name__}")
    with conn.cursor() as cur:
        cur.execute("SELECT count(*) FROM mem.l3_fact WHERE subject_norm=%s", (f"{TAG}柏林",))
        check("★★ l3_fact 里没有它", cur.fetchone()[0] == 0)
    row = repo.get_pending(conn, r.pending_id)
    check("队列里有它且状态 pending", row and row.status == "pending")
    check("置信度封顶 0.4", row.source_confidence is not None and row.source_confidence <= 0.4,
          f"{row.source_confidence}")

    print("=== ④ 对照:用户直述直接进库(别把好人也挡了)===")
    r2 = gate.submit(conn, candidate=cand(f"{TAG} 我妹妹叫小雨", "user_typed"),
                     subject_norm=f"{TAG}妹妹", predicate_norm="名字", object_text="小雨")
    conn.commit()
    check("user_typed 直接进 l3_fact", isinstance(r2, gate.GateResult))
    check("无票据只能到 0.6", r2.source_confidence == 0.6, f"{r2.source_confidence}")

    print("=== ⑤ 冲突检测:「这条将取代谁」必须被填上 ===")
    r3 = gate.submit(conn, candidate=cand(f"{TAG} 网上说她叫小雪", "web_content"),
                     subject_norm=f"{TAG}妹妹", predicate_norm="名字", object_text="小雪")
    conn.commit()
    check("★ 同主语+谓词 → 检测到冲突并填 supersedes_ref",
          r3.supersedes_ref == r2.fact_id, f"{r3.supersedes_ref} vs {r2.fact_id}")

    print("=== ⑥ 确认必须带票据(1.0 的唯一来源)===")
    pr = repo.get_pending(conn, r3.pending_id)
    try:
        gate.confirm_pending(conn, r3.pending_id, ticket_id="伪造的票据",
                             expect_sha256=pr.candidate_sha256, session_id=TAG,
                             subject_norm=f"{TAG}妹妹", predicate_norm="名字", object_text="小雪")
        check("★★ 无有效票据不得确认", False, "竟然放行")
    except GateReject:
        conn.rollback()
        check("★★ 无有效票据不得确认", True)

    tk = gate.issue_confirm_ticket(conn, r3.pending_id, session_id=TAG)
    conn.commit()
    try:
        gate.confirm_pending(conn, r3.pending_id, ticket_id=tk,
                             expect_sha256="哈希对不上", session_id=TAG,
                             subject_norm=f"{TAG}妹妹", predicate_norm="名字", object_text="小雪")
        check("★ 哈希对不上不得确认", False, "竟然放行")
    except GateReject:
        conn.rollback()
        check("★ 哈希对不上不得确认", True)

    print("=== ⑦ 正常确认:进库 + 取代旧行 + 队列转终态 ===")
    tk2 = gate.issue_confirm_ticket(conn, r3.pending_id, session_id=TAG)
    conn.commit()
    ok = gate.confirm_pending(conn, r3.pending_id, ticket_id=tk2,
                              expect_sha256=pr.candidate_sha256, session_id=TAG,
                              subject_norm=f"{TAG}妹妹", predicate_norm="名字", object_text="小雪")
    conn.commit()
    check("确认后置信度 1.0", ok.source_confidence == 1.0, f"{ok.source_confidence}")
    check("attestation 为 panel_ticket", ok.attestation_kind == "panel_ticket")
    check("队列转 approved", repo.get_pending(conn, r3.pending_id).status == "approved")
    with conn.cursor() as cur:
        cur.execute("SELECT superseded_by FROM mem.l3_fact WHERE id=%s", (r2.fact_id,))
        check("★ 旧事实被取代", cur.fetchone()[0] == ok.fact_id)
        cur.execute("SELECT source_ref->>'origin_provenance' FROM mem.l3_fact WHERE id=%s",
                    (ok.fact_id,))
        check("★ 溯源保留了原始来源(provenance 记权威,source_ref 记来路)",
              cur.fetchone()[0] == "web_content")
    check("★ 票据不可双花(同一张再确认一次)",
          not repo.consume_ticket(conn, tk2, session_id=TAG, candidate_text="x"))
    conn.rollback()

    print("=== ⑧ 拒绝:不产生任何内容行 ===")
    r4 = gate.submit(conn, candidate=cand(f"{TAG} 该被拒的东西", "rag_chunk"),
                     subject_norm=f"{TAG}拒", predicate_norm="p", object_text="o")
    conn.commit()
    before = 0
    with conn.cursor() as cur:
        cur.execute("SELECT count(*) FROM mem.l3_fact WHERE subject_norm=%s", (f"{TAG}拒",))
        before = cur.fetchone()[0]
    p4 = repo.get_pending(conn, r4.pending_id)
    gate.reject_pending(conn, r4.pending_id, expect_sha256=p4.candidate_sha256)
    conn.commit()
    check("队列转 rejected", repo.get_pending(conn, r4.pending_id).status == "rejected")
    with conn.cursor() as cur:
        cur.execute("SELECT count(*) FROM mem.l3_fact WHERE subject_norm=%s", (f"{TAG}拒",))
        check("★ 拒绝不产生内容行", cur.fetchone()[0] == before)

    print("=== ⑨ 熔断:积压超限 → 响亮拒绝,但不挡用户直述 ===")
    base = repo.count_pending(conn)
    need = max(0, repo.PENDING_BACKLOG_LIMIT - base)
    for i in range(need):
        gate.submit(conn, candidate=cand(f"{TAG} 灌水 {i}", "web_content"),
                    subject_norm=f"{TAG}灌{i}", predicate_norm="p", object_text="o")
    conn.commit()
    check(f"积压达到上限 {repo.PENDING_BACKLOG_LIMIT}",
          repo.count_pending(conn) >= repo.PENDING_BACKLOG_LIMIT, f"{repo.count_pending(conn)}")
    check("★ 熔断已跳闸", repo.breaker_tripped(conn))
    try:
        gate.submit(conn, candidate=cand(f"{TAG} 再灌一条", "web_content"),
                    subject_norm=f"{TAG}再", predicate_norm="p", object_text="o")
        check("★★ 熔断后拒绝新候选", False, "竟然还收")
    except GateCircuitOpen:
        conn.rollback()
        check("★★ 熔断后拒绝新候选(响亮,不是静默丢弃)", True)

    r5 = gate.submit(conn, candidate=cand(f"{TAG} 我自己说的话", "user_typed"),
                     subject_norm=f"{TAG}自己", predicate_norm="p", object_text="o")
    conn.commit()
    check("★★ 熔断不挡本机用户直述(否则等于替攻击者把 DoS 做完)",
          isinstance(r5, gate.GateResult))

    print("=== ⑩ 熔断恢复需票据 ===")
    try:
        gate.clear_backlog_breaker(conn, ticket_id="伪造", session_id=TAG)
        check("★ 无票据不得恢复熔断", False, "竟然放行")
    except GateReject:
        conn.rollback()
        check("★ 无票据不得恢复熔断", True)
    tk3 = repo.issue_ticket(conn, session_id=TAG, candidate_text="clear_backlog_breaker")
    conn.commit()
    gate.clear_backlog_breaker(conn, ticket_id=tk3, session_id=TAG)
    conn.commit()
    check("带票据可恢复", not repo.breaker_tripped(conn))

    print("=== ⑪ 过期:转 expired + 正文进隔离区(不静默删除)===")
    # ★ 不能"事后把 expires_at 拨回过去" —— 那一列在 DB 层被冻结了(而且冻结是对的:
    #   可延长 = 让候选永久占着熔断额度;可缩短 = 把别人的候选逼到过期)。
    #   所以造一条"马上过期"的候选只能在**入队时**指定 TTL。
    before = repo.count_pending(conn)             # 此时队列里还有 ⑨ 灌进去的那批(TTL 14 天)
    pid_exp = repo.insert_pending(conn, repo.PendingWrite(
        body=seal(f"{TAG} 马上就过期的候选", sensitivity="S0", source="web_content"),
        provenance="web_content", source_confidence=0.4, sensitivity_domain="S0",
        session_id=TAG, ttl_days=-0.001))          # 负数 = 入队即已过期
    conn.commit()
    check("过期用例已入队", repo.count_pending(conn) == before + 1)

    n_exp = repo.expire_pending(conn)
    conn.commit()
    # ★ 只该过期【那一条】—— 顺手把没到期的也清掉,就等于给攻击者提供了清空证据的手段
    check("★ 只过期到期的那一条,不误伤未到期的", n_exp == 1, f"{n_exp}")
    check("被转的正是那一条", repo.get_pending(conn, pid_exp).status == "expired")
    check("★ 未到期的候选原样留在队列里", repo.count_pending(conn) == before,
          f"{repo.count_pending(conn)} vs {before}")
    with conn.cursor() as cur:
        cur.execute("""SELECT count(*) FROM mem.quarantine
                        WHERE src_table='pending_review' AND src_id=%s""", (pid_exp,))
        check("★ 正文进了隔离区(不是静默删除)", cur.fetchone()[0] == 1)

    print("=== ⑫ 架构断言 ===")
    src = inspect.getsource(gate)
    # 1.0 只能由 mint_confidence 产出
    tree = ast.parse(src)
    minting = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.FunctionDef):
            for sub in ast.walk(node):
                if isinstance(sub, ast.Constant) and isinstance(sub.value, float) \
                        and sub.value == 1.0:
                    minting.add(node.name)
    check("★ 只有 mint_confidence 里出现字面量 1.0", minting <= {"mint_confidence"},
          f"{sorted(minting)}")
    # 没有批量确认的姊妹函数
    check("★ 不存在批量确认函数",
          not any(n in src for n in ("confirm_many", "confirm_all", "confirm_batch")))
    # scan_surface 覆盖所有落库字段
    sig = inspect.signature(gate.scan_surface).parameters
    check("scan_surface 覆盖 body/subject/predicate/object",
          {"body", "subject_norm", "predicate_norm", "object_text"} <= set(sig))
    check("submit 用 scan_surface 而不是自己拼字符串",
          "scan_surface(" in inspect.getsource(gate.submit))

    print("=== ⑬ 两套票据存储的一致性(防漂移)===")
    mem_store = gate.TicketStore()
    for label, issue, consume in (
        ("内存", lambda s, c: mem_store.issue(s, c),
                 lambda t, s, c: mem_store.consume(t, s, c)),
        ("PG",   lambda s, c: repo.issue_ticket(conn, session_id=s, candidate_text=c),
                 lambda t, s, c: repo.consume_ticket(conn, t, session_id=s, candidate_text=c)),
    ):
        t = issue("sessA", "文本X")
        check(f"[{label}] 首次消费成功", consume(t, "sessA", "文本X"))
        check(f"[{label}] 二次消费失败", not consume(t, "sessA", "文本X"))
        t2 = issue("sessA", "文本X")
        check(f"[{label}] 换会话失败", not consume(t2, "sessB", "文本X"))
        check(f"[{label}] 换文本失败", not consume(t2, "sessA", "文本Y"))
    conn.commit()

finally:
    try:
        conn.close()
    except Exception:
        pass
    clean()

print(f"\n=== S3 验收:{_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
