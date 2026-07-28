# -*- coding: utf-8 -*-
"""S8 验收:L4 程序记忆独立写路径 + 哈希绑定批准 + executor

验收句(与前七条同级):

  ★ 「教会一个工作流后,源码被改一个字节,执行前哈希对不上就拒绝执行;
     局域网设备根本【碰不到】写 L4 这个能力。」

跑(需活库):PYTHONPATH=. python test_s8_acceptance.py
"""
import os
import sys

import l4_proc
import repo
from l4_proc import L4Denied, L4Error, L4HashMismatch
from tainted import CallerTier

TAG = "s8proc"
_p = _f = 0


def check(name, cond, extra=""):
    global _p, _f
    if cond:
        _p += 1
        print(f"  PASS  {name}")
    else:
        _f += 1
        print(f"  FAIL  {name} {extra}")


def denied(fn, name, exc):
    try:
        fn()
        check(name, False, "竟然没拒")
    except exc:
        check(name, True)
    except Exception as e:
        check(name, False, f"异常类型不对: {type(e).__name__}: {e}")


def clean():
    old = os.environ.get("LOCALAI_PG_USER")
    os.environ["LOCALAI_PG_USER"] = "postgres"
    c = repo.connect()
    with c.cursor() as cur:
        cur.execute("DELETE FROM mem.l4_approval WHERE procedure_id IN "
                    "(SELECT id FROM mem.l4_procedure WHERE name LIKE %s)", (f"{TAG}%",))
        cur.execute("DELETE FROM mem.l4_procedure WHERE name LIKE %s", (f"{TAG}%",))
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

BODY = {"steps": [{"op": "noop", "arg": "step1"}, {"op": "echo", "arg": "hello"}]}

