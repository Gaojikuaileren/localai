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

import hashlib
import os
import re
import secrets
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


# ★ 可被 tombstone / 手动标 S2 的内容表白名单。
#   与 schema-p3a §3(加 redacted_at 的表)、§6(绑 append-only)一致 ——
#   泛化的 (table,id) 操作必须先过这个白名单,否则一个拼错的表名会拼进 SQL。
_CONTENT_TABLES = frozenset({
    "l1_session_summary", "l2_episode", "l3_fact",
    "entity_person", "entity_event", "entity_preference", "entity_project",
    "entity_device", "entity_place", "entity_thing"})


def redact(conn: psycopg.Connection, ref, reason: str, *, table: str = "l3_fact") -> None:
    """D33② tombstone 删除:置 redacted_at + 正文进隔离区。永不物理 DELETE。

    ★ S5 泛化到 (table, id):原实现硬编码 'l3_fact',而 schema/roles 给全部 10 张
      内容表都加了 redacted_at、撤了 DELETE —— 承诺全局可删,实际只有事实能删。
      情节/实体的删除此前是空的。

    调用形态两种(向后兼容):
      redact(conn, fact_id, reason)                    → 默认 l3_fact
      redact(conn, id, reason, table="l2_episode")     → 指定表
    ★ 删情节还须调 track_vector.delete_episode_vector 删掉 Qdrant 点 —— 那不在本函数里,
      因为 repo 不依赖向量层(分层)。面板层负责把两步串起来(见 panel.delete)。
    """
    if table not in _CONTENT_TABLES:
        raise RepoError("22P02", f"不是内容表: {table!r}")
    try:
        with conn.cursor() as cur:
            # ★ table 已过白名单,可安全内插;id 仍走参数
            cur.execute(f"""
                INSERT INTO mem.quarantine (src_table, src_id, payload, expires_at, reason,
                                            sensitivity_domain)
                SELECT %s, id, to_jsonb(f), now() + interval '30 days', %s, sensitivity_domain
                  FROM mem.{table} f WHERE id=%s
            """, (table, reason, ref))
            cur.execute(f"UPDATE mem.{table} SET redacted_at=now() "
                        f"WHERE id=%s AND redacted_at IS NULL", (ref,))
            if cur.rowcount == 0:
                raise RepoError("00000", "该行不存在或已被删除")
    except psycopg.Error as e:
        raise _sanitize(e) from None


def as_jsonb(obj: Any):
    """把 dict 包成 psycopg Jsonb —— 让别的模块不必直接 import psycopg(守分层)。"""
    return psycopg.types.json.Jsonb(obj)


def get_system_state(conn: psycopg.Connection, key: str) -> Optional[Dict[str, Any]]:
    """读系统状态标记(如 cold_start_completed)。不存在则 None。"""
    try:
        with conn.cursor() as cur:
            cur.execute("SELECT value FROM mem.system_state WHERE key=%s", (key,))
            r = cur.fetchone()
    except psycopg.Error as e:
        raise _sanitize(e) from None
    return r[0] if r else None


def set_system_state_once(conn: psycopg.Connection, key: str, value: Dict[str, Any]) -> None:
    """置一次性系统标记。★ 表只授了 INSERT(无 UPDATE),已存在会抛 —— 一次性由 DB 保证。"""
    try:
        with conn.cursor() as cur:
            cur.execute("INSERT INTO mem.system_state (key, value) VALUES (%s, %s)",
                        (key, psycopg.types.json.Jsonb(value)))
    except psycopg.Error as e:
        raise _sanitize(e) from None


def set_sensitivity(conn: psycopg.Connection, row_id: int, level: str,
                    *, table: str = "l3_fact") -> None:
    """手动标记敏感度(§4.11.4 的第二个 S2 生产者)。

    ★ 只做 DB 那一列。DB 侧 tg_sensitivity_ratchet 保证【单向收紧】(S0/S1→S2,反向拒)。
    ★★ 若把一条【情节】标 S2,它的向量还在 mem_main(非 S2 实例)——DB 声称 S2、
       向量却在远程可读的实例里,§4.11.4 结构隔离被悄悄破坏。所以情节标 S2 必须
       连带迁移向量。那一步在 panel.mark_confidential 里串(repo 不碰向量层)。
    """
    if table not in _CONTENT_TABLES:
        raise RepoError("22P02", f"不是内容表: {table!r}")
    if level not in ("S0", "S1", "S2"):
        raise RepoError("22P02", f"非法敏感度: {level!r}")
    try:
        with conn.cursor() as cur:
            cur.execute(f"UPDATE mem.{table} SET sensitivity_domain=%s WHERE id=%s",
                        (level, row_id))
            if cur.rowcount == 0:
                raise RepoError("00000", "该行不存在")
    except psycopg.Error as e:
        raise _sanitize(e) from None


