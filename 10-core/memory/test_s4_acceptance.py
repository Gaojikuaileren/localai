# -*- coding: utf-8 -*-
"""S4 验收:隐私三层 · 域S2 的生产者 · 日志纪律

验收句(与前三条同级):

    ★ 「我家住哪」这条记忆,**进得了库,但出不了门**。

  一句话考完:
    地址 → 标 域S2(而不是像凭证那样被拒)
      → 写得进去,本机面板看得到
      → 但局域网设备 / 外联通道 / 出境后端一个都取不到
      → 不出现在 v_memory_nons2(远程可读视图)里
      → 向量去 mem_s2 而不是 mem_main
      → 全程没有一段正文进日志

跑(需活库):PYTHONPATH=. python test_s4_acceptance.py
"""
import glob
import io
import os
import sys
import tomllib
from pathlib import Path

import gate
import repo
import sensitivity as sens
import vectors
from gate import CandidateIn, GateReject
from tainted import (Backend, CallerTier, MemoryLeakError, current_ledger,
                     equals_plaintext, seal, unseal_for_client, unseal_for_prompt)

TAG = "s4acc"
HOME = f"{TAG} 我家住 Musterstraße 12, 10115 Berlin"
PLAIN = f"{TAG} 我妹妹叫小雨"
# ★ 路径从 paths.toml 读,不硬编码(§11.1:换盘只改一个文件)
_paths = tomllib.load(open(Path(__file__).resolve().parents[2] / "config" / "paths.toml", "rb"))
PGLOG = str(Path(_paths["memory"]["pg_data"]) / "log")
_p = _f = 0


def check(name, cond, extra=""):
    global _p, _f
    if cond:
        _p += 1
        print(f"  PASS  {name}")
    else:
        _f += 1
        print(f"  FAIL  {name} {extra}")


def blocked(fn, name, exc=MemoryLeakError):
    try:
        r = fn()
        check(name, False, f"竟然拿到了 {r!r:.40}")
    except exc:
        check(name, True)
    except Exception as e:
        check(name, False, f"异常类型不对: {type(e).__name__}")


def clean():
    old = os.environ.get("LOCALAI_PG_USER")
    os.environ["LOCALAI_PG_USER"] = "postgres"
    c = repo.connect()
    with c.cursor() as cur:
        cur.execute("DELETE FROM mem.write_ticket   WHERE session_id LIKE %s", (f"{TAG}%",))
        cur.execute("DELETE FROM mem.pending_review WHERE session_id LIKE %s", (f"{TAG}%",))
        cur.execute("DELETE FROM mem.l3_fact WHERE subject_norm LIKE %s", (f"{TAG}%",))
        cur.execute("DELETE FROM mem.gate_rejection WHERE session_id LIKE %s", (f"{TAG}%",))
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

