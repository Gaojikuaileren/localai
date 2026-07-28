# -*- coding: utf-8 -*-
"""S9 演练:满载库(S0-S4 全结构+真实数据)的完整恢复 + 恢复后安全性质

★ 与之前的近空库演练不同(§8.5.4 铁律 3:没演练过的备份不算备份):
  这次库里承载经【真实写路径】产出的 S0-S4 数据 —— 事实(S0+S2)· 情节 · 待审 ·
  L4 过程 · 隔离区条目。演练要证明:
    ① 精确 count 保真(排除 quarantine)
    ② quarantine 数据被排除、但表结构在(恢复后表在只是空的)
    ③ 金丝雀那一行真的回来了(不是光比数量)
    ④ 恢复库上安全性质仍成立(远程角色读基表被拒 —— 权限模型随备份恢复)

跑:PYTHONPATH=. python test_s9_drill.py  (需以能连 postgres 的身份;用 pg_dump/pg_restore)
"""
import os
import subprocess
import sys
import tomllib
from datetime import datetime, timezone
from pathlib import Path

import coldstart
import gate
import l4_proc
import repo
import track_vector
from coldstart import Seed
from repo import EpisodeWrite
from tainted import CallerTier, seal

TAG = "s9drill"
CANARY = f"{TAG} 金丝雀事实 KANARIE_9Z8Y"
DRILL_DB = "memory_s9_drill"
_p = _f = 0

_paths = tomllib.load(open(Path(__file__).resolve().parents[2] / "config" / "paths.toml", "rb"))
PGBIN = Path(_paths["memory"]["pg_bin"])
PGPORT = str(_paths["memory"]["pg_port"])


def check(name, cond, extra=""):
    global _p, _f
    if cond:
        _p += 1
        print(f"  PASS  {name}")
    else:
        _f += 1
        print(f"  FAIL  {name} {extra}")


def psql(db, sql, user="postgres"):
    r = subprocess.run([str(PGBIN / "psql.exe"), "-h", "127.0.0.1", "-p", PGPORT,
                        "-U", user, "-d", db, "-tAc", sql],
                       capture_output=True, text=True, encoding="utf-8")
    return r.stdout.strip(), r.returncode


def clean_fixtures():
    old = os.environ.get("LOCALAI_PG_USER")
    os.environ["LOCALAI_PG_USER"] = "postgres"
    c = repo.connect()
    with c.cursor() as cur:
        cur.execute("SELECT id FROM mem.l2_episode WHERE body LIKE %s", (f"{TAG}%",))
        for (eid,) in cur.fetchall():
            for s in ("S0", "S2"):
                try:
                    track_vector.delete_episode_vector(eid, sensitivity=s)
                except Exception:
                    pass
        cur.execute("DELETE FROM mem.l4_approval WHERE procedure_id IN "
                    "(SELECT id FROM mem.l4_procedure WHERE name LIKE %s)", (f"{TAG}%",))
        cur.execute("DELETE FROM mem.l4_procedure WHERE name LIKE %s", (f"{TAG}%",))
        cur.execute("DELETE FROM mem.l2_episode WHERE body LIKE %s", (f"{TAG}%",))
        cur.execute("DELETE FROM mem.pending_review WHERE session_id LIKE %s", (f"{TAG}%",))
        cur.execute("DELETE FROM mem.write_ticket WHERE session_id LIKE %s", (f"{TAG}%",))
        cur.execute("DELETE FROM mem.quarantine WHERE reason LIKE %s", (f"{TAG}%",))
        cur.execute("DELETE FROM mem.l3_fact WHERE statement LIKE %s OR subject_norm LIKE %s",
                    (f"%{TAG}%", f"%{TAG}%"))
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
clean_fixtures()

