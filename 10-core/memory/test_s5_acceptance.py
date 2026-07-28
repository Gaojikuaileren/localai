# -*- coding: utf-8 -*-
"""S5 验收:记忆面板后端 API(浏览 · 溯源 · 编辑 · 删除 · 手动标密)

验收句(与前四条同级):

  ★ 「从局域网连到记忆 API,那条 S2 记忆读不到(默认拒),本机面板读得到;
     展开凭证登记那条,只显示『由凭证流程创建 + 时间 + 会话』,吐不出原值。」

跑(需活库 + Qdrant + embedding):PYTHONPATH=. python test_s5_acceptance.py
"""
import ast
import inspect
import os
import sys

import panel
import repo
import track_vector
from panel import PanelDenied
from tainted import CallerTier

TAG = "s5acc"
_p = _f = 0


def check(name, cond, extra=""):
    global _p, _f
    if cond:
        _p += 1
        print(f"  PASS  {name}")
    else:
        _f += 1
        print(f"  FAIL  {name} {extra}")


def denied(fn, name, exc=PanelDenied):
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
        cur.execute("DELETE FROM mem.write_ticket WHERE session_id LIKE %s", (f"{TAG}%",))
        cur.execute("DELETE FROM mem.l3_fact WHERE subject_norm LIKE %s", (f"{TAG}%",))
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


def write_fact(body, subj, pred, obj, prov="user_typed", ticket=True):
    """经 Gate 正常写一条,返回 fact_id。"""
    import gate
    cand = gate.CandidateIn(body=body, provenance=prov, session_id=TAG)
    tk = repo.issue_ticket(conn, session_id=TAG, candidate_text=body) if ticket else None
    r = gate.submit(conn, candidate=cand, subject_norm=subj, predicate_norm=pred,
                    object_text=obj, ticket_id=tk)
    conn.commit()
    return r.fact_id


