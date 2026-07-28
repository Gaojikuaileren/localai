"""Memory Gate —— 记忆的【唯一】写入口(§4.4)

只要存在一条绕过 Gate 的写路径,§4.4.2 的 provenance 分级、§6.9.4 的 E3 凭证拦截、
§4.5 的不覆盖铁律就**同时失效** —— 它们全挂在这里。

本模块负责四件 schema 强制不了的事:

  1. **白名单剥离** —— 只从请求取 {body, provenance, session_id};其余字段一律不看。
     客户端自报的 user_explicit / source_confidence / write_seq / asserted_at /
     sensitivity_domain 全部忽略(§4.4.2 审查 S1:客户端自报 user_explicit 可铸造
     confidence=1.0 的「用户事实」并自动 supersede 真实事实 —— 写侧最短的提权路径)。
  2. **置信度铸造** —— `mint_confidence()` 是全代码库**唯一**能返回 1.0 的函数,
     且必须消费一张一次性票据。没有票据就没有 1.0。
  3. **服务端定级** —— sensitivity_domain 由服务端定;凭证正则命中时**覆写**调用方声明为 S2。
  4. **E3 凭证拦截** —— 命中即拒绝写入,且**不落盘**(§6.9.8:只记类别/时间/会话,
     不记 body、不记片段、不记哈希 —— 定长 IBAN 的哈希可爆破)。

★★ 还有一件框架层面的:**FastAPI 的校验错误默认泄露被拒字段的值**。
   实测(2026-07-28)默认 422 响应体形如:
     {"detail":[{"type":"extra_forbidden","loc":["body","user_explicit"],"input":true}]}
   `input` 就是被拒的值;若是 body 字段本身校验失败,那里会是**整段正文**。
   而它同时进响应和错误日志。§4.4.2 却要求「只记字段名不记值」——
   **开箱即用的框架行为就违反这条**,必须覆写处理器。见 `sanitized_validation_handler`。
"""
from __future__ import annotations

import hashlib
import secrets
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional, Set

from pydantic import BaseModel, ConfigDict, Field

from tainted import TaintedText, seal, safe_meta
import repo
from repo import FactWrite, RepoError

# 凭证正则族只有一份实现(§6.9.4:五个强制点调同一个函数,五份拷贝必然漂移)。
# ★ 技术债:它目前住在 10-core/gateway,memory 这边靠 sys.path 借用。
#   应移到共享模块 —— 记在 STATE 技术债里,不在此偷偷复制一份。
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "gateway"))
import e1_detector as creds  # noqa: E402


class GateReject(Exception):
    """Gate 拒绝写入。★ 消息里绝不含候选正文。"""
    def __init__(self, reason: str, categories: Optional[Set[str]] = None):
        self.reason = reason
        self.categories = categories or set()
        super().__init__(reason)


# ── 请求模型:白名单剥离 ──────────────────────────────────────────
class CandidateIn(BaseModel):
    """★ extra='forbid' + 只有三个字段。

    服务端产生的一切(置信度/时间/序号/定级)都不在这里 ——
    调用方**在类型上无法提供**,因此无法伪造。这比「收到后忽略」更强。
    """
    model_config = ConfigDict(extra="forbid")
    body: str = Field(min_length=1, max_length=2000)
    provenance: str
    session_id: str = ""