def get_fact(conn: psycopg.Connection, fact_id: int) -> Optional["FactRow"]:
    """按 id 取单条活跃事实(溯源展开用)。"""
    try:
        with conn.cursor() as cur:
            cur.execute("""
                SELECT id, statement, object, subject_norm, predicate_norm, provenance,
                       source_confidence, sensitivity_domain, asserted_at, origin_device_id,
                       write_seq, source_ref
                  FROM mem.l3_fact
                 WHERE id=%s AND superseded_by IS NULL AND redacted_at IS NULL
            """, (fact_id,))
            r = cur.fetchone()
    except psycopg.Error as e:
        raise _sanitize(e) from None
    if r is None:
        return None
    return FactRow(
        id=r[0], statement=seal(r[1], sensitivity=r[7], source=r[5]),
        object_text=seal(r[2], sensitivity=r[7], source=r[5]),
        subject_norm=r[3], predicate_norm=r[4], provenance=r[5],
        source_confidence=float(r[6]) if r[6] is not None else None,
        sensitivity_domain=r[7], asserted_at=r[8], origin_device_id=r[9],
        write_seq=r[10], source_ref=r[11])


def list_facts(conn: psycopg.Connection, *, include_s2: bool,
               limit: int = 50, offset: int = 0) -> List["FactRow"]:
    """★ 浏览:列出当前活跃事实(§4.4.1「写入后必须:用户可见」)。

    此前全代码库没有任何「列出全部活跃事实」的读函数 —— 浏览的最基本形态缺失。

    ★★ `include_s2` 由**调用方的档位**决定,不是可选开关:面板层根据 CallerTier
       传 True/False。非 trusted-local 一律 False —— S2 行连列出来都不行
       (行的存在性本身也是信息,§4.11.4)。
    """
    where = "superseded_by IS NULL AND redacted_at IS NULL"
    if not include_s2:
        where += " AND sensitivity_domain <> 'S2'"
    try:
        with conn.cursor() as cur:
            cur.execute(f"""
                SELECT id, statement, object, subject_norm, predicate_norm, provenance,
                       source_confidence, sensitivity_domain, asserted_at, origin_device_id,
                       write_seq, source_ref
                  FROM mem.l3_fact
                 WHERE {where}
                 ORDER BY asserted_at DESC
                 LIMIT %s OFFSET %s
            """, (limit, offset))
            rows = cur.fetchall()
    except psycopg.Error as e:
        raise _sanitize(e) from None
    return [FactRow(
        id=r[0], statement=seal(r[1], sensitivity=r[7], source=r[5]),
        object_text=seal(r[2], sensitivity=r[7], source=r[5]),
        subject_norm=r[3], predicate_norm=r[4], provenance=r[5],
        source_confidence=float(r[6]) if r[6] is not None else None,
        sensitivity_domain=r[7], asserted_at=r[8], origin_device_id=r[9],
        write_seq=r[10], source_ref=r[11]) for r in rows]


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


# =====================================================================
#  S3 · 队列 · 冲突 · 熔断 · 票据 · 拒绝审计
#  这一段全部是 S3 新增。设计依据见 schema-p3a.sql 的 S3-* 各节。
# =====================================================================

# ── 冲突检测 ─────────────────────────────────────────────────────
def find_conflicts(conn: psycopg.Connection, subject_norm: str,
                   predicate_norm: str) -> List[FactRow]:
    """找出与 (subject_norm, predicate_norm) 相同的【活跃】事实。

    ★ 判据是**结构性的**,不用 LLM:同主语 + 同谓词的活跃行即冲突候选。
      语义冲突(「我住柏林」vs「我搬到慕尼黑了」)留给面板上的人判断 ——
      机器只负责把「这条要取代谁」摆到你面前(§4.4.2:确认时它是主视觉)。

    ★ 为什么不做成"自动判断是否真冲突":那需要 LLM,而 LLM 判错的方向是
      **不对称**的 —— 判成"不冲突"就并列存两条互相矛盾的事实,检索时两条都返回,
      而没有任何东西会报错。§4.5 铁律只管 supersede 方向,不管并列;
      绕过它的成本本来就只是"少调一次 UPDATE"。所以宁可多报,让人来筛。
    """
    if not subject_norm or not predicate_norm:
        return []
    return find_facts(conn, subject_norm, predicate_norm, limit=20)