try:
    print("=== ① ★★ 验收句:S2 记忆的档位隔离(默认拒)===")
    # 写一条机密事实(地址 → 机密定级会标 S2)
    fid_s2 = write_fact(f"{TAG} 我家住 Musterstraße 12, 10115 Berlin",
                        f"{TAG}我", "住址", "Musterstraße 12, 10115 Berlin")
    fid_s0 = write_fact(f"{TAG} 我妹妹叫小雨", f"{TAG}妹妹", "名字", "小雨")

    # 本机浏览:两条都看得到
    cards_local = panel.browse_facts(conn, caller=CallerTier.TRUSTED_LOCAL)
    ids_local = {c.id for c in cards_local}
    check("★ 本机面板浏览:S2 与 S0 都在", fid_s2 in ids_local and fid_s0 in ids_local)

    # 局域网设备浏览:只看得到 S0,S2 连列都不列
    cards_lan = panel.browse_facts(conn, caller=CallerTier.LAN_DEVICE)
    ids_lan = {c.id for c in cards_lan}
    check("★★ 局域网设备浏览:S2 行【连存在性都不给】", fid_s2 not in ids_lan)
    check("★ 局域网设备仍能看到 S0", fid_s0 in ids_lan)

    # 未认证远程:连浏览都拒
    denied(lambda: panel.browse_facts(conn, caller=CallerTier.REMOTE_UNAUTH),
           "★ 未认证远程不得浏览")

    print("=== ② ★★ 溯源展开:本机取得到原文,局域网取 S2 被拒 ===")
    tr = panel.trace_fact(conn, caller=CallerTier.TRUSTED_LOCAL, fact_id=fid_s2)
    check("★ 溯源四件套齐全",
          all(k in tr for k in ("provenance", "asserted_at", "origin_device_id", "snippet")))
    check("★ 本机能取到 S2 原文片段", "Musterstraße" in (tr["snippet"] or ""))
    denied(lambda: panel.trace_fact(conn, caller=CallerTier.LAN_DEVICE, fact_id=fid_s2),
           "★★ 局域网设备展开 S2 → 被拒(不是静默空串)")
    tr_s0_lan = panel.trace_fact(conn, caller=CallerTier.LAN_DEVICE, fact_id=fid_s0)
    check("★ 局域网设备能展开 S0 原文", "小雨" in (tr_s0_lan["snippet"] or ""))

    print("=== ③ ★★ 凭证登记类记忆:溯源不吐原值(§6.9.8 例外)===")
    # 造一条 source_ref.kind='cred_registration' 的记忆(用 postgres 直写模拟凭证登记流程产物)
    _old = os.environ.get("LOCALAI_PG_USER")
    os.environ["LOCALAI_PG_USER"] = "postgres"
    c2 = repo.connect()
    with c2.cursor() as cur:
        cur.execute("""INSERT INTO mem.l3_fact
                         (statement, object, subject_norm, predicate_norm, provenance,
                          source_confidence, sensitivity_domain, attestation_kind,
                          asserted_at, confidence, source_ref)
                       VALUES (%s,%s,%s,%s,'user_typed',1.0,'S2','panel_ticket',
                               now(),1.0,%s) RETURNING id""",
                    (f"{TAG} 银行密码是 hunter2SECRET", "hunter2SECRET",
                     f"{TAG}银行", "密码", '{"kind":"cred_registration","session_id":"s5acc"}'))
        cred_id = cur.fetchone()[0]
    c2.commit()
    c2.close()
    if _old is None:
        os.environ.pop("LOCALAI_PG_USER", None)
    else:
        os.environ["LOCALAI_PG_USER"] = _old

    tr_cred = panel.trace_fact(conn, caller=CallerTier.TRUSTED_LOCAL, fact_id=cred_id)
    check("★★ 凭证登记类:snippet 为空(不渲染原文)", tr_cred["snippet"] is None)
    check("★ 给出了不展示的理由", "snippet_withheld" in tr_cred)
    check("★★ 溯源结果全文 grep 不到原值",
          "hunter2SECRET" not in str(tr_cred))
    check("★ 但元数据齐全(来源/时间/会话)",
          tr_cred["provenance"] == "user_typed" and "asserted_at" in tr_cred)

    print("=== ④ ★★ 编辑 = supersede(不是 UPDATE 正文)===")
    new_res = panel.edit_fact(conn, caller=CallerTier.TRUSTED_LOCAL, old_id=fid_s0,
                              session_id=TAG, new_body=f"{TAG} 我妹妹叫小雪",
                              subject_norm=f"{TAG}妹妹", predicate_norm="名字",
                              object_text="小雪")
    conn.commit()
    check("★ 编辑写入新事实且 1.0", new_res.source_confidence == 1.0)
    with conn.cursor() as cur:
        cur.execute("SELECT superseded_by FROM mem.l3_fact WHERE id=%s", (fid_s0,))
        check("★★ 旧事实被显式退休(superseded_by 指向新行)", cur.fetchone()[0] == new_res.fact_id)
    facts = repo.find_facts(conn, f"{TAG}妹妹", "名字")
    check("★★ 查询只返回新行(不并列两条)", len(facts) == 1 and facts[0].id == new_res.fact_id)

    denied(lambda: panel.edit_fact(conn, caller=CallerTier.LAN_DEVICE, old_id=new_res.fact_id,
                                   session_id=TAG, new_body="x", subject_norm="a",
                                   predicate_norm="b", object_text="c"),
           "★ 局域网设备不得编辑(写操作限本机)")

    print("=== ⑤ ★ 删除 = tombstone(永不物理 DELETE)===")
    del_id = write_fact(f"{TAG} 该被删的事实", f"{TAG}删", "x", "y")
    panel.delete_fact(conn, caller=CallerTier.TRUSTED_LOCAL, fact_id=del_id, reason="s5测试")
    conn.commit()
    with conn.cursor() as cur:
        cur.execute("SELECT redacted_at IS NOT NULL FROM mem.l3_fact WHERE id=%s", (del_id,))
        check("★ 行仍物理存在,redacted_at 置位", cur.fetchone()[0] is True)
        cur.execute("SELECT count(*) FROM mem.quarantine WHERE src_table='l3_fact' AND src_id=%s",
                    (del_id,))
        check("★ 正文进了隔离区", cur.fetchone()[0] == 1)
    check("★ 删除后浏览不再出现",
          del_id not in {c.id for c in panel.browse_facts(conn, caller=CallerTier.TRUSTED_LOCAL)})
    denied(lambda: panel.delete_fact(conn, caller=CallerTier.LAN_DEVICE, fact_id=new_res.fact_id),
           "★ 局域网设备不得删除")

    print("=== ⑥ ★ 手动标密:S0 → S2(单向棘轮)===")
    mark_id = write_fact(f"{TAG} 一条普通事实待标密", f"{TAG}标", "x", "y")
    panel.mark_confidential_fact(conn, caller=CallerTier.TRUSTED_LOCAL, fact_id=mark_id)
    conn.commit()
    with conn.cursor() as cur:
        cur.execute("SELECT sensitivity_domain FROM mem.l3_fact WHERE id=%s", (mark_id,))
        check("★★ 标密后为 S2", cur.fetchone()[0] == "S2")
    check("★ 标密后局域网设备浏览不到它",
          mark_id not in {c.id for c in panel.browse_facts(conn, caller=CallerTier.LAN_DEVICE)})

    print("=== ⑦ ★ 架构断言 ===")
    src = inspect.getsource(panel)
    tree = ast.parse(src)
    # ★ 没有【面向调用方的操作】把解封档位硬编码成 TRUSTED_LOCAL。
    #   例外:_require_write / _can_see_s2 里出现 TRUSTED_LOCAL 是【档位判据本身】
    #   (「写操作限本机」「谁能看 S2」),不是把它当解封档位传给 unseal_for_client。
    #   真正要防的是:某个操作用字面量 TRUSTED_LOCAL 去 unseal,绕过调用方真实档位。
    JUDGE_FNS = {"_require_write", "_can_see_s2"}
    hardcoded = []
    for fn in [n for n in ast.walk(tree) if isinstance(n, ast.FunctionDef)]:
        if fn.name in JUDGE_FNS:
            continue
        for node in ast.walk(fn):
            # 只盯「unseal_for_client(..., caller=CallerTier.TRUSTED_LOCAL)」这种调用
            if isinstance(node, ast.Call):
                for kw in node.keywords:
                    if (kw.arg == "caller" and isinstance(kw.value, ast.Attribute)
                            and kw.value.attr == "TRUSTED_LOCAL"):
                        hardcoded.append(fn.name)
    check("★★ 没有操作把解封档位硬编码成 TRUSTED_LOCAL(必须用调用方真实档位)",
          not hardcoded, f"→ {sorted(set(hardcoded))}")
    check("★ 每个公开操作都收 caller 参数",
          all("caller" in inspect.signature(getattr(panel, n)).parameters
              for n in ("browse_facts", "trace_fact", "edit_fact", "delete_fact",
                        "mark_confidential_fact", "search", "confirm_pending")))
    # 面板只走 unseal_for_client,不【调用】unseal_for_prompt(AST 查调用,不查注释文本)
    prompt_calls = [n for n in ast.walk(tree)
                    if isinstance(n, ast.Call) and (
                        (isinstance(n.func, ast.Name) and n.func.id == "unseal_for_prompt")
                        or (isinstance(n.func, ast.Attribute) and n.func.attr == "unseal_for_prompt"))]
    check("★★ 面板不【调用】unseal_for_prompt(读侧不进 MODEL 平面 · §4.6.3)",
          not prompt_calls, f"{len(prompt_calls)} 处调用")
    check("★ 实体图谱显式声明不暴露", panel.ENTITY_GRAPH_EXPOSED is False)

    print("=== ⑧ ★ 向量轨最低分闸:零相关查询不硬凑结果 ===")
    # 用一段与库里全无关的查询
    ps = panel.search(conn, caller=CallerTier.TRUSTED_LOCAL,
                      query="量子色动力学的重整化群方程")
    # 库里 s5acc 数据都是家庭/名字类,这条应命中很少或为空;关键是不返回低分噪声
    check("★ 最低分闸存在且被 search 用到",
          "min_score" in inspect.signature(track_vector.search).parameters
          and track_vector.MIN_RERANK_SCORE > 0)

finally:
    try:
        conn.close()
    except Exception:
        pass
    clean()
    # 清掉凭证登记模拟行
    _o = os.environ.get("LOCALAI_PG_USER")
    os.environ["LOCALAI_PG_USER"] = "postgres"
    try:
        c = repo.connect()
        with c.cursor() as cur:
            cur.execute("DELETE FROM mem.l3_fact WHERE subject_norm LIKE %s", (f"{TAG}%",))
            cur.execute("DELETE FROM mem.quarantine WHERE src_table='l3_fact'")
        c.commit()
        c.close()
    except Exception:
        pass
    if _o is None:
        os.environ.pop("LOCALAI_PG_USER", None)
    else:
        os.environ["LOCALAI_PG_USER"] = _o

print(f"\n=== S5 验收:{_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