try:
    print("=== ① ★ 提议 ≠ 批准:未批准的过程拒绝执行 ===")
    pid = l4_proc.propose(conn, caller=CallerTier.TRUSTED_LOCAL,
                          name=f"{TAG}.workflow", version="1.0",
                          git_ref="code@abc123", body=BODY)
    conn.commit()
    check("★ 提议成功", isinstance(pid, int))
    proc = l4_proc.get(conn, pid)
    check("★ 此时未批准", proc.is_approved is False)
    denied(lambda: l4_proc.execute(conn, caller=CallerTier.TRUSTED_LOCAL, procedure_id=pid),
           "★★ 未批准的过程 → 拒绝执行", L4HashMismatch)

    print("=== ② ★ 批准:记录内容哈希 + 签名 ===")
    l4_proc.approve(conn, caller=CallerTier.TRUSTED_LOCAL, procedure_id=pid,
                    approved_by="test-user")
    conn.commit()
    proc = l4_proc.get(conn, pid)
    check("★ 批准后 last_approved_sha256 = 当前内容哈希",
          proc.last_approved_sha256 == l4_proc.content_sha256(proc.body))
    check("★ 有签名", proc.signature_ref is not None)
    check("★★ 签名验证通过",
          l4_proc.verify_signature(proc.name, proc.version, proc.sha256, proc.signature_ref))

    print("=== ③ ★★ 已批准的过程可以执行 ===")
    res = l4_proc.execute(conn, caller=CallerTier.TRUSTED_LOCAL, procedure_id=pid)
    check("★ 执行成功,返回每步结果", len(res) == 2 and res[1] == {"echo": "hello"})

    print("=== ④ ★★★ 验收句:源码改一个字节 → 执行前哈希对不上 → 拒执行 ===")
    # 用 postgres 直接篡改 body(模拟"批准后内容被改")—— 改动一个字节
    _old = os.environ.get("LOCALAI_PG_USER")
    os.environ["LOCALAI_PG_USER"] = "postgres"
    c2 = repo.connect()
    tampered = {"steps": [{"op": "noop", "arg": "step1"}, {"op": "echo", "arg": "hellp"}]}  # o→p
    with c2.cursor() as cur:
        cur.execute("UPDATE mem.l4_procedure SET body=%s, sha256=%s WHERE id=%s",
                    (repo.as_jsonb(tampered), l4_proc.content_sha256(tampered), pid))
    c2.commit()
    c2.close()
    if _old is None:
        os.environ.pop("LOCALAI_PG_USER", None)
    else:
        os.environ["LOCALAI_PG_USER"] = _old

    proc2 = l4_proc.get(conn, pid)
    check("★ 篡改后当前哈希 ≠ 批准哈希",
          l4_proc.content_sha256(proc2.body) != proc2.last_approved_sha256)
    denied(lambda: l4_proc.execute(conn, caller=CallerTier.TRUSTED_LOCAL, procedure_id=pid),
           "★★★ 内容被改一字节 → 执行前哈希复核失败 → 拒绝执行", L4HashMismatch)

    print("=== ⑤ ★ 重新批准后又可执行(字节级,不是永久锁死)===")
    l4_proc.approve(conn, caller=CallerTier.TRUSTED_LOCAL, procedure_id=pid,
                    approved_by="test-user")
    conn.commit()
    res2 = l4_proc.execute(conn, caller=CallerTier.TRUSTED_LOCAL, procedure_id=pid)
    check("★ 重新批准后执行,返回改后的值", res2[1] == {"echo": "hellp"})

    print("=== ⑥ ★★ 伪造签名 → 拒执行 ===")
    _old = os.environ.get("LOCALAI_PG_USER")
    os.environ["LOCALAI_PG_USER"] = "postgres"
    c3 = repo.connect()
    with c3.cursor() as cur:
        cur.execute("UPDATE mem.l4_procedure SET signature_ref=%s WHERE id=%s",
                    ("deadbeef" * 8, pid))
    c3.commit()
    c3.close()
    if _old is None:
        os.environ.pop("LOCALAI_PG_USER", None)
    else:
        os.environ["LOCALAI_PG_USER"] = _old
    denied(lambda: l4_proc.execute(conn, caller=CallerTier.TRUSTED_LOCAL, procedure_id=pid),
           "★★ 签名被换成伪造值 → 拒绝执行", L4HashMismatch)

    print("=== ⑦ ★★ 验收句下半:局域网设备碰不到 L4 ===")
    denied(lambda: l4_proc.propose(conn, caller=CallerTier.LAN_DEVICE,
                                   name=f"{TAG}.x", version="1", git_ref="x", body=BODY),
           "★★ 局域网设备不得【提议】L4", L4Denied)
    denied(lambda: l4_proc.approve(conn, caller=CallerTier.LAN_DEVICE, procedure_id=pid,
                                   approved_by="x"),
           "★★ 局域网设备不得【批准】L4", L4Denied)
    denied(lambda: l4_proc.execute(conn, caller=CallerTier.LAN_DEVICE, procedure_id=pid),
           "★★ 局域网设备不得【执行】L4", L4Denied)
    denied(lambda: l4_proc.propose(conn, caller=CallerTier.CHANNEL_RELAY,
                                   name=f"{TAG}.y", version="1", git_ref="y", body=BODY),
           "★★ 外联通道更不得碰 L4", L4Denied)

    print("=== ⑧ ★★ 结构性远程排除(不是「默认排除」)===")
    # ai_mem_remote 对 l4_procedure 零授权
    _old = os.environ.get("LOCALAI_PG_USER")
    os.environ["LOCALAI_PG_USER"] = "postgres"
    cx = repo.connect()
    with cx.cursor() as cur:
        cur.execute("""SELECT count(*) FROM information_schema.role_table_grants
                        WHERE grantee='ai_mem_remote' AND table_schema='mem'
                          AND table_name IN ('l4_procedure','l4_approval')""")
        n_grants = cur.fetchone()[0]
        cur.execute("""SELECT count(*) FROM pg_views WHERE schemaname='mem'
                        AND viewname='v_memory_nons2'
                        AND definition LIKE '%l4_procedure%'""")
        in_view = cur.fetchone()[0]
    cx.commit(); cx.close()
    if _old is None:
        os.environ.pop("LOCALAI_PG_USER", None)
    else:
        os.environ["LOCALAI_PG_USER"] = _old
    check("★★ ai_mem_remote 对 l4 表零直接授权", n_grants == 0, f"{n_grants} 处")
    check("★★ l4_procedure 不在 v_memory_nons2 视图里(结构性排除)", in_view == 0)

    print("=== ⑨ ★ 批准链 append-only,已批准不得清空 ===")
    _old = os.environ.get("LOCALAI_PG_USER")
    os.environ["LOCALAI_PG_USER"] = "postgres"
    cz = repo.connect()
    ok = False
    try:
        with cz.cursor() as cur:
            cur.execute("UPDATE mem.l4_procedure SET last_approved_sha256=NULL WHERE id=%s", (pid,))
        cz.commit()
    except Exception:
        cz.rollback(); ok = True
    cz.close()
    if _old is None:
        os.environ.pop("LOCALAI_PG_USER", None)
    else:
        os.environ["LOCALAI_PG_USER"] = _old
    check("★ 已批准的 last_approved_sha256 不得清空(否则绕过审计)", ok)

    print("=== ⑩ ★ 未签名的已批准行进不了库(CHECK)===")
    _old = os.environ.get("LOCALAI_PG_USER")
    os.environ["LOCALAI_PG_USER"] = "postgres"
    cw = repo.connect()
    ok2 = False
    try:
        with cw.cursor() as cur:
            cur.execute("""INSERT INTO mem.l4_procedure
                             (name,version,git_ref,sha256,sensitivity_domain,last_approved_sha256)
                           VALUES (%s,'9','x','deadbeef','S0','deadbeef')""", (f"{TAG}.unsigned",))
        cw.commit()
    except Exception:
        cw.rollback(); ok2 = True
    cw.close()
    if _old is None:
        os.environ.pop("LOCALAI_PG_USER", None)
    else:
        os.environ["LOCALAI_PG_USER"] = _old
    check("★★ 已批准(last_approved 非空)但无签名 → 被 CHECK 拒", ok2)

finally:
    try:
        conn.close()
    except Exception:
        pass
    clean()

print(f"\n=== S8 验收:{_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
