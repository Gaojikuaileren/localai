# -*- coding: utf-8 -*-
"""S6 验收:冷启动初始化会话(只跑一次)

验收句(与前五条同级):

  ★ 「冷启动只跑一次;录完『妹妹叫小雨』当场能查、能改;录『我家住 X』自动进 S2
     手机读不到;再跑一次冷启动被拒,不会种出第二行妹妹。」

跑(需活库):PYTHONPATH=. python test_s6_acceptance.py
"""
import os
import sys

import coldstart
import panel
import repo
import route
from coldstart import ColdStartError, Seed
from tainted import CallerTier, unseal_for_client

TAG = "s6acc"
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
    old = os.environ.get("LOCALAI_PG_USER")
    os.environ["LOCALAI_PG_USER"] = "postgres"
    c = repo.connect()
    with c.cursor() as cur:
        cur.execute("DELETE FROM mem.write_ticket WHERE session_id LIKE %s", (f"{TAG}%",))
        cur.execute("DELETE FROM mem.l3_fact WHERE subject_norm LIKE %s OR statement LIKE %s",
                    (f"%{TAG}%", f"%{TAG}%"))
        cur.execute("DELETE FROM mem.system_state WHERE value->>'session_id' LIKE %s", (f"{TAG}%",))
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
clean()

SID = f"{TAG}-session"
seeds = [
    Seed(subject="妹妹", predicate="名字", statement=f"{TAG} 我妹妹叫小雨", object_text="小雨"),
    Seed(subject="我", predicate="住址",
         statement=f"{TAG} 我家住 Musterstraße 12, 10115 Berlin",
         object_text="Musterstraße 12, 10115 Berlin"),
]

try:
    print("=== ① 冷启动前:未初始化 ===")
    check("初始状态 is_initialized == False", coldstart.is_initialized(conn) is False)

    print("=== ② ★ 播种:每条经 Gate + 票据铸 1.0 ===")
    ids = coldstart.run_cold_start(conn, seeds, session_id=SID)
    conn.commit()
    check("写入两条种子", len(ids) == 2)
    with conn.cursor() as cur:
        cur.execute("SELECT source_confidence, attestation_kind FROM mem.l3_fact WHERE id=%s",
                    (ids[0],))
        conf, att = cur.fetchone()
    check("★★ 种子置信度 1.0(§4.9.1 当场确认,不是 0.6)", float(conf) == 1.0, f"{conf}")
    check("★ attestation=panel_ticket", att == "panel_ticket")

    print("=== ③ ★★ 归一化对齐:『妹妹』能被『二妹叫什么』这类查询命中 ===")
    # 冷启动写的是 subject='妹妹';用户查询用别的称谓,折叠后应落同一键
    lex = route.Lexicon.load()
    # 直接验证写入侧 subject_norm 与查询侧折叠结果一致
    written_subj = lex.apply_fold(route.normalize("妹妹"))
    with conn.cursor() as cur:
        cur.execute("SELECT subject_norm FROM mem.l3_fact WHERE id=%s", (ids[0],))
        db_subj = cur.fetchone()[0]
    check("★★ 写入的 subject_norm == 查询侧折叠结果(读写落同一键)",
          db_subj == written_subj, f"db={db_subj!r} vs {written_subj!r}")
    rows = repo.find_facts(conn, db_subj, lex.apply_fold(route.normalize("名字")))
    check("★ 用归一化后的键能查到那条", len(rows) == 1)
    if rows:
        ans = unseal_for_client(rows[0].object_text, caller=CallerTier.TRUSTED_LOCAL)
        check("★★ 答出「小雨」", ans == "小雨", ans)

    print("=== ④ ★★ 地址种子自动进 S2,手机读不到 ===")
    with conn.cursor() as cur:
        cur.execute("SELECT sensitivity_domain FROM mem.l3_fact WHERE id=%s", (ids[1],))
        check("★★ 地址种子自动标 S2(机密定级)", cur.fetchone()[0] == "S2")
    # 局域网设备(≈手机档位之上)浏览不到 S2
    lan_ids = {c.id for c in panel.browse_facts(conn, caller=CallerTier.LAN_DEVICE)}
    check("★★ 地址种子:局域网/远程档位看不到", ids[1] not in lan_ids)

    print("=== ⑤ ★★ 只跑一次:再跑被拒,不种出第二行 ===")
    check("现在 is_initialized == True", coldstart.is_initialized(conn) is True)
    n_before = 0
    with conn.cursor() as cur:
        cur.execute("SELECT count(*) FROM mem.l3_fact WHERE statement LIKE %s", (f"%{TAG}%",))
        n_before = cur.fetchone()[0]
    try:
        coldstart.run_cold_start(conn, seeds, session_id=SID)
        conn.commit()
        check("★★ 重跑冷启动 → 必须拒绝", False, "竟然又跑了一遍")
    except ColdStartError:
        conn.rollback()
        check("★★ 重跑冷启动 → 必须拒绝(fail-closed,不静默复制)", True)
    with conn.cursor() as cur:
        cur.execute("SELECT count(*) FROM mem.l3_fact WHERE statement LIKE %s", (f"%{TAG}%",))
        check("★★ 记忆条数没有变多(没种出第二行妹妹)",
              cur.fetchone()[0] == n_before, f"{n_before} → {cur.fetchone()[0] if False else '?'}")

    print("=== ⑥ ★ 标记不可翻转(一次性,连 ai_mem_local 也不能删)===")
    try:
        with conn.cursor() as cur:
            cur.execute("DELETE FROM mem.system_state WHERE key=%s", (coldstart.COLD_START_KEY,))
        conn.commit()
        check("★ ai_mem_local 不得删除冷启动标记(否则可重开)", False, "竟然删掉了")
    except Exception:
        conn.rollback()
        check("★ ai_mem_local 不得删除冷启动标记(权限层拒绝)", True)

    print("=== ⑦ ★ 录后当场能改(接 S5 编辑)===")
    new = panel.edit_fact(conn, caller=CallerTier.TRUSTED_LOCAL, old_id=ids[0],
                          session_id=SID, new_body=f"{TAG} 我妹妹叫小雪",
                          subject_norm=lex.apply_fold(route.normalize("妹妹")),
                          predicate_norm=lex.apply_fold(route.normalize("名字")),
                          object_text="小雪")
    conn.commit()
    check("★ 冷启动种子可经面板编辑(supersede)", new.source_confidence == 1.0)
    rows2 = repo.find_facts(conn, lex.apply_fold(route.normalize("妹妹")),
                            lex.apply_fold(route.normalize("名字")))
    check("★★ 改后只剩新值,不并列", len(rows2) == 1 and
          unseal_for_client(rows2[0].object_text, caller=CallerTier.TRUSTED_LOCAL) == "小雪")

finally:
    try:
        conn.close()
    except Exception:
        pass
    clean()

print(f"\n=== S6 验收:{_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
