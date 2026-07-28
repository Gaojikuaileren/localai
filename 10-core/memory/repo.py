"""记忆库的【唯一】写入模块(§4.4 Memory Gate 是唯一写入口)

只要存在第二条绕过 Gate 的 INSERT 路径,§4.4.2 的 provenance 分级、§6.9.4 的 E3 凭证拦截、
§4.5 的不覆盖铁律就**同时失效** —— 它们全挂在 Gate 上。故所有写入收敛到本模块,
并由架构测试禁止别处直接持有连接(见 test_repo.py::架构约束)。

★★ 本模块有一件别处没有的职责:**净化数据库异常**。

   PostgreSQL 的错误信息会把**整行正文**搬进异常:
       DETAIL: Key (subject_norm, predicate_norm)=(我, 妹妹) already exists.
       DETAIL: Failing row contains (12, 我妹妹叫小雨, ...).
   这是**纯 str**,类型层拦不住、渲染层看不到、存储层管不着 —— 三层全不拦。
   而异常会进日志、进 traceback、进给调用方的错误响应。
   → 本模块捕获所有 psycopg 异常,只放行 sqlstate 与我们自己写的约束名,
     丢弃 detail/message 里可能携带正文的部分。

★ 身份:连接走 SSPI(D30),认的是**进程的 Windows 身份**。memory-service 以 ai-mem 运行,
  pg_ident 映射 ai-mem→ai_mem_local。因此**没有口令**可存,也没有口令可泄。
"""
from __future__ import annotations

import os
import re
import tomllib
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional

import psycopg
from psycopg import errors as pg_errors

from tainted import TaintedText, seal, unseal_for_storage

PATHS_TOML = Path(__file__).resolve().parents[2] / "config" / "paths.toml"

# 允许出现在错误信息里的东西:sqlstate + 我们自己命名的约束/触发器。
# 这些是**我们写的**,不含用户数据。除此之外一律不放行。
_SAFE_CONSTRAINT_RE = re.compile(
    r"\b(l\d_\w*|entity_\w+|pending_review\w*|secret_ref\w*|mem\.\w+|"
    r"tg_\w+|trg_\w+|\w+_conf_needs_attestation|\w+_derived_conf_cap)\b"
)


class RepoError(RuntimeError):
    """已净化的数据库错误。**保证不含记忆正文。**"""
    def __init__(self, sqlstate: str, hint: str, constraint: str = ""):
        self.sqlstate = sqlstate
        self.constraint = constraint
        super().__init__(f"[{sqlstate}] {hint}" + (f" (约束: {constraint})" if constraint else ""))


def _sanitize(e: Exception) -> RepoError:
    """把 psycopg 异常压成「只有 sqlstate + 约束名」的形式。

    ★ 绝不把 e.diag.message_detail / message_primary 原样带出 —— 那里有正文。
      我们自己触发器抛的中文消息(如「记忆内容不可覆盖」)也可能带旧值片段,同样过滤。
    """
    sqlstate = getattr(e, "sqlstate", None) or "UNKNOWN"
    diag = getattr(e, "diag", None)
    constraint = (getattr(diag, "constraint_name", None) or "") if diag else ""
    if not constraint:
        # 从消息里只捞我们自己命名的标识符,其余丢弃
        m = _SAFE_CONSTRAINT_RE.search(str(e))
        constraint = m.group(0) if m else ""
    hint = {
        "23505": "唯一约束冲突 —— 这条事实已存在",
        "23502": "缺少必填字段(很可能是 sensitivity_domain:每条必须显式定级)",
        "23514": "违反 CHECK 约束",
        "42501": "权限不足 —— 当前角色不允许此操作",
        "23503": "外键约束",
    }.get(sqlstate, "数据库拒绝了这次写入")
    return RepoError(sqlstate, hint, constraint)


# ── 连接 ──────────────────────────────────────────────────────────
def _dsn() -> str:
    """从 paths.toml 取端口;库名/用户固定。SSPI ⇒ 无口令。"""
    port = "5432"
    try:
        with open(PATHS_TOML, "rb") as f:
            port = str(tomllib.load(f)["memory"]["pg_port"])
    except Exception:
        pass
    user = os.environ.get("LOCALAI_PG_USER", "ai_mem_local")
    return f"host=127.0.0.1 port={port} dbname=memory user={user}"


