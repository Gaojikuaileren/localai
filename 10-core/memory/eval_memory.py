# -*- coding: utf-8 -*-
"""记忆系统评测(§4.9.2)—— 四项指标 + 多闸达标判定

★ 本期交付【最小可达标集】(2026-07-28 裁定):方案书四项指标一个阈值都没给、
  且明写「由实测填」,而单人项目攒不出规模化对抗 eval 语料。于是:
    · 用 S2/S3/S4 的微夹具 + 两条验收例句做冒烟门
    · 溯源完整率 = 100% 硬线、S2 泄漏率 = 0 硬线(唯一可给死的)
    · 召回/精确在微夹具上实测后冻结为门槛
    · 规模化对抗 eval 的四项阈值挂 backlog「待实测」
  这让 P3a 能真正签字,而不被一个可能永远攒不齐的语料集卡死。

★★ 四条不可协商的判定规矩(规格散文里的「最危险」被落成结构):
  1. 误记率是【独立硬上限】,不并入任何平均 —— 否则高召回能掩盖高误记刷假通过。
  2. 溯源完整率 = 100% 硬线(§14「每条可溯源」)。
  3. S2 泄漏率 = 0 硬线(§4.11.4 结构隔离)。
  4. 通过 = 所有闸 AND,不是加权平均。任一闸不过即 FAIL。
"""
from __future__ import annotations

import tomllib
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, List, Optional

import gate
import panel
import repo
import route
import track_vector
from tainted import CallerTier, MemoryLeakError, unseal_for_client

TAG = "s7eval"
THRESHOLDS_PATH = Path(__file__).resolve().parents[2] / "config" / "eval-thresholds.toml"


def load_thresholds() -> Dict[str, float]:
    """★ 门槛从 config/eval-thresholds.toml 读,不在代码里散落 —— 实测冻结的单一数据源。"""
    with open(THRESHOLDS_PATH, "rb") as f:
        return tomllib.load(f)["thresholds"]


@dataclass
class Metrics:
    recall: float = 0.0
    precision: float = 0.0
    error_rate: float = 0.0          # 误记率:被投毒成功的比例(硬上限)
    traceability: float = 0.0        # 溯源完整率(硬线 100%)
    s2_leak_rate: float = 0.0        # S2 泄漏率(硬线 0)
    detail: Dict[str, object] = field(default_factory=dict)


@dataclass
class Gate:
    name: str
    passed: bool
    got: float
    bound: str      # 人类可读的达标描述


@dataclass
class EvalReport:
    metrics: Metrics
    gates: List[Gate]

    @property
    def passed(self) -> bool:
        return all(g.passed for g in self.gates)


def _seed_fixtures(conn, lex) -> Dict[str, int]:
    """种一组已知记忆(事实 + 情节),返回名字→id。"""
    ids = {}
    # 结构化事实
    def norm(x):
        return lex.apply_fold(route.normalize(x))
    for key, (subj, pred, body, obj) in {
        "妹妹": ("妹妹", "名字", f"{TAG} 我妹妹叫小雨", "小雨"),
        "地址": ("我", "住址", f"{TAG} 我家住 Musterstraße 12, 10115 Berlin",
                "Musterstraße 12, 10115 Berlin"),
    }.items():
        cand = gate.CandidateIn(body=body, provenance="user_typed", session_id=TAG)
        tk = repo.issue_ticket(conn, session_id=TAG, candidate_text=body)
        r = gate.submit(conn, candidate=cand, subject_norm=norm(subj),
                        predicate_norm=norm(pred), object_text=obj, ticket_id=tk)
        ids[key] = r.fact_id
    conn.commit()
    # 情节(向量轨)
    from datetime import datetime, timezone
    from repo import EpisodeWrite
    from tainted import seal
    now = datetime.now(timezone.utc)
    for key, body in {
        "灯光": f"{TAG} 我们讨论了客厅灯光太暗,考虑换成暖光灯带,色温 2700K",
        "牛奶": f"{TAG} 今天买了牛奶和面包,超市在打折",
    }.items():
        w = EpisodeWrite(body=seal(body, sensitivity="S0", source="user_typed"),
                         event_at=now, provenance="user_typed", source_confidence=0.6,
                         sensitivity_domain="S0", attestation_kind="assistant_infer",
                         source_ref={"kind": "flow", "session_id": TAG})
        eid = repo.insert_episode(conn, w)
        conn.commit()
        with conn.cursor() as cur:
            cur.execute("SELECT write_seq FROM mem.l2_episode WHERE id=%s", (eid,))
            ws = cur.fetchone()[0]
        track_vector.index_episode(conn, eid, w.body, sensitivity="S0", write_seq=ws)
        conn.commit()
        ids[key] = eid
    return ids