# ── 待审队列 ─────────────────────────────────────────────────────
@dataclass
class PendingWrite:
    """一条待人工确认的候选。★ 与 FactWrite 一样,没有 write_seq / asserted_at ——
    调用方在类型上无法提供,因此无法伪造。"""
    body: TaintedText
    provenance: str
    source_confidence: float
    sensitivity_domain: str
    session_id: str
    supersedes_ref: Optional[int] = None
    origin_device_id: str = "workstation"
    # ★ float 而非 int:expires_at 一经入队即被 DB 冻结(不可延长也不可缩短),
    #   所以"造一条马上过期的候选"只能在入队时指定,不能事后改 —— 测试因此需要小数。
    ttl_days: float = 14.0


@dataclass
class PendingRow:
    id: int
    body: TaintedText
    provenance: str
    source_confidence: Optional[float]
    sensitivity_domain: str
    status: str
    supersedes_ref: Optional[int]
    candidate_sha256: str
    session_id: Optional[str]
    created_at: datetime
    expires_at: Optional[datetime]
    origin_device_id: Optional[str]

    def trace(self) -> Dict[str, Any]:
        return {"created_at": self.created_at.isoformat(),
                "expires_at": self.expires_at.isoformat() if self.expires_at else None,
                "provenance": self.provenance, "confidence": self.source_confidence,
                "origin_device_id": self.origin_device_id, "session_id": self.session_id,
                "supersedes_ref": self.supersedes_ref}


def candidate_hash(text: str) -> str:
    """候选哈希 —— 面板显示与确认提交之间的绑定物。"""
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def insert_pending(conn: psycopg.Connection, w: PendingWrite) -> int:
    """把候选放进待审队列。返回队列行 id。

    ★ 派生来源【只能】走这条路进库 —— gate 不再对它们调 insert_fact。
      DB 侧 tg_pending_initial_state 保证它们以 pending 入队。
    """
    if not isinstance(w.body, TaintedText):
        raise TypeError("正文必须是 TaintedText")
    if w.provenance not in _ALLOWED_PROVENANCE:
        raise RepoError("22P02", f"provenance 不在封闭枚举内: {w.provenance!r}")
    body = unseal_for_storage(w.body, table="pending_review")
    sha = candidate_hash(body)
    now = _server_now()
    try:
        with conn.cursor() as cur:
            cur.execute("""
                INSERT INTO mem.pending_review
                  (candidate_body, provenance, source_confidence, sensitivity_domain,
                   supersedes_ref, status, origin_device_id, session_id,
                   candidate_sha256, asserted_at, expires_at)
                VALUES (%s,%s,%s,%s,%s,'pending',%s,%s,%s,%s,%s) RETURNING id
            """, (psycopg.types.json.Jsonb({"text": body}), w.provenance,
                  w.source_confidence, w.sensitivity_domain, w.supersedes_ref,
                  w.origin_device_id, w.session_id, sha, now,
                  now + timedelta(days=w.ttl_days)))
            return cur.fetchone()[0]
    except psycopg.Error as e:
        raise _sanitize(e) from None


def _pending_row(r) -> PendingRow:
    body_text = (r[1] or {}).get("text", "") if isinstance(r[1], dict) else ""
    return PendingRow(
        id=r[0], body=seal(body_text, sensitivity=r[4], source=r[2]),
        provenance=r[2], source_confidence=float(r[3]) if r[3] is not None else None,
        sensitivity_domain=r[4], status=r[5], supersedes_ref=r[6],
        candidate_sha256=r[7], session_id=r[8], created_at=r[9],
        expires_at=r[10], origin_device_id=r[11])


_PENDING_COLS = """id, candidate_body, provenance, source_confidence, sensitivity_domain,
                   status, supersedes_ref, candidate_sha256, session_id,
                   created_at, expires_at, origin_device_id"""


def get_pending(conn: psycopg.Connection, pending_id: int) -> Optional[PendingRow]:
    try:
        with conn.cursor() as cur:
            cur.execute(f"SELECT {_PENDING_COLS} FROM mem.pending_review WHERE id=%s",
                        (pending_id,))
            r = cur.fetchone()
    except psycopg.Error as e:
        raise _sanitize(e) from None
    return _pending_row(r) if r else None


def list_pending(conn: psycopg.Connection, limit: int = 50) -> List[PendingRow]:
    """列出待审候选(最旧的在前 —— 队列是先进先出,不给"挑软柿子"留空间)。"""
    try:
        with conn.cursor() as cur:
            cur.execute(f"""SELECT {_PENDING_COLS} FROM mem.pending_review
                             WHERE status='pending' ORDER BY id LIMIT %s""", (limit,))
            rows = cur.fetchall()
    except psycopg.Error as e:
        raise _sanitize(e) from None
    return [_pending_row(r) for r in rows]