def connect(dsn: Optional[str] = None) -> psycopg.Connection:
    try:
        return psycopg.connect(dsn or _dsn(), autocommit=False)
    except psycopg.Error as e:
        raise _sanitize(e) from None      # ★ from None:切断 __cause__,防原异常经 traceback 泄露


# ── 写入的输入形态 ────────────────────────────────────────────────
@dataclass
class FactWrite:
    """一条待写入的 L3 事实。**正文一律是 TaintedText**,不是 str。

    ★ 服务端产生的字段(write_seq / asserted_at)不在这里 —— 调用方无法提供,
      因此也无法伪造(§4.4.2「客户端自报一律忽略」在类型上成立)。
    """
    statement: TaintedText
    subject_norm: str
    predicate_norm: str
    object_text: TaintedText
    provenance: str
    source_confidence: float
    sensitivity_domain: str
    attestation_kind: Optional[str] = None
    origin_device_id: str = "workstation"
    source_ref: Optional[Dict[str, Any]] = None


_ALLOWED_PROVENANCE = {"user_typed", "user_voice_asr", "tool_result", "rag_chunk", "web_content"}

# ★★ 「什么算用户直述」的**唯一定义**。DB 层的 CHECK、gate 的定级、§4.5 的 is_user_fact
#   三处必须共用同一条判据 —— 分开写就会漂移,而漂移的方向永远是放松。
#
# 它必须是 **allowlist**:凡不在此集合内的 provenance 一律按 derived 处理
#   (封顶 0.4 + 强制 pending)。反过来写成 denylist 的后果 2026-07-28 审查已验明:
#   将来新增任何枚举值都会**自动逃逸全部约束**,且不报错 —— 而最想走这条捷径的
#   恰恰是将来那条外联通道。
#
# ★ 加新 provenance 时不要往这里加,除非它真的等价于「本人当面说的话」。
#   注意 user_voice_asr 的陷阱:语音消息经 ASR 也长这个样子,
#   但**它算不算用户直述取决于麦克风在哪台机器上,而不是取决于输入是不是语音**。
USER_DIRECT = {"user_typed", "user_voice_asr"}


def _server_now() -> datetime:
    """★ 时间戳一律以服务端为准(§4.11.3):客户端时钟不可信。"""
    return datetime.now(timezone.utc)


def insert_fact(conn: psycopg.Connection, w: FactWrite) -> int:
    """写一条 L3 事实。返回新行 id。

    ★ 唯一的写函数之一;架构测试禁止 gate 之外的模块调用它。
    """
    if w.provenance not in _ALLOWED_PROVENANCE:
        raise RepoError("22P02", f"provenance 不在封闭枚举内: {w.provenance!r}")
    if not isinstance(w.statement, TaintedText) or not isinstance(w.object_text, TaintedText):
        raise TypeError("正文必须是 TaintedText —— 裸 str 说明它在别处被解封过而没记账")

    # ★ 解封:写库是四个具名解封点之一,会记账
    stmt = unseal_for_storage(w.statement, table="l3_fact")
    obj = unseal_for_storage(w.object_text, table="l3_fact")

    sql = """
        INSERT INTO mem.l3_fact
          (statement, subject, predicate, object, subject_norm, predicate_norm,
           provenance, source_confidence, sensitivity_domain, attestation_kind,
           origin_device_id, source_ref, asserted_at, confidence)
        VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)
        RETURNING id
    """
    try:
        with conn.cursor() as cur:
            cur.execute(sql, (
                stmt, w.subject_norm, w.predicate_norm, obj,
                w.subject_norm, w.predicate_norm,
                w.provenance, w.source_confidence, w.sensitivity_domain,
                w.attestation_kind, w.origin_device_id,
                psycopg.types.json.Jsonb(w.source_ref) if w.source_ref else None,
                _server_now(),                     # ★ 服务端时间,不接受调用方的
                w.source_confidence,
            ))
            return cur.fetchone()[0]
    except psycopg.Error as e:
        raise _sanitize(e) from None