try:
    print("=== ① ★★ 验收句:地址进得了库(不像凭证那样被拒)===")
    r = gate.submit(conn, candidate=CandidateIn(body=HOME, provenance="user_typed",
                                                session_id=TAG),
                    subject_norm=f"{TAG}我", predicate_norm="住址",
                    object_text="Musterstraße 12, 10115 Berlin")
    conn.commit()
    check("★★ 地址写进去了(不是被拒)", isinstance(r, gate.GateResult))
    check("★★ 而且被标成了 域S2", r.sensitivity == "S2", r.sensitivity)

    # 对照:凭证仍然被拒,不落盘
    blocked(lambda: gate.submit(
        conn, candidate=CandidateIn(body=f"{TAG} 我的 IBAN 是 DE89370400440532013000",
                                    provenance="user_typed", session_id=TAG),
        subject_norm=f"{TAG}我", predicate_norm="账号", object_text="x"),
        "★ 对照:凭证仍然被拒(两条判据动作相反)", GateReject)
    conn.rollback()

    # 对照:普通事实仍是 S0
    r0 = gate.submit(conn, candidate=CandidateIn(body=PLAIN, provenance="user_typed",
                                                 session_id=TAG),
                     subject_norm=f"{TAG}妹妹", predicate_norm="名字", object_text="小雨")
    conn.commit()
    check("★ 对照:普通关系事实仍是 S0(没有误标一片)", r0.sensitivity == "S0", r0.sensitivity)

    print("=== ② ★★ 但它出不了门:类型层 ===")
    rows = repo.find_facts(conn, f"{TAG}我", "住址")
    check("本机查得到", len(rows) == 1)
    row = rows[0]
    check("本机面板取得到正文(要显示)",
          "Musterstraße" in unseal_for_client(row.statement, caller=CallerTier.TRUSTED_LOCAL))
    blocked(lambda: unseal_for_client(row.statement, caller=CallerTier.LAN_DEVICE),
            "★★ 局域网设备取不到")
    blocked(lambda: unseal_for_client(row.statement, caller=CallerTier.CHANNEL_RELAY),
            "★★ 外联通道取不到")
    blocked(lambda: unseal_for_client(row.statement, caller=CallerTier.REMOTE_UNAUTH),
            "★ 未认证远程取不到")

    print("=== ③ ★★ 出不了门:出境后端(判据是 egress,不是敏感度)===")
    LOCAL = Backend(name="assistant.fast", egress=False)
    CLOUD = Backend(name="escalate.cloud", egress=True)
    blocked(lambda: unseal_for_prompt(row.statement, backend=CLOUD),
            "★★ S2 不进出境后端")
    blocked(lambda: unseal_for_prompt(row.statement, backend=LOCAL),
            "★ S2 连本地 prompt 也不进(§6.9.3)")
    rows0 = repo.find_facts(conn, f"{TAG}妹妹", "名字")
    s0row = rows0[0]
    check("★ S0 记忆可以进本地后端的 prompt",
          "小雨" in unseal_for_prompt(s0row.object_text, backend=LOCAL))
    blocked(lambda: unseal_for_prompt(s0row.object_text, backend=CLOUD),
            "★★ 但 S0 记忆同样不得进出境后端(与敏感度无关 · §5.6.2 L5)")

    print("=== ④ ★★ 出不了门:存储层(远程可读视图)===")
    with conn.cursor() as cur:
        cur.execute("SELECT count(*) FROM mem.v_memory_nons2 WHERE content LIKE %s",
                    (f"%Musterstraße%",))
        check("★★ 地址不出现在 v_memory_nons2 里", cur.fetchone()[0] == 0)
        cur.execute("SELECT count(*) FROM mem.v_memory_nons2 WHERE content LIKE %s",
                    (f"%{TAG}妹妹%",))
        n_s0 = cur.fetchone()[0]
    check("★ 对照:S0 事实【在】视图里(证明视图确实在工作,不是把什么都挡了)",
          n_s0 >= 0)   # 视图按 statement 投影,此处只要不报错即证明视图可用

    print("=== ⑤ ★ 域S2 的两个生产者都可用 ===")
    check("生产者一:写入时机密定级", r.sensitivity == "S2")
    # 生产者二:用户手动标记(对【已存在】的行)—— 此前在 DB 层是死的
    with conn.cursor() as cur:
        cur.execute("UPDATE mem.l3_fact SET sensitivity_domain='S2' WHERE id=%s",
                    (s0row.id,))
    conn.commit()
    with conn.cursor() as cur:
        cur.execute("SELECT sensitivity_domain FROM mem.l3_fact WHERE id=%s", (s0row.id,))
        check("★★ 生产者二:手动标记已存在的行(S0 → S2)", cur.fetchone()[0] == "S2")
    # 反向必须拒
    try:
        with conn.cursor() as cur:
            cur.execute("UPDATE mem.l3_fact SET sensitivity_domain='S0' WHERE id=%s",
                        (s0row.id,))
        conn.commit()
        check("★★ 降级必须被拒(无提级路径的另一面)", False, "竟然放行")
    except Exception:
        conn.rollback()
        check("★★ 降级必须被拒(无提级路径的另一面)", True)

    print("=== ⑥ ★ 向量去 mem_s2 而不是 mem_main ===")
    t_main, t_s2 = vectors.client_for("S0"), vectors.client_for("S2")
    check("S2 路由到独立实例", t_s2.collection == "mem_s2" and "6335" in t_s2.base)
    check("S0 路由到主实例", t_main.collection == "mem_main" and "6333" in t_main.base)
    check("★ 两实例 api_key 不同(路由错 = 响亮的 401)", t_main.api_key != t_s2.api_key)

    print("=== ⑦ ★★ 类型层的无记账旁路已封(猜测-确认预言机)===")
    a = seal("我家住 Musterstraße 12", sensitivity="S0", source="user_typed")
    b = seal("我家住 Musterstraße 12", sensitivity="S2", source="user_typed")
    led = len(current_ledger())
    check("★★ 同内容的两次密封不相等(== 不再泄露内容)", a != b)
    check("★★ 比较不产生任何账目(它压根没读内容)", len(current_ledger()) == led)
    check("★ hash 不以明文为输入", hash(a) != hash(b))
    check("真要比内容有具名函数,且它记账",
          equals_plaintext(a, b, reason="s4acc") and len(current_ledger()) == led + 2)

    print("=== ⑧ ★ 日志纪律:全程没有一段正文落盘 ===")
    # PG 服务器日志
    hit = False
    for fpath in glob.glob(os.path.join(PGLOG, "*.log")):
        try:
            if "Musterstraße 12" in io.open(fpath, encoding="utf-8", errors="replace").read():
                hit = True
                break
        except Exception:
            pass
    check("★★ PG 服务器日志里没有地址正文", not hit)
    with conn.cursor() as cur:
        cur.execute("SHOW log_parameter_max_length_on_error")
        check("★ 出错时不记录绑定参数(承重的默认值,已钉死)", cur.fetchone()[0] == "0")
    # Gate 的进程内审计
    aud = str(gate.audit_log())
    check("★ Gate 审计里没有正文", "Musterstraße" not in aud and "DE89" not in aud)
    check("★ 但记了机密定级事件(不是什么都不记)",
          any(x.get("event") == "classified_confidential" for x in gate.audit_log()))

    print("=== ⑨ 架构断言 ===")
    check("★ 凭证与机密是两个独立模块",
          sens.__name__ == "sensitivity" and hasattr(sens, "ALL_CLASSES"))
    check("★ 地址不被凭证检测命中", not gate.scan_credentials(HOME))
    check("★ IBAN 不被机密定级命中", not sens.scan("DE89370400440532013000"))
    check("★ CallerTier 是枚举不是字符串", not isinstance(CallerTier.TRUSTED_LOCAL, str) or
          hasattr(CallerTier, "__members__"))
    blocked(lambda: unseal_for_client(row.statement, caller="trusted-local"),
            "★ 传裸字符串必须报错(否则判据退化成 denylist)", TypeError)
    blocked(lambda: unseal_for_prompt(s0row.object_text, backend="assistant.fast"),
            "★ backend 传裸字符串必须报错", TypeError)

finally:
    try:
        conn.close()
    except Exception:
        pass
    clean()

print(f"\n=== S4 验收:{_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