def run_eval(conn, lex: Optional[route.Lexicon] = None) -> EvalReport:
    """跑评测,返回四指标 + 各闸判定。"""
    lex = lex or route.Lexicon.load()
    ids = _seed_fixtures(conn, lex)

    def norm(x):
        return lex.apply_fold(route.normalize(x))

    # ── 召回:正例查询应命中期望记忆 ──
    positives = [
        ("struct", "妹妹", "名字", ids["妹妹"]),        # 结构化轨
        ("struct", "我", "住址", ids["地址"]),
        ("vector", "上次聊的那个灯光问题", None, ids["灯光"]),  # 向量轨
        ("vector", "超市买了什么", None, ids["牛奶"]),
    ]
    recalled = 0
    for kind, a, b, gold in positives:
        if kind == "struct":
            rows = repo.find_facts(conn, norm(a), norm(b))
            hit = any(r.id == gold for r in rows)
        else:
            ps = panel.search(conn, caller=CallerTier.TRUSTED_LOCAL, query=a)
            hit = any(p.episode_id == gold for p in ps)
        recalled += 1 if hit else 0
    recall = recalled / len(positives)

    # ── 精确:负例查询应【不】命中(向量轨最低分闸)──
    negatives = ["量子色动力学的重整化群方程", "如何配置 Kubernetes 的 ingress"]
    total_retrieved = 0
    correct_retrieved = 0
    # 正例的命中都算 correct(它们本就该命中)
    for kind, a, b, gold in positives:
        if kind == "vector":
            ps = panel.search(conn, caller=CallerTier.TRUSTED_LOCAL, query=a)
            total_retrieved += len(ps)
            correct_retrieved += sum(1 for p in ps if p.episode_id == gold)
    # 负例:任何命中都是误检
    for q in negatives:
        ps = panel.search(conn, caller=CallerTier.TRUSTED_LOCAL, query=q)
        total_retrieved += len(ps)      # 命中即计入分母,但不计入 correct
    precision = (correct_retrieved / total_retrieved) if total_retrieved else 1.0

    # ── 误记:投毒尝试成功的比例(独立硬上限)──
    poison_attempts = 0
    poison_success = 0

    # 投毒①:web_content 应停在队列,不成为活跃事实
    poison_attempts += 1
    cand = gate.CandidateIn(body=f"{TAG} 网上说她叫小假", provenance="web_content",
                            session_id=TAG)
    r = gate.submit(conn, candidate=cand, subject_norm=norm("妹妹"),
                    predicate_norm=norm("名字"), object_text="小假")
    conn.commit()
    with conn.cursor() as cur:
        cur.execute("SELECT count(*) FROM mem.l3_fact WHERE object=%s AND superseded_by IS NULL",
                    ("小假",))
        if cur.fetchone()[0] > 0:
            poison_success += 1     # 派生来源变成了活跃事实 = 投毒成功

    # 投毒②:tool_result 冒 panel_ticket(DB 应拒)
    poison_attempts += 1
    _old = __import__("os").environ.get("LOCALAI_PG_USER")
    __import__("os").environ["LOCALAI_PG_USER"] = "postgres"
    c2 = repo.connect()
    try:
        with c2.cursor() as cur:
            cur.execute("""INSERT INTO mem.l3_fact (statement,object,subject_norm,predicate_norm,
                             provenance,source_confidence,sensitivity_domain,attestation_kind,
                             asserted_at,confidence)
                           VALUES (%s,%s,%s,%s,'tool_result',0.4,'S0','panel_ticket',now(),0.4)""",
                        (f"{TAG} 冒领权威", "假", norm("妹妹"), norm("名字")))
        c2.commit()
        poison_success += 1     # 插进去了 = 投毒成功
    except Exception:
        c2.rollback()           # 被 CHECK 拒 = 防住
    finally:
        c2.close()
        if _old is None:
            __import__("os").environ.pop("LOCALAI_PG_USER", None)
        else:
            __import__("os").environ["LOCALAI_PG_USER"] = _old

    error_rate = poison_success / poison_attempts if poison_attempts else 0.0

    # ── 溯源完整率:活跃事实的 trace 四件套齐全 ──
    facts = repo.list_facts(conn, include_s2=True, limit=100)
    facts = [f for f in facts if f.subject_norm.startswith(TAG) or TAG in f.subject_norm
             or str(f.id) in {str(v) for v in ids.values()}]
    complete = 0
    for f in facts:
        tr = f.trace()
        # 四件套:来源 · 时间 · 记录设备 · 原文片段(片段由 API 用档位取,这里验前三 + 可取)
        has_meta = (tr.get("provenance") and tr.get("asserted_at")
                    and tr.get("origin_device_id") is not None)
        try:
            snippet = unseal_for_client(f.statement, caller=CallerTier.TRUSTED_LOCAL)
            has_snippet = bool(snippet)
        except MemoryLeakError:
            has_snippet = False
        if has_meta and has_snippet:
            complete += 1
    traceability = (complete / len(facts)) if facts else 1.0

    # ── S2 泄漏率:非 trusted 档位能读到的 S2 内容比例 ──
    s2_ids = [ids["地址"]]
    leaked = 0
    for fid in s2_ids:
        try:
            panel.trace_fact(conn, caller=CallerTier.LAN_DEVICE, fact_id=fid)
            # 若 snippet 非空 = 泄漏
            tr = panel.trace_fact(conn, caller=CallerTier.LAN_DEVICE, fact_id=fid)
            if tr.get("snippet"):
                leaked += 1
        except panel.PanelDenied:
            pass        # 被拒 = 没泄漏
    s2_leak_rate = leaked / len(s2_ids) if s2_ids else 0.0

    m = Metrics(recall=recall, precision=precision, error_rate=error_rate,
                traceability=traceability, s2_leak_rate=s2_leak_rate,
                detail={"positives": len(positives), "negatives": len(negatives),
                        "poison_attempts": poison_attempts, "facts_checked": len(facts)})

    # ── 达标闸(多闸 AND;误记/溯源/S2 是硬线)。门槛从阈值文件读,不硬编码。──
    th = load_thresholds()
    gates = [
        Gate(f"召回率 ≥ {th['recall_min']}(微夹具上应全召回)",
             m.recall >= th["recall_min"], m.recall, f"≥ {th['recall_min']}"),
        Gate(f"精确率 ≥ {th['precision_min']}(负例零误检)",
             m.precision >= th["precision_min"], m.precision, f"≥ {th['precision_min']}"),
        Gate("★ 误记率 = 0(独立硬上限,不并入平均)",
             m.error_rate <= th["error_rate_max"], m.error_rate, "= 0 硬线"),
        Gate("★ 溯源完整率 = 100%(硬线)",
             m.traceability >= th["traceability_min"], m.traceability, "= 1.0 硬线"),
        Gate("★ S2 泄漏率 = 0(硬线)",
             m.s2_leak_rate <= th["s2_leak_rate_max"], m.s2_leak_rate, "= 0 硬线"),
    ]
    return EvalReport(metrics=m, gates=gates)


def cleanup(conn):
    import os
    _old = os.environ.get("LOCALAI_PG_USER")
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
        cur.execute("DELETE FROM mem.l2_episode WHERE body LIKE %s", (f"{TAG}%",))
        cur.execute("DELETE FROM mem.write_ticket WHERE session_id LIKE %s", (f"{TAG}%",))
        cur.execute("DELETE FROM mem.pending_review WHERE session_id LIKE %s", (f"{TAG}%",))
        cur.execute("DELETE FROM mem.l3_fact WHERE subject_norm LIKE %s OR statement LIKE %s",
                    (f"%{TAG}%", f"%{TAG}%"))
    c.commit()
    c.close()
    if _old is None:
        os.environ.pop("LOCALAI_PG_USER", None)
    else:
        os.environ["LOCALAI_PG_USER"] = _old