def supersede(conn: psycopg.Connection, old_id: int, new_id: int) -> None:
    """把旧事实标记为被 new_id 取代。

    ★ 这是写路径里**唯一**的 UPDATE,且只动 superseded_by 一列 ——
      §4.5「冲突不覆盖」。schema 的 append-only 触发器会拦住任何改内容列的企图,
      本模块则从代码层面保证不存在那样的函数。
    """
    try:
        with conn.cursor() as cur:
            cur.execute(
                "UPDATE mem.l3_fact SET superseded_by=%s WHERE id=%s AND superseded_by IS NULL",
                (new_id, old_id))
            if cur.rowcount == 0:
                raise RepoError("00000", "旧事实不存在或已被取代(不可重复 supersede)")
    except psycopg.Error as e:
        raise _sanitize(e) from None


def redact(conn: psycopg.Connection, fact_id: int, reason: str) -> None:
    """D33② tombstone 删除:置 redacted_at + 正文进隔离区。永不物理 DELETE。"""
    try:
        with conn.cursor() as cur:
            cur.execute("""
                INSERT INTO mem.quarantine (src_table, src_id, payload, expires_at, reason,
                                            sensitivity_domain)
                SELECT 'l3_fact', id, to_jsonb(f), now() + interval '30 days', %s, sensitivity_domain
                  FROM mem.l3_fact f WHERE id=%s
            """, (reason, fact_id))
            cur.execute("UPDATE mem.l3_fact SET redacted_at=now() WHERE id=%s AND redacted_at IS NULL",
                        (fact_id,))
            if cur.rowcount == 0:
                raise RepoError("00000", "该事实不存在或已被删除")
    except psycopg.Error as e:
        raise _sanitize(e) from None


# ── 读:结构化轨 ──────────────────────────────────────────────────
@dataclass
class FactRow:
    """检索结果。正文密封,元数据明文(溯源要显示)。"""
    id: int
    statement: TaintedText
    object_text: TaintedText
    subject_norm: str
    predicate_norm: str
    provenance: str
    source_confidence: Optional[float]
    sensitivity_domain: str
    asserted_at: datetime
    origin_device_id: Optional[str]
    write_seq: int
    source_ref: Optional[Dict[str, Any]]

    def trace(self) -> Dict[str, Any]:
        """§4.5 溯源六件套 —— 记忆面板必须能显示「这条是在哪台设备上、什么时候、
        以什么来源记的」。缺任一项,多设备下的可解释性就残缺。"""
        return {
            "asserted_at": self.asserted_at.isoformat(),
            "confidence": self.source_confidence,
            "source_ref": self.source_ref,
            "origin_device_id": self.origin_device_id,
            "write_seq": self.write_seq,
            "provenance": self.provenance,
        }


@dataclass
class EpisodeWrite:
    """一条待写入的 L2 情节(带时间戳的事件)。向量轨检索的就是它。"""
    body: TaintedText
    event_at: datetime
    provenance: str
    source_confidence: float
    sensitivity_domain: str
    attestation_kind: Optional[str] = None
    origin_device_id: str = "workstation"
    source_ref: Optional[Dict[str, Any]] = None


def insert_episode(conn: psycopg.Connection, w: EpisodeWrite) -> int:
    """写一条 L2 情节。返回新行 id。

    ★ event_at 由服务端裁剪:客户端时钟不可信(§4.11.3),且不得晚于现在。
    """
    if not isinstance(w.body, TaintedText):
        raise TypeError("正文必须是 TaintedText")
    if w.provenance not in _ALLOWED_PROVENANCE:
        raise RepoError("22P02", f"provenance 不在封闭枚举内: {w.provenance!r}")
    body = unseal_for_storage(w.body, table="l2_episode")
    now = _server_now()
    event_at = min(w.event_at, now) if w.event_at else now   # ★ 不接受"未来"的事件
    try:
        with conn.cursor() as cur:
            cur.execute("""
                INSERT INTO mem.l2_episode
                  (body, event_at, provenance, source_confidence, sensitivity_domain,
                   attestation_kind, origin_device_id, source_ref, asserted_at, confidence,
                   qdrant_collection)
                VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s) RETURNING id
            """, (body, event_at, w.provenance, w.source_confidence, w.sensitivity_domain,
                  w.attestation_kind, w.origin_device_id,
                  psycopg.types.json.Jsonb(w.source_ref) if w.source_ref else None,
                  now, w.source_confidence,
                  "mem_s2" if w.sensitivity_domain == "S2" else "mem_main"))
            return cur.fetchone()[0]
    except psycopg.Error as e:
        raise _sanitize(e) from None