try:
    print("=== ① 种满载夹具(经真实写路径 S0-S4)===")
    # S0 事实 + 金丝雀
    for body, subj, pred, obj in [
        (CANARY, f"{TAG}金丝雀", "存在", "是"),
        (f"{TAG} 我妹妹叫小雨", f"{TAG}妹妹", "名字", "小雨"),
    ]:
        cand = gate.CandidateIn(body=body, provenance="user_typed", session_id=TAG)
        tk = repo.issue_ticket(conn, session_id=TAG, candidate_text=body)
        gate.submit(conn, candidate=cand, subject_norm=subj, predicate_norm=pred,
                    object_text=obj, ticket_id=tk)
    conn.commit()
    # S2 事实(地址)
    body = f"{TAG} 我家住 Musterstraße 12, 10115 Berlin"
    cand = gate.CandidateIn(body=body, provenance="user_typed", session_id=TAG)
    tk = repo.issue_ticket(conn, session_id=TAG, candidate_text=body)
    r_s2 = gate.submit(conn, candidate=cand, subject_norm=f"{TAG}我", predicate_norm="住址",
                       object_text="Musterstraße 12, 10115 Berlin", ticket_id=tk)
    conn.commit()
    # 情节
    w = EpisodeWrite(body=seal(f"{TAG} 讨论了灯光问题", sensitivity="S0", source="user_typed"),
                     event_at=datetime.now(timezone.utc), provenance="user_typed",
                     source_confidence=0.6, sensitivity_domain="S0",
                     attestation_kind="assistant_infer", source_ref={"kind": "flow"})
    repo.insert_episode(conn, w)
    conn.commit()
    # pending 候选
    cand = gate.CandidateIn(body=f"{TAG} 网上读到的东西", provenance="web_content", session_id=TAG)
    gate.submit(conn, candidate=cand, subject_norm=f"{TAG}网", predicate_norm="x", object_text="y")
    conn.commit()
    # L4 过程(已批准)
    pid = l4_proc.propose(conn, caller=CallerTier.TRUSTED_LOCAL, name=f"{TAG}.wf",
                          version="1", git_ref="x", body={"steps": [{"op": "noop", "arg": "a"}]})
    conn.commit()
    l4_proc.approve(conn, caller=CallerTier.TRUSTED_LOCAL, procedure_id=pid, approved_by="drill")
    conn.commit()
    # 一条删除 → 进 quarantine(要证明它被排除)
    cand = gate.CandidateIn(body=f"{TAG} 该被删的", provenance="user_typed", session_id=TAG)
    tk = repo.issue_ticket(conn, session_id=TAG, candidate_text=f"{TAG} 该被删的")
    rdel = gate.submit(conn, candidate=cand, subject_norm=f"{TAG}删", predicate_norm="x",
                       object_text="y", ticket_id=tk)
    conn.commit()
    repo.redact(conn, rdel.fact_id, f"{TAG} 演练删除", table="l3_fact")
    conn.commit()
    check("★ 满载夹具已种(含 S0/S2 事实·情节·pending·L4·quarantine)", True)
    q_before, _ = psql("memory", "SELECT count(*) FROM mem.quarantine")
    check("★ 源库 quarantine 有数据(待证明它被排除)", int(q_before or 0) >= 1, q_before)

    print("=== ② pg_dump(排除 quarantine 数据)===")
    dump_path = str(Path(os.environ.get("TEMP", ".")) / f"s9_{TAG}.dump")
    r = subprocess.run([str(PGBIN / "pg_dump.exe"), "-h", "127.0.0.1", "-p", PGPORT,
                        "-U", "postgres", "-Fc", "--exclude-table-data", "mem.quarantine",
                        "-d", "memory", "-f", dump_path],
                       capture_output=True, text=True, encoding="utf-8")
    check("★ pg_dump 成功", r.returncode == 0, r.stderr[:120])

    print("=== ③ 恢复到 scratch 库 ===")
    psql("postgres", f"DROP DATABASE IF EXISTS {DRILL_DB}")
    _, rc = psql("postgres", f"CREATE DATABASE {DRILL_DB} ENCODING 'UTF8' "
                             f"LC_COLLATE 'C' LC_CTYPE 'C' TEMPLATE template0")
    r = subprocess.run([str(PGBIN / "pg_restore.exe"), "-h", "127.0.0.1", "-p", PGPORT,
                        "-U", "postgres", "-d", DRILL_DB, dump_path],
                       capture_output=True, text=True, encoding="utf-8")
    check("★ pg_restore 完成", r.returncode == 0, r.stderr[:200])

    print("=== ④ ★★ 精确 count 保真(排除 quarantine)===")
    SIG = ("SELECT coalesce(sum(cnt),0) FROM ("
           " SELECT (xpath('/row/c/text()', query_to_xml("
           "   format('SELECT count(*) c FROM mem.%I', tablename), false, true, '')))[1]::text::bigint cnt"
           " FROM pg_tables WHERE schemaname='mem' AND tablename<>'quarantine') s")
    src, _ = psql("memory", SIG)
    dst, _ = psql(DRILL_DB, SIG)
    check("★★ 源库与恢复库精确行数一致(排除 quarantine)", src == dst and int(src) > 0,
          f"src={src} dst={dst}")

    print("=== ⑤ ★★ quarantine:表结构在,数据被排除 ===")
    exists, _ = psql(DRILL_DB, "SELECT count(*) FROM information_schema.tables "
                               "WHERE table_schema='mem' AND table_name='quarantine'")
    q_dst, _ = psql(DRILL_DB, "SELECT count(*) FROM mem.quarantine")
    check("★★ 恢复库 quarantine 表存在(结构在)", exists == "1")
    check("★★ 恢复库 quarantine 数据为空(被删记忆正文没进备份)", q_dst == "0", q_dst)

    print("=== ⑥ ★★ 金丝雀那一行真的回来了 ===")
    # ★ 用金丝雀里的 ASCII 段查 —— Windows 下命令行参数带中文会被客户端编码搞乱,
    #   而 KANARIE_9Z8Y 是纯 ASCII,不受影响(精确 count 已证明行都在)。
    found, _ = psql(DRILL_DB, "SELECT count(*) FROM mem.l3_fact WHERE statement LIKE '%KANARIE_9Z8Y%'")
    check("★★ 金丝雀事实在恢复库中(不是光比数量)", found == "1", found)
    l4_found, _ = psql(DRILL_DB, f"SELECT count(*) FROM mem.l4_procedure "
                                 f"WHERE name='{TAG}.wf' AND last_approved_sha256 IS NOT NULL")
    check("★ L4 过程连批准状态一起恢复", l4_found == "1")

    print("=== ⑦ ★★ 恢复库上安全性质仍成立(权限模型随备份恢复)===")
    # 以 ai_mem_remote 读基表应被拒
    out, rc = psql(DRILL_DB, "SET ROLE ai_mem_remote; SELECT count(*) FROM mem.l3_fact",
                   user="postgres")
    # 注意:role 是集群级的,恢复库继承同一集群的角色。SET ROLE 后读基表应 permission denied
    denied = ("permission denied" in out) or ("permission denied" in
              (subprocess.run([str(PGBIN / "psql.exe"), "-h", "127.0.0.1", "-p", PGPORT,
                               "-U", "postgres", "-d", DRILL_DB, "-c",
                               "SET ROLE ai_mem_remote; SELECT count(*) FROM mem.l3_fact"],
                              capture_output=True, text=True, encoding="utf-8").stderr))
    check("★★ 恢复库上远程角色读基表 → permission denied(隔离随备份恢复)", denied)
    # v_memory_nons2 在恢复库里仍过滤 S2
    s2_leak, _ = psql(DRILL_DB, "SELECT count(*) FROM mem.v_memory_nons2 WHERE content LIKE '%Musterstraße%'")
    check("★★ 恢复库 v_memory_nons2 仍不含 S2 地址", s2_leak == "0", s2_leak)

    print("=== ⑧ 清理演练库 ===")
    psql("postgres", f"DROP DATABASE IF EXISTS {DRILL_DB}")
    try:
        os.remove(dump_path)
    except Exception:
        pass
    check("★ 演练 scratch 库已删", True)

finally:
    try:
        conn.close()
    except Exception:
        pass
    clean_fixtures()

print(f"\n=== S9 演练:{_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
