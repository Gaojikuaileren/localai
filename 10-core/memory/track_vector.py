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
from tainted import TaintedText, unseal_for_client, CallerTier

TOP_K = 50          # ANN 粗召回
TOP_N = 8           # rerank 后返回
HALF_LIFE_DAYS = 90.0   # 衰减半衰期:90 天前的记忆权重降到 0.5

# ★★ 向量轨最低 rerank 分闸(S7/M9)。此前【不存在】——零相关的查询照返 8 条,
#   而「零命中说不知道」只写给了结构化轨(route.py),向量轨没有这道闸 ⇒
#   负例/对抗例上的精确率结构性无下限。
#   fail-closed:低于此分的命中一律不返回,宁可少给也不给可疑的。
#
#   ★★ 值由实测标定(2026-07-28,T10),不是拍的。在 5 文档 × 5 查询的微夹具上测得:
#         相关命中 rerank 分:min 0.399 · max 0.980
#         无关命中 rerank 分:max 0.006(纯无关查询最高分 0.001)
#       相关与无关之间有巨大空隙(0.006 ↔ 0.399),取略偏无关侧的 0.20 作阈值:
#       既远高于噪声(0.006),又留足裕量不误杀边缘真命中(最低 0.399)。
#   ★ 这是【微夹具】上的标定,S7 的对抗 eval 集会在更大负例集上复核并可能收紧(T10 挂 backlog)。
#     测量脚本见 worklog;改这里一处即可。
MIN_RERANK_SCORE = 0.20


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
           top_k: int = TOP_K, top_n: int = TOP_N,
           min_score: float = MIN_RERANK_SCORE) -> List[Passage]:
    """向量轨检索。

    ★ sensitivity 决定去哪个 Qdrant 实例(§4.11.4 结构性隔离):
      S2 内容在独立进程+端口+api_key 里,不是靠 payload 过滤挡住的。
      ★★ 调用方【不能自选】想查哪个实例 —— 一个远程调用方传 sensitivity='S2'
         就能拿到 mem_s2 的结果。所以本函数由 panel 层按 CallerTier 决定查哪些实例,
         而不是把 sensitivity 当自由参数暴露给上层随便传。

    ★ min_score:低于此 rerank 分的命中一律丢弃(fail-closed)。零相关查询返回空,
      向量轨据此也能"说不知道",而不是硬凑 8 条(见 MIN_RERANK_SCORE)。
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

    # rerank 需要明文文档 —— 经具名解封点(会记账)。
    # ★ 这里用 TRUSTED_LOCAL 是【服务端内部为打分而临时取明文】,不是"交给某个调用方":
    #   打分用的明文当场用完即弃;真正返回给上层的是 row.body(仍密封的 TaintedText),
    #   由 panel 层用【真实的 caller 档位】再解封一次。两处不要混淆。
    ordered_ids = [i for i in ids if i in rows]
    docs = [unseal_for_client(rows[i].body, caller=CallerTier.TRUSTED_LOCAL) for i in ordered_ids]
    ranked = vectors.rerank(query, docs, top_n=min(top_n * 2, len(docs)))

    out: List[Passage] = []
    for idx, rscore in ranked:
        if rscore < min_score:
            continue                    # ★ 最低分闸:可疑命中直接丢
        eid = ordered_ids[idx]
        row = rows[eid]
        d = _decay(row.event_at)
        out.append(Passage(episode_id=eid, body=row.body, score=rscore * d,
                           rerank_score=rscore, decay=d, event_at=row.event_at,
                           trace=row.trace()))
    out.sort(key=lambda p: p.score, reverse=True)
    return out[:top_n]


def delete_episode_vector(episode_id: int, *, sensitivity: str) -> None:
    """删掉一条情节的 Qdrant 向量点(tombstone 情节时连带调用)。

    ★★ 必须删,否则:PG 侧 redacted_at 置位后 get_episodes 过滤掉它 →
       向量命中却取不到正文 → 静默丢弃(不是泄露,但是悬空命中,库长期不一致)。
    ★ sensitivity 决定去哪个实例:S2 情节的点在 mem_s2,删错实例等于没删。
      point_id 与 episode_id 相同(见 index_episode)。
    """
    tgt = vectors.client_for(sensitivity)
    vectors.delete_point(tgt, episode_id)


def migrate_episode_vector(conn, episode_id: int, body,
                           *, from_sensitivity: str, to_sensitivity: str,
                           write_seq: int) -> None:
    """把一条情节的向量从一个实例迁到另一个(手动标 S2 时用)。

    ★ 顺序:先在目标实例建点,再删源实例的点 —— 崩在中间留下的是「两处都有」
      (检索可能多命中一次,可修),而不是「两处都无」(彻底丢)。
      失败方向选可修的那个。
    """
    if from_sensitivity == to_sensitivity:
        return
    vec = vectors.encode_tainted(body, is_query=False)
    dst = vectors.client_for(to_sensitivity)
    vectors.assert_no_text_in_payload({"kind": "episode", "row_id": episode_id,
                                       "write_seq": write_seq,
                                       "sensitivity_domain": to_sensitivity})
    vectors.upsert_point(dst, episode_id, vec, kind="episode", row_id=episode_id,
                         write_seq=write_seq, sensitivity=to_sensitivity)
    src = vectors.client_for(from_sensitivity)
    vectors.delete_point(src, episode_id)


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