# ── FastAPI 校验错误净化 ─────────────────────────────────────────
def sanitize_validation_errors(errors: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """只保留 loc + type,丢掉 input / ctx / url —— 那里有值。

    §4.4.2:收到自报字段时「记一条审计事件(只记字段名,不记值)」。
    """
    out = []
    for e in errors:
        loc = [str(x) for x in e.get("loc", [])]
        out.append({"loc": loc, "type": e.get("type", "invalid")})
    return out


async def sanitized_validation_handler(request, exc):
    """挂到 FastAPI 上替换默认处理器:`app.add_exception_handler(RequestValidationError, ...)`"""
    from fastapi.responses import JSONResponse
    clean = sanitize_validation_errors(exc.errors())
    self_reported = [e["loc"][-1] for e in clean if e["type"] == "extra_forbidden"]
    if self_reported:
        audit_self_report(self_reported)
    return JSONResponse(
        status_code=400,          # §4.4.2 要求 400,不是 FastAPI 默认的 422
        content={"error": {"type": "invalid_candidate",
                           "message": "候选被拒。服务端产生的字段不接受调用方提供。",
                           "fields": clean}},
    )


_AUDIT: List[Dict[str, Any]] = []


def audit_self_report(field_names: List[str]) -> None:
    """★ 只记字段名,不记值。"""
    _AUDIT.append({"ts": time.time(), "event": "self_reported_fields_stripped",
                   "fields": sorted(set(field_names))})


def audit_log() -> List[Dict[str, Any]]:
    return list(_AUDIT)


# ── 票据:1.0 的唯一来源 ─────────────────────────────────────────
@dataclass
class Ticket:
    ticket_id: str
    session_id: str
    candidate_sha256: str
    expires_at: float
    consumed: bool = False


class TicketStore:
    """一次性票据。S1 内存实现;S3 移到 PG 表并用 UPDATE...RETURNING 原子消费。

    ★ 绑定 candidate_sha256 —— 否则可以「拿旧票据套新内容」:
      面板上看到的是 A,确认时提交的是 B。
    """
    TTL = 300.0

    def __init__(self) -> None:
        self._t: Dict[str, Ticket] = {}

    def issue(self, session_id: str, candidate: str) -> str:
        tid = secrets.token_urlsafe(18)
        self._t[tid] = Ticket(tid, session_id, _sha(candidate), time.time() + self.TTL)
        return tid

    def consume(self, ticket_id: str, session_id: str, candidate: str) -> bool:
        """原子消费:成功返回 True,并把票据置为已用。"""
        t = self._t.get(ticket_id)
        if t is None or t.consumed:
            return False
        if time.time() > t.expires_at:
            return False
        if t.session_id != session_id:
            return False
        if t.candidate_sha256 != _sha(candidate):
            return False          # ★ 展示后被改 → 拒绝
        t.consumed = True
        return True


def _sha(s: str) -> str:
    return hashlib.sha256(s.encode("utf-8")).hexdigest()


_TICKETS = TicketStore()


def issue_ticket(session_id: str, candidate: str) -> str:
    return _TICKETS.issue(session_id, candidate)


def mint_confidence(*, provenance: str, ticket_id: Optional[str],
                    session_id: str, candidate: str) -> tuple[float, Optional[str]]:
    """★★ 全代码库【唯一】能返回 1.0 的函数。返回 (source_confidence, attestation_kind)。

    §4.4.2 四档:
      面板逐条确认(带票据)      1.0   可自动 supersede
      设备签名(远程)            0.6   D33③ 封顶,不可自动 supersede
      助理从对话流推断          0.6   不可自动 supersede
      tool_result/rag/web 派生  0.4   强制 pending
    """
    derived = {"tool_result", "rag_chunk", "web_content"}
    if provenance in derived:
        return 0.4, "derived"
    if ticket_id and _TICKETS.consume(ticket_id, session_id, candidate):
        return 1.0, "panel_ticket"
    # 没有有效票据 —— 无论调用方声称什么,都到不了 1.0
    return 0.6, "assistant_infer"


# ── 服务端定级 ────────────────────────────────────────────────────
def classify_sensitivity(text: str) -> tuple[str, Set[str]]:
    """服务端定级。凭证正则命中 → 强制 S2(**覆写**调用方声明)。

    ★ E3 用的类别子集**排除 high_entropy**(§6.9.4:它误报率高,只用于 E1/E4,
      拿它做 E3 拒绝会把正常写入打死)。
    """
    hits = creds.scan(text).for_e3()
    return ("S2" if hits else "S0"), hits


# ── 写路径 ────────────────────────────────────────────────────────
@dataclass
class GateResult:
    fact_id: int
    sensitivity: str
    source_confidence: float
    attestation_kind: str


def submit_fact(conn, *, candidate: CandidateIn, subject_norm: str, predicate_norm: str,
                object_text: str, ticket_id: Optional[str] = None) -> GateResult:
    """经 Gate 写入一条 L3 事实。这是写路径的正门,也是唯一的门。"""
    # 1. provenance 强制(schema 也有 NOT NULL,这里给出可读的拒绝理由)
    if candidate.provenance not in creds.ALL_CATEGORIES and \
       candidate.provenance not in {"user_typed", "user_voice_asr", "tool_result",
                                     "rag_chunk", "web_content"}:
        raise GateReject(f"provenance 不在封闭枚举内")

    # 2. ★ E3 凭证拦截 —— 命中即拒绝,且【不落盘】(§6.9.8)
    scan_target = f"{candidate.body}\n{object_text}"
    sensitivity, hits = classify_sensitivity(scan_target)
    if hits:
        # 只记 (类别, 时间, 会话id) —— 不记 body、不记片段、不记哈希
        _AUDIT.append({"ts": time.time(), "event": "gate_rejection",
                       "categories": sorted(hits), "session_id": candidate.session_id})
        raise GateReject("候选含疑似凭证,已拒绝写入(不落盘)", hits)

    # 3. 置信度:唯一来源
    conf, attestation = mint_confidence(
        provenance=candidate.provenance, ticket_id=ticket_id,
        session_id=candidate.session_id, candidate=candidate.body)

    # 4. 密封后交给唯一写模块
    w = FactWrite(
        statement=seal(candidate.body, sensitivity=sensitivity, source=candidate.provenance),
        subject_norm=subject_norm, predicate_norm=predicate_norm,
        object_text=seal(object_text, sensitivity=sensitivity, source=candidate.provenance),
        provenance=candidate.provenance,
        source_confidence=conf,
        sensitivity_domain=sensitivity,
        attestation_kind=attestation,
        source_ref={"kind": "flow", "session_id": candidate.session_id},
    )
    fid = repo.insert_fact(conn, w)
    return GateResult(fid, sensitivity, conf, attestation)