def count_pending(conn: psycopg.Connection) -> int:
    """熔断计数。★ 只数 status='pending' —— 已处理的不占额度。"""
    try:
        with conn.cursor() as cur:
            cur.execute("SELECT count(*) FROM mem.pending_review WHERE status='pending'")
            return int(cur.fetchone()[0])
    except psycopg.Error as e:
        raise _sanitize(e) from None


def set_pending_status(conn: psycopg.Connection, pending_id: int, status: str,
                       *, expect_sha256: str) -> None:
    """把队列行转到终态。

    ★★ `expect_sha256` 是必填的:它是「面板上显示的那条」与「现在要确认的这条」
       之间的绑定。对不上就拒 —— 防「面板看到 A、确认进库 B」。
       DB 侧 tg_pending_immutable 已冻结候选内容,这里是第二道
       (冻结防的是"被改",哈希回绑防的是"你看的根本不是这条")。
    """
    if status not in {"approved", "rejected", "expired"}:
        raise RepoError("22P02", f"非法的终态: {status!r}")
    try:
        with conn.cursor() as cur:
            cur.execute("""UPDATE mem.pending_review SET status=%s
                            WHERE id=%s AND status='pending' AND candidate_sha256=%s""",
                        (status, pending_id, expect_sha256))
            if cur.rowcount == 0:
                raise RepoError("00000",
                                "候选不存在、已被处理,或哈希与面板所示不符(拒绝执行)")
    except psycopg.Error as e:
        raise _sanitize(e) from None


def expire_pending(conn: psycopg.Connection) -> int:
    """GC:把过期候选转 expired,正文搬进隔离区。返回处理条数。

    ★ 裁定:过期【进隔离区】而不是静默删除(§12.4 永不 delete 只移隔离区),
      也不放它们永久占着熔断额度 —— 否则塞满队列即达成永久 DoS。
    """
    try:
        with conn.cursor() as cur:
            cur.execute("""
                INSERT INTO mem.quarantine (src_table, src_id, payload, expires_at,
                                            reason, sensitivity_domain)
                SELECT 'pending_review', id, to_jsonb(p), now() + interval '30 days',
                       '待审候选超时未处理', sensitivity_domain
                  FROM mem.pending_review p
                 WHERE status='pending' AND expires_at IS NOT NULL AND expires_at < now()
            """)
            cur.execute("""UPDATE mem.pending_review SET status='expired'
                            WHERE status='pending' AND expires_at IS NOT NULL
                              AND expires_at < now()""")
            return cur.rowcount
    except psycopg.Error as e:
        raise _sanitize(e) from None


# ── 票据:1.0 的唯一来源,原子消费 ────────────────────────────────
def issue_ticket(conn: psycopg.Connection, *, session_id: str, candidate_text: str,
                 pending_id: Optional[int] = None, ttl_seconds: int = 300) -> str:
    """签发一次性票据。绑 (会话, 候选哈希),可选绑队列行。"""
    tid = secrets.token_urlsafe(24)
    try:
        with conn.cursor() as cur:
            cur.execute("""INSERT INTO mem.write_ticket
                             (ticket_id, session_id, candidate_sha256, pending_id, expires_at)
                           VALUES (%s,%s,%s,%s, now() + make_interval(secs => %s))""",
                        (tid, session_id, candidate_hash(candidate_text), pending_id,
                         ttl_seconds))
    except psycopg.Error as e:
        raise _sanitize(e) from None
    return tid


def consume_ticket(conn: psycopg.Connection, ticket_id: str, *, session_id: str,
                   candidate_text: str) -> bool:
    """消费票据。★ 必须是 UPDATE...RETURNING —— 「先查再改」在并发下可双花。

    返回 True 表示本次消费成功(且此后该票据再不可用)。
    """
    try:
        with conn.cursor() as cur:
            cur.execute("""
                UPDATE mem.write_ticket SET consumed_at = now()
                 WHERE ticket_id = %s
                   AND consumed_at IS NULL
                   AND expires_at > now()
                   AND session_id = %s
                   AND candidate_sha256 = %s
             RETURNING ticket_id
            """, (ticket_id, session_id, candidate_hash(candidate_text)))
            return cur.fetchone() is not None
    except psycopg.Error as e:
        raise _sanitize(e) from None


# ── 熔断:状态落库(内存态"重启即复位"等于没有)────────────────────
PENDING_BACKLOG_LIMIT = 50      # §4.4.2:积压上限 50 条 —— 积压本身是攻击信号


