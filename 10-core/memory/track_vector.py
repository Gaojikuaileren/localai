"""向量轨(§4.2)—— 「上次聊的那个灯光问题」走这条

    ANN top_k → rerank → 时间衰减 → top_n

★★ 本轨**永远不产出 answer**,只产出 passages(§4.2 / S1 已定死的规矩)。
   原因:向量检索返回的是"相关的东西",不是"确切的值"。
   一条 rerank 高分片段冒充确切答案 = 静默答错的正门。
   要确切值,走结构化轨;结构化轨零命中时,正确行为是**说不知道**。

★ 时间衰减是**乘性因子**而不是过滤:
   「上次聊的」暗示时间近,但不能把老的直接排除 —— 用户可能在问三个月前的事。
   乘性因子让新的更容易冒头,而不是让老的不可见。
"""
from __future__ import annotations

import math
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional

import repo
import vectors
from tainted import TaintedText, unseal_for_client

TOP_K = 50          # ANN 粗召回
TOP_N = 8           # rerank 后返回
HALF_LIFE_DAYS = 90.0   # 衰减半衰期:90 天前的记忆权重降到 0.5


@dataclass
class Passage:
    """向量轨的产出。★ 注意没有 answer 字段 —— 类型上就不产出确切答案。"""
    episode_id: int
    body: TaintedText
    score: float          # rerank 分 × 衰减
    rerank_score: float
    decay: float
    event_at: datetime
    trace: Dict[str, Any]


def _decay(event_at: datetime, now: Optional[datetime] = None) -> float:
    now = now or datetime.now(timezone.utc)
    if event_at.tzinfo is None:
        event_at = event_at.replace(tzinfo=timezone.utc)
    days = max(0.0, (now - event_at).total_seconds() / 86400.0)
    return 0.5 ** (days / HALF_LIFE_DAYS)


def search(conn, query: str, *, sensitivity: str = "S0",
           top_k: int = TOP_K, top_n: int = TOP_N) -> List[Passage]:
    """向量轨检索。

    ★ sensitivity 决定去哪个 Qdrant 实例(§4.11.4 结构性隔离):
      S2 内容在独立进程+端口+api_key 里,不是靠 payload 过滤挡住的。
    """
    qvec = vectors.encode_texts([query], is_query=True)[0]
    tgt = vectors.client_for(sensitivity)
    hits = vectors.search(tgt, qvec, top_k=top_k)
    if not hits:
        return []

    ids = [int(h["payload"]["row_id"]) for h in hits
           if h.get("payload", {}).get("kind") == "episode"]
    rows = repo.get_episodes(conn, ids)          # ★ 正文从 PG 取 ⇒ 必然被 seal
    if not rows:
        return []

    # rerank 需要明文文档 —— 经具名解封点(会记账)
    ordered_ids = [i for i in ids if i in rows]
    docs = [unseal_for_client(rows[i].body, caller="trusted-local") for i in ordered_ids]
    ranked = vectors.rerank(query, docs, top_n=min(top_n * 2, len(docs)))

    out: List[Passage] = []
    for idx, rscore in ranked:
        eid = ordered_ids[idx]
        row = rows[eid]
        d = _decay(row.event_at)
        out.append(Passage(episode_id=eid, body=row.body, score=rscore * d,
                           rerank_score=rscore, decay=d, event_at=row.event_at,
                           trace=row.trace()))
    out.sort(key=lambda p: p.score, reverse=True)
    return out[:top_n]


def index_episode(conn, episode_id: int, body: TaintedText, *,
                  sensitivity: str, write_seq: int) -> int:
    """把一条已写入 PG 的情节加入向量索引。

    ★ 固定顺序 PG→Qdrant(见 repo.set_episode_vector 注释):
      崩在中间留下「有正文无向量」是可修复的;反过来是悬空指针。
    """
    vec = vectors.encode_tainted(body, is_query=False)
    tgt = vectors.client_for(sensitivity)
    payload = {"kind": "episode", "row_id": episode_id,
               "write_seq": write_seq, "sensitivity_domain": sensitivity}
    vectors.assert_no_text_in_payload(payload)     # ★ 架构断言:载荷不得带正文
    vectors.upsert_point(tgt, episode_id, vec, kind="episode", row_id=episode_id,
                         write_seq=write_seq, sensitivity=sensitivity)
    repo.set_episode_vector(conn, episode_id, episode_id)
    return episode_id
