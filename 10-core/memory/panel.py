# -*- coding: utf-8 -*-
"""记忆面板后端 API(§4.4.1 · §4.5 · D36)—— 浏览 · 溯源 · 编辑 · 删除 · 手动标密

★★ 本模块存在的首要理由:**唯一的档位强制点**。

  隐私隔离(tainted._ALLOWED_CALLERS)是按【调用方给的档位】判的,而此前唯一的
  生产调用点把档位硬编码成 TRUSTED_LOCAL —— 于是"S2 永不出境/远程不可读"退化成
  "信任调用方诚实"。一个到达记忆端点的局域网请求就能读 S2。

  ⇒ 面板层的铁律:**每个函数都收一个 `caller: CallerTier`,绝不硬编码**;
    它由上层(P3c 客户端经网关)从【已认证连接】的身份推导出来传进来。
    本模块只负责:拿这个档位去过滤能看什么、能改什么,并把它透传给 unseal_for_client。

★ 读侧只走 unseal_for_client(③回客户端),永不 unseal_for_prompt(④进 prompt)——
  面板结果只渲染成卡片,永不回流进 MODEL 平面请求体(§4.6.3)。

★ 写侧(编辑/删除/标密/确认)一律 **TRUSTED_LOCAL only**:
  它们是记忆里最危险的操作,而 D36 把全功能面板限定在本机客户端。
  局域网设备(P3b)可读非 S2,但不改。

★ 分层:panel 依赖 repo + track_vector + gate + tainted;它们互不依赖 panel。
  多步操作(删情节 = PG tombstone + 删向量点;标情节 S2 = 棘轮 + 迁向量)在这里串,
  因为只有这一层同时看得到 PG 与向量层。
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Dict, List, Optional

import repo
import track_vector
import gate
from tainted import CallerTier, MemoryLeakError, unseal_for_client


class PanelDenied(Exception):
    """面板操作被档位拒绝。★ 消息里绝不含正文。"""


# ── 档位守卫 ──────────────────────────────────────────────────────
def _require_type(caller) -> None:
    if not isinstance(caller, CallerTier):
        raise TypeError(
            f"caller 必须是 CallerTier 枚举,收到 {type(caller).__name__}。"
            "裸字符串会让判据退化成 denylist —— 新增档位默认放行。")


def _require_write(caller: CallerTier) -> None:
    """改记忆(编辑/删除/标密/确认)只允许本机面板。"""
    _require_type(caller)
    if caller is not CallerTier.TRUSTED_LOCAL:
        raise PanelDenied(f"{caller.value} 不得修改记忆 —— 全功能面板限本机(D36)")


def _can_see_s2(caller: CallerTier) -> bool:
    return caller is CallerTier.TRUSTED_LOCAL


# ── 浏览 ─────────────────────────────────────────────────────────
@dataclass
class Card:
    """面板卡片:密封正文不在这里,正文由调用方另经 snippet/trace 取(且记账)。"""
    id: int
    subject_norm: str
    predicate_norm: str
    sensitivity_domain: str
    provenance: str
    source_confidence: Optional[float]
    asserted_at: str


def browse_facts(conn, *, caller: CallerTier, limit: int = 50, offset: int = 0) -> List[Card]:
    """列出当前活跃事实。★ 非 trusted-local 看不到 S2 —— 连行的存在性都不给。"""
    _require_type(caller)
    if caller in (CallerTier.REMOTE_UNAUTH,):
        raise PanelDenied("未认证远程不得浏览记忆")
    rows = repo.list_facts(conn, include_s2=_can_see_s2(caller), limit=limit, offset=offset)
    return [Card(id=r.id, subject_norm=r.subject_norm, predicate_norm=r.predicate_norm,
                 sensitivity_domain=r.sensitivity_domain, provenance=r.provenance,
                 source_confidence=r.source_confidence,
                 asserted_at=r.asserted_at.isoformat()) for r in rows]


# ── 溯源展开 ─────────────────────────────────────────────────────
def trace_fact(conn, *, caller: CallerTier, fact_id: int) -> Dict[str, Any]:
    """展开一条记忆的溯源(§4.5 四件套:来源·时间·原文片段·记录设备)。

    ★★ 两条并存的规则,必须按 source_ref.kind 分支:
      · §4.5:溯源要含【原文片段】(可解释性)
      · §6.9.8:凭证登记类记忆【不得】显示原始发言快照,结果不含原值

    ⇒ 凭证登记类(source_ref.kind == 'cred_registration')恒不渲染 snippet,
      只回「由凭证登记流程创建 + 时间 + 会话」。这是**内建守卫**,不是事后过滤 ——
      一个"总是展开原文"的朴素实现会直接违 §6.9.8。
    """
    _require_type(caller)
    row = repo.get_fact(conn, fact_id)
    if row is None:
        raise PanelDenied("该记忆不存在或已被删除/取代")

    tr = row.trace()                        # 来源 · 时间 · 设备(+confidence/write_seq)
    src_kind = (row.source_ref or {}).get("kind")

    base = {"id": row.id,
            "provenance": tr["provenance"],
            "asserted_at": tr["asserted_at"],
            "origin_device_id": tr["origin_device_id"],
            "source_ref": row.source_ref,
            "sensitivity_domain": row.sensitivity_domain}

    if src_kind == "cred_registration":
        # ★ §6.9.8 例外:不渲染原文片段
        base["snippet"] = None
        base["snippet_withheld"] = "由凭证登记流程创建;按 §6.9.8 不展示原始快照"
        return base

    # 原文片段:用【调用方真实档位】解封 —— 档位不够则 MemoryLeakError,不返回空串
    try:
        base["snippet"] = unseal_for_client(row.statement, caller=caller)
    except MemoryLeakError:
        raise PanelDenied(
            f"{caller.value} 无权查看这条 {row.sensitivity_domain} 记忆的原文(§4.11.4)")
    return base


# ── 编辑 = 显式 supersede,重铸 user_typed/1.0 ─────────────────────
def edit_fact(conn, *, caller: CallerTier, old_id: int, session_id: str,
              new_body: str, subject_norm: str, predicate_norm: str,
              object_text: str) -> "gate.GateResult":
    """编辑一条既有事实 —— 走 supersede,不是 UPDATE 正文。

    ★★ 面板点击 = 把这句话说成本人的:新事实重铸 provenance=user_typed,经票据铸 1.0。
       **不得复用 gate.submit()** —— 它的直写分支只把 supersedes 记进 source_ref,
       并不调 repo.supersede,会与旧事实【并列存两条活跃行】。所以这里显式退休旧行。
    """
    _require_write(caller)
    old = repo.get_fact(conn, old_id)
    if old is None:
        raise PanelDenied("要编辑的记忆不存在或已被取代")

    # 1. 新内容经 Gate 写入(凭证拦截 · 机密定级 · user_typed + 票据 → 铸 1.0 直写)。
    #    面板编辑天然是"逐条确认"语义 → 当场签票据并让 gate.submit 消费它。
    cand = gate.CandidateIn(body=new_body, provenance="user_typed", session_id=session_id)
    tk = repo.issue_ticket(conn, session_id=session_id, candidate_text=new_body)
    res = gate.submit(conn, candidate=cand, ticket_id=tk,
                      subject_norm=subject_norm, predicate_norm=predicate_norm,
                      object_text=object_text)
    if not isinstance(res, gate.GateResult):
        # user_typed 应当直写;若走了队列说明前提被破坏,别静默
        raise PanelDenied("编辑未能直接写入(意外走了待审队列)")
    if res.source_confidence != 1.0:
        raise PanelDenied("编辑未取得面板确认权威(票据未生效)")
    # 2. 显式退休旧行(§4.5 不覆盖,新增并标 superseded)——
    #    gate.submit 的直写分支只把 supersedes 记进 source_ref,并不真的退休旧行。
    repo.supersede(conn, old_id, res.fact_id)
    return res


# ── 删除 = tombstone(+ 情节连带删向量点)──────────────────────────
def delete_fact(conn, *, caller: CallerTier, fact_id: int, reason: str = "面板删除") -> None:
    _require_write(caller)
    repo.redact(conn, fact_id, reason, table="l3_fact")


def delete_episode(conn, *, caller: CallerTier, episode_id: int,
                   reason: str = "面板删除") -> None:
    """删情节:PG tombstone + 删 Qdrant 点。★ 两步都要,否则留悬空向量命中。"""
    _require_write(caller)
    row = repo.get_episodes(conn, [episode_id]).get(episode_id)
    sens = row.sensitivity_domain if row else "S0"
    repo.redact(conn, episode_id, reason, table="l2_episode")
    # ★ tombstone 之后 get_episodes 会过滤掉它;向量点必须另外删
    try:
        track_vector.delete_episode_vector(episode_id, sensitivity=sens)
    except Exception:
        # 向量删失败不回滚 PG 删除:留下的是「有 tombstone、向量还在」——
        # 悬空命中会被 get_episodes 的 redacted 过滤挡住(不泄露),可后台重扫修。
        # 反过来(向量删了、PG 没删)才是危险的。所以顺序是 PG 先。
        pass


# ── 手动标密(§4.11.4 第二个 S2 生产者)────────────────────────────
def mark_confidential_fact(conn, *, caller: CallerTier, fact_id: int) -> None:
    """把一条事实标成 S2(单向棘轮,DB 保证不可降级)。"""
    _require_write(caller)
    repo.set_sensitivity(conn, fact_id, "S2", table="l3_fact")


def mark_confidential_episode(conn, *, caller: CallerTier, episode_id: int) -> None:
    """把一条情节标成 S2。★★ 必须连带把向量从 mem_main 迁到 mem_s2 ——
    只改 DB 列不迁向量,等于 DB 声称 S2 而正文留在远程可读的实例里(§4.11.4 破防)。"""
    _require_write(caller)
    row = repo.get_episodes(conn, [episode_id]).get(episode_id)
    if row is None:
        raise PanelDenied("该情节不存在或已被删除")
    if row.sensitivity_domain == "S2":
        return
    old_sens = row.sensitivity_domain
    repo.set_sensitivity(conn, episode_id, "S2", table="l2_episode")   # 棘轮放行 S0/S1→S2
    track_vector.migrate_episode_vector(conn, episode_id, row.body,
                                        from_sensitivity=old_sens, to_sensitivity="S2",
                                        write_seq=row.write_seq)


# ── 语义搜索(档位决定查哪些实例)────────────────────────────────
def search(conn, *, caller: CallerTier, query: str) -> List["track_vector.Passage"]:
    """向量轨搜索。★ 调用方【不能自选查哪个实例】—— 档位决定:
    trusted-local 查 S2 + 非 S2;其余只查非 S2。返回的 passage 正文仍密封,
    由调用方经 trace/snippet 用真实档位解封。"""
    _require_type(caller)
    if caller in (CallerTier.REMOTE_UNAUTH,):
        raise PanelDenied("未认证远程不得搜索记忆")
    out = track_vector.search(conn, query, sensitivity="S0")   # 非 S2 实例
    if _can_see_s2(caller):
        out = out + track_vector.search(conn, query, sensitivity="S2")
        out.sort(key=lambda p: p.score, reverse=True)
    return out


# ── 待审队列:确认/拒绝(必须经 gate,不绕过票据/禁批量)────────────
def list_pending(conn, *, caller: CallerTier, limit: int = 50):
    _require_write(caller)          # 队列只有本机面板能处理
    return repo.list_pending(conn, limit=limit)


def confirm_pending(conn, *, caller: CallerTier, pending_id: int, session_id: str,
                    subject_norm: str, predicate_norm: str, object_text: str):
    """确认一条待审候选 —— 经 gate(签票据 → confirm_pending 消费票据铸 1.0)。
    ★ 一次一个 id,没有批量版本(§4.4.2 禁批量)。"""
    _require_write(caller)
    row = repo.get_pending(conn, pending_id)
    if row is None:
        raise PanelDenied("候选不存在")
    tk = gate.issue_confirm_ticket(conn, pending_id, session_id=session_id)
    return gate.confirm_pending(conn, pending_id, ticket_id=tk,
                                expect_sha256=row.candidate_sha256, session_id=session_id,
                                subject_norm=subject_norm, predicate_norm=predicate_norm,
                                object_text=object_text)


def reject_pending(conn, *, caller: CallerTier, pending_id: int):
    _require_write(caller)
    row = repo.get_pending(conn, pending_id)
    if row is None:
        raise PanelDenied("候选不存在")
    gate.reject_pending(conn, pending_id, expect_sha256=row.candidate_sha256)


# ── 实体图谱本期不暴露(显式声明,不留半吊子)────────────────────
#
# ★ S5【不】暴露实体图谱(7 张 entity 表):repo 对它们无任何读写函数,
#   v_memory_nons2 里的 entity label 只供远程只读。做成"浏览看得到、编辑删除溯源不了"
#   的半吊子态比不做更糟。实体双写(D33①)记入 backlog,本期只保证读写归一化对齐,
#   使「我妹妹叫什么」经 l3_fact 三元组能端到端命中。
ENTITY_GRAPH_EXPOSED = False