def breaker_tripped(conn: psycopg.Connection, name: str = "pending_backlog") -> bool:
    try:
        with conn.cursor() as cur:
            cur.execute("""SELECT 1 FROM mem.circuit_breaker
                            WHERE name=%s AND tripped_at IS NOT NULL AND cleared_at IS NULL""",
                        (name,))
            return cur.fetchone() is not None
    except psycopg.Error as e:
        raise _sanitize(e) from None


def trip_breaker(conn: psycopg.Connection, name: str, reason: str) -> None:
    """跳闸。幂等:已跳则不动(保留最早的 tripped_at 与原因)。"""
    try:
        with conn.cursor() as cur:
            cur.execute("""
                INSERT INTO mem.circuit_breaker (name, tripped_at, reason)
                VALUES (%s, now(), %s)
                ON CONFLICT (name) DO UPDATE
                   SET tripped_at = coalesce(mem.circuit_breaker.tripped_at, now()),
                       reason     = coalesce(mem.circuit_breaker.reason, EXCLUDED.reason),
                       cleared_at = NULL, cleared_by = NULL
                 WHERE mem.circuit_breaker.cleared_at IS NOT NULL
                    OR mem.circuit_breaker.tripped_at IS NULL
            """, (name, reason))
    except psycopg.Error as e:
        raise _sanitize(e) from None


def clear_breaker(conn: psycopg.Connection, name: str, *, cleared_by: str) -> None:
    """恢复。★ 裁定:恢复走面板票据,与逐条确认同级 —— 调用方须先消费一张票据。

    规格里那句「需显式恢复」是循环引用(§6.9.7 说「同 §4.4.2」,而 §4.4.2 里
    没有这句话),故此处裁定。理由:熔断是「有人在往队列里灌东西」的信号,
    自动恢复等于把告警关掉继续跑。
    """
    try:
        with conn.cursor() as cur:
            cur.execute("""UPDATE mem.circuit_breaker
                              SET cleared_at=now(), cleared_by=%s
                            WHERE name=%s AND cleared_at IS NULL""",
                        (cleared_by, name))
            if cur.rowcount == 0:
                raise RepoError("00000", "该熔断器未处于跳闸状态")
    except psycopg.Error as e:
        raise _sanitize(e) from None


# ── E3 拒绝审计:必须落库 ──────────────────────────────────────────
def log_gate_rejection(conn: psycopg.Connection, categories: List[str],
                       session_id: str) -> None:
    """E3 命中的审计。★ 只记 (类别, 时间, 会话) —— 不记 body、不记片段、不记哈希。

    §6.9.8:定长 IBAN 的哈希可爆破,所以连哈希都不能记。
    每个类别一行(表结构就是每行一个 category 枚举)。

    ★★ 走**独立连接**并立即提交,不用调用方的 conn。

      2026-07-28 实测发现的缺陷:审计原本写在调用方的事务里,而 Gate 命中后
      立刻 raise —— 调用方 rollback 时**把审计记录一起回滚了**。
      净效果是:攻击者的每一次被拒尝试都不留痕迹,而 §9.3 的告警正要靠这张表计数。
      「拒绝要被审计」于是只在"调用方恰好没有回滚"时成立。

      ⇒ 审计必须活在**被审计对象的事务之外**。拒绝是稀有事件(要么是攻击、
        要么是误操作),多开一条短连接的代价可以忽略,换来的是"回滚抹不掉痕迹"。

    ★ 本函数**永不抛异常**:审计失败不该把主流程的拒绝理由盖掉
      (那会把"候选含凭证"变成"数据库错误",用户看到的原因就错了)。
      审计写不进去时只在 stderr 留一行 —— 它本身就是需要被注意到的异常状态。
    """
    if not categories:
        return
    audit_conn = None
    try:
        audit_conn = psycopg.connect(_dsn(), autocommit=True)
        with audit_conn.cursor() as cur:
            for c in sorted(set(categories)):
                cur.execute("""INSERT INTO mem.gate_rejection
                                 (category, session_id, sensitivity_domain)
                               VALUES (%s, %s, 'S2')""", (c, session_id or "unknown"))
    except Exception as e:                      # noqa: BLE001 —— 见上,审计不得掩盖主因
        import sys as _sys
        print(f"[gate_rejection 审计写入失败] {type(e).__name__} —— "
              f"拒绝本身仍然生效,但这一条没能留痕", file=_sys.stderr)
    finally:
        if audit_conn is not None:
            try:
                audit_conn.close()
            except Exception:
                pass