def set_episode_vector(conn: psycopg.Connection, episode_id: int, point_id: int) -> None:
    """登记向量点 id。★ 固定顺序:先 PG 后 Qdrant —— 崩在中间时,
    留下的是「有正文无向量」(检索少一条,可重建),而不是「有向量无正文」(悬空指针)。"""
    try:
        with conn.cursor() as cur:
            cur.execute("UPDATE mem.l2_episode SET vector_point_id=%s WHERE id=%s",
                        (point_id, episode_id))
    except psycopg.Error as e:
        raise _sanitize(e) from None


@dataclass
class EpisodeRow:
    id: int
    body: TaintedText
    event_at: datetime
    provenance: str
    source_confidence: Optional[float]
    sensitivity_domain: str
    asserted_at: datetime
    origin_device_id: Optional[str]
    write_seq: int
    source_ref: Optional[Dict[str, Any]]

    def trace(self) -> Dict[str, Any]:
        return {"asserted_at": self.asserted_at.isoformat(), "event_at": self.event_at.isoformat(),
                "confidence": self.source_confidence, "source_ref": self.source_ref,
                "origin_device_id": self.origin_device_id, "write_seq": self.write_seq,
                "provenance": self.provenance}


def get_episodes(conn: psycopg.Connection, ids: List[int]) -> Dict[int, EpisodeRow]:
    """按 id 批量取情节正文。★ 向量库只存指针,正文永远从这里取 ——
    于是正文必然经过 seal,天然是 TaintedText。"""
    if not ids:
        return {}
    try:
        with conn.cursor() as cur:
            cur.execute("""
                SELECT id, body, event_at, provenance, source_confidence, sensitivity_domain,
                       asserted_at, origin_device_id, write_seq, source_ref
                  FROM mem.l2_episode
                 WHERE id = ANY(%s) AND superseded_by IS NULL AND redacted_at IS NULL
            """, (list(ids),))
            rows = cur.fetchall()
    except psycopg.Error as e:
        raise _sanitize(e) from None
    return {r[0]: EpisodeRow(
        id=r[0], body=seal(r[1] or "", sensitivity=r[5], source=r[3]),
        event_at=r[2], provenance=r[3],
        source_confidence=float(r[4]) if r[4] is not None else None,
        sensitivity_domain=r[5], asserted_at=r[6], origin_device_id=r[7],
        write_seq=r[8], source_ref=r[9]) for r in rows}


def find_facts(conn: psycopg.Connection, subject_norm: str, predicate_norm: str,
               limit: int = 5) -> List[FactRow]:
    """结构化轨的精确查询 —— 「我妹妹叫什么名字」走这条。

    ★ 只返回【当前有效】的事实:被 supersede 的、被 tombstone 的都排除。
      索引 idx_l3_lookup 正是按这两个条件建的部分索引。
    """
    sql = """
        SELECT id, statement, object, subject_norm, predicate_norm, provenance,
               source_confidence, sensitivity_domain, asserted_at, origin_device_id,
               write_seq, source_ref
          FROM mem.l3_fact
         WHERE subject_norm=%s AND predicate_norm=%s
           AND superseded_by IS NULL AND redacted_at IS NULL
         ORDER BY asserted_at DESC
         LIMIT %s
    """
    try:
        with conn.cursor() as cur:
            cur.execute(sql, (subject_norm, predicate_norm, limit))
            rows = cur.fetchall()
    except psycopg.Error as e:
        raise _sanitize(e) from None
    out = []
    for r in rows:
        # 列序:0 id · 1 statement · 2 object · 3 subject_norm · 4 predicate_norm
        #       5 provenance · 6 source_confidence · 7 sensitivity_domain
        #       8 asserted_at · 9 origin_device_id · 10 write_seq · 11 source_ref
        out.append(FactRow(
            id=r[0],
            statement=seal(r[1], sensitivity=r[7], source=r[5]),   # ★ 出库立刻密封
            object_text=seal(r[2], sensitivity=r[7], source=r[5]),
            subject_norm=r[3], predicate_norm=r[4], provenance=r[5],
            source_confidence=float(r[6]) if r[6] is not None else None,
            sensitivity_domain=r[7], asserted_at=r[8], origin_device_id=r[9],
            write_seq=r[10], source_ref=r[11],
        ))
    return out
