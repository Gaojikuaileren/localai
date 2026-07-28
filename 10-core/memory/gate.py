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
from typing import Any, Callable, Dict, List, Optional, Set

from pydantic import BaseModel, ConfigDict, Field

from tainted import TaintedText, seal, safe_meta
import repo
import sensitivity as sens
from repo import FactWrite, RepoError, USER_DIRECT, _ALLOWED_PROVENANCE

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
                    session_id: str, candidate: str,
                    consume: Optional[Callable[[str, str, str], bool]] = None
                    ) -> tuple[float, Optional[str]]:
    """★★ 全代码库【唯一】能返回 1.0 的函数。返回 (source_confidence, attestation_kind)。

    §4.4.2 四档:
      面板逐条确认(带票据)      1.0   可自动 supersede
      设备签名(远程)            0.6   D33③ 封顶,不可自动 supersede
      助理从对话流推断          0.6   不可自动 supersede
      tool_result/rag/web 派生  0.4   强制 pending

    ★ `consume` 是**注入的票据消费动作**,签名 (ticket_id, session_id, candidate) -> bool。
      S3 把票据从进程内存搬进了 PG(原子消费,不可双花),但**分级规则只有这一份** ——
      两套存储实现同一条规则会漂移,而漂移的方向永远是放松。
      故此处只注入"消费成功与否",判据本身不下放。
      不传时退回进程内 TicketStore(仅供无 DB 的单元测试)。
      两套存储的消费语义由 test_s3 的一致性测试跑同一组断言来防漂移。
    """
    # ★★ allowlist,不是 denylist(2026-07-28 审查后改写)。
    #   原写法是 `derived = {"tool_result","rag_chunk","web_content"}` 再判 in ——
    #   于是**任何新增的 provenance 都不在这个集合里 ⇒ 直接落到最后一行拿 0.6**,
    #   而 0.6 正好等于 D33③ 给"手机有设备签名"的档位。
    #   也就是说:将来接一条外联通道(WhatsApp/Signal/Discord),它的消息会被
    #   自动铸成 0.6 —— 与"经过设备密钥验签的远程写入"同级,而它的身份保证
    #   实际上只是一个可被 SIM swap / 账号劫持的号码。
    #   改成正面列举"什么算用户直述",其余一律 derived(0.4 + 强制 pending),
    #   与 DB 层的 CHECK 形状保持一致 —— 两层用同一条判据,不给漂移留缝。
    if provenance not in USER_DIRECT:
        return 0.4, "derived"
    _consume = consume or (lambda t, s, c: _TICKETS.consume(t, s, c))
    if ticket_id and _consume(ticket_id, session_id, candidate):
        return 1.0, "panel_ticket"
    # 没有有效票据 —— 无论调用方声称什么,都到不了 1.0
    return 0.6, "assistant_infer"


# ── 服务端定级 ────────────────────────────────────────────────────
def scan_credentials(text: str) -> Set[str]:
    """① 凭证检测(E3)。命中 → **拒绝写入,不落盘**。

    ★ 排除 high_entropy(§6.9.4:误报率高,只用于 E1/E4,
      拿它做 E3 拒绝会把正常写入打死)。
    """
    return creds.scan(text).for_e3()


def classify_sensitivity(text: str) -> tuple[str, Set[str]]:
    """② 机密定级。命中 → 标 **域S2**,但**照常写入**。

    ★★ 2026-07-28 规格提取后重写。原实现是:
          hits = creds.scan(text).for_e3(); return ("S2" if hits else "S0"), hits
        ——它把「凭证」与「机密」当成了同一件事,而两者的**动作相反**:
        凭证要拒绝,机密要照写。于是那条返回 "S2" 的分支在调用点被
        无条件 raise 吃掉,成了死分支 ⇒ **全库没有任何写路径能产生一条域S2 记忆行**,
        而整套 S2 隔离(v_memory_nons2 / mem_s2 / 远程永久不可读)都以它存在为前提。
        `test_gate.py` 里那条「凭证命中 → 强制 S2」断言的,是一个永远不会落库的值。

    ★ 判据来自 sensitivity 模块(地址 / 健康 / 亲属细节),与凭证族**分开调参** ——
      两者误报的代价方向相反,详见 sensitivity.py 顶部。
    """
    return sens.classify(text)


# ── 写路径 ────────────────────────────────────────────────────────
@dataclass
class GateResult:
    fact_id: int
    sensitivity: str
    source_confidence: float
    attestation_kind: str


# ★★ 这里曾经有一个 `submit_fact()` —— S3/S4 时删掉了。
#
# 它是 S1 的最小写路径。S3 加了 `submit()`(队列分流 · 熔断 · 冲突检测 ·
# 全字段 E3 扫描)之后,两个函数就是**两条写路径**,而本模块开篇写着:
#     「只要存在一条绕过 Gate 的写路径,§4.4.2 的分级、§6.9.4 的 E3 拦截、
#       §4.5 的不覆盖铁律就同时失效 —— 它们全挂在这里。」
# 两个写函数并存,本身就是那句话说的情况。
#
# ★ 而且它已经真的漏了:S4 把 classify_sensitivity 从「凭证检测」改成
#   「机密定级」(两者动作相反,见该函数注释)之后,submit_fact 里
#   `hits = classify_sensitivity(...)` 那条拦截**不再命中凭证** ——
#   一条带 IBAN 的候选会径直走到 repo.insert_fact。
#   改一个函数的语义、漏掉一个调用方,就是这么发生的。
#
# ⇒ 结论不是"补上它",是"删掉它"。写路径只留 `submit()` 一条。
#   历史调用点(test_s1_acceptance)已改为调 submit()。


# =====================================================================
#  S3 · 完整记忆闸:E3 全面扫描 · 队列 · 冲突 · 熔断
# =====================================================================

# ── E3 的扫描面 ──────────────────────────────────────────────────
def scan_surface(*, body: str, subject_norm: str = "", predicate_norm: str = "",
                 object_text: str = "", extra: Optional[List[str]] = None) -> str:
    """把**所有将要落库的字符串**拼成 E3 的扫描面。

    ★★ 为什么单列一个函数:S1 的 submit_fact 只扫了 body 与 object_text,
       而 subject_norm / predicate_norm 是独立形参 —— 既不受 CandidateIn 的
       extra='forbid' 保护,也不过 E3,却被 repo.insert_fact 原样写进 4 列。
       **把 IBAN 放进 subject_norm 即可完整绕过 E3 并落盘**,
       直接违反 §6.9.10 否定用例①(「PG 全库 grep 不到该串」)。

    ⇒ 规矩:**凡是会落库的字符串,必须从这里过一遍**。
       新增一个持久化字段时,若忘了加进这里,test_s3 的架构断言会红。
    """
    parts = [body, subject_norm, predicate_norm, object_text]
    if extra:
        parts.extend(extra)
    return "\n".join(p for p in parts if p)


# ── 熔断 ─────────────────────────────────────────────────────────
class GateCircuitOpen(GateReject):
    """待审队列积压超限 —— 暂停接受新候选。★ 响亮拒绝,不是静默丢弃。"""


def check_breaker(conn) -> None:
    """入口处的熔断检查。积压本身是攻击信号(§4.4.2)。

    ★ 熔断只挡【需要入队的候选】,不挡本机用户直述 ——
      否则一次刷屏就能让你连自己的记忆都写不进去,那是把 DoS 帮着做完了。
      调用点见 submit_fact。
    """
    if repo.breaker_tripped(conn):
        raise GateCircuitOpen(
            f"待审队列积压已触发熔断,暂停接受新候选。"
            f"请在记忆面板上逐条处理后显式恢复(积压上限 {repo.PENDING_BACKLOG_LIMIT} 条)。")


def _trip_if_backlogged(conn) -> None:
    n = repo.count_pending(conn)
    if n >= repo.PENDING_BACKLOG_LIMIT:
        repo.trip_breaker(conn, "pending_backlog",
                          f"待审积压 {n} 条 ≥ 上限 {repo.PENDING_BACKLOG_LIMIT}")


# ── 冲突 ─────────────────────────────────────────────────────────
def detect_conflict(conn, subject_norm: str, predicate_norm: str,
                    new_object: str) -> Optional[int]:
    """返回「这条候选将要取代谁」的 id;没有冲突则 None。

    ★ 判据是结构性的:同 subject_norm + predicate_norm 的活跃行。
      **不判断语义是否真的矛盾** —— 那需要 LLM,而 LLM 判错的方向不对称:
      判成"不冲突"就并列存两条互相矛盾的事实,检索时两条都返回,
      而没有任何东西会报错(§4.5 铁律只管 supersede 方向,不管并列)。

    ★ 若已有多条活跃行,返回**最新的那条** —— 但这本身说明库里已经有并列冲突,
      调用方(面板)应当把全部候选摊开给人看。
    """
    rows = repo.find_conflicts(conn, subject_norm, predicate_norm)
    if not rows:
        return None
    return rows[0].id


# ── S3 版写入:分流 ───────────────────────────────────────────────
@dataclass
class GateQueued:
    """派生候选没有直接进库,而是进了待审队列。"""
    pending_id: int
    supersedes_ref: Optional[int]
    candidate_sha256: str
    sensitivity: str


def submit(conn, *, candidate: CandidateIn, subject_norm: str, predicate_norm: str,
           object_text: str, ticket_id: Optional[str] = None):
    """★★ S3 的写入正门。按 provenance 分流:

        用户直述(user_typed / user_voice_asr) → 直接写 l3_fact
        其余一切(含将来新增的枚举)            → **进待审队列**,等人逐条确认

    返回 GateResult(直写)或 GateQueued(入队)。

    ★ 分流是 allowlist 形状:新增 provenance 默认落到队列一侧。
      S1 的 submit_fact 对派生来源照样直写 l3_fact —— §4.4.2「强制 pending」
      此前**没有任何强制点**,一条 web_content 可以静默成为活跃 L3 事实。
    """
    # 1. provenance 封闭枚举
    #    ★ S1 那版写成「不在凭证类别集合 AND 不在 provenance 枚举 → 拒」,
    #      于是 provenance='iban' 之类的凭证类别名**可以通过 Gate 的校验**
    #      (最终被 repo 兜住)。那是一条假的校验;S3 新增的队列路径若不经
    #      repo.insert_fact,这些值就会真的落表。此处改为只认 provenance 枚举。
    if candidate.provenance not in _ALLOWED_PROVENANCE:
        raise GateReject("provenance 不在封闭枚举内")

    is_direct = candidate.provenance in USER_DIRECT

    # 2. 熔断:只挡需要入队的候选,不挡本机用户直述
    if not is_direct:
        check_breaker(conn)

    # 3. ★ 两步,顺序不能反 —— 它们是【两件事】(见 classify_sensitivity 的注释):
    #      (a) 凭证 → 拒绝写入,不落盘
    #      (b) 机密 → 照写,但强制标 域S2
    #    扫的是【全部将落库的字符串】,不只是 body。
    surface = scan_surface(body=candidate.body, subject_norm=subject_norm,
                           predicate_norm=predicate_norm, object_text=object_text)

    cred_hits = scan_credentials(surface)
    if cred_hits:
        # 只记 (类别, 时间, 会话) —— 不记 body、不记片段、不记哈希(§6.9.8)
        repo.log_gate_rejection(conn, sorted(cred_hits), candidate.session_id)
        _AUDIT.append({"ts": time.time(), "event": "gate_rejection",
                       "categories": sorted(cred_hits), "session_id": candidate.session_id})
        raise GateReject("候选含疑似凭证,已拒绝写入(不落盘)", cred_hits)

    # ★ 走到这里说明不含凭证。现在才定机密等级 —— 命中也【不拒绝】。
    sensitivity, conf_hits = classify_sensitivity(surface)
    if conf_hits:
        _AUDIT.append({"ts": time.time(), "event": "classified_confidential",
                       "classes": sorted(conf_hits), "session_id": candidate.session_id})

    # 4. 冲突检测 —— 「这条将取代哪条现有事实」是确认时的主视觉(§4.4.2)
    supersedes = detect_conflict(conn, subject_norm, predicate_norm, object_text)

    # 5. 置信度:唯一来源。DB 票据,原子消费。
    conf, attestation = mint_confidence(
        provenance=candidate.provenance, ticket_id=ticket_id,
        session_id=candidate.session_id, candidate=candidate.body,
        consume=lambda t, s, c: repo.consume_ticket(conn, t, session_id=s, candidate_text=c))

    sealed = lambda s: seal(s, sensitivity=sensitivity, source=candidate.provenance)

    if not is_direct:
        # ── 派生:入队,不进库 ──
        pid = repo.insert_pending(conn, repo.PendingWrite(
            body=sealed(candidate.body), provenance=candidate.provenance,
            source_confidence=conf, sensitivity_domain=sensitivity,
            session_id=candidate.session_id, supersedes_ref=supersedes))
        _trip_if_backlogged(conn)
        return GateQueued(pid, supersedes, repo.candidate_hash(candidate.body), sensitivity)

    # ── 用户直述:直接进库 ──
    w = FactWrite(
        statement=sealed(candidate.body), subject_norm=subject_norm,
        predicate_norm=predicate_norm, object_text=sealed(object_text),
        provenance=candidate.provenance, source_confidence=conf,
        sensitivity_domain=sensitivity, attestation_kind=attestation,
        source_ref={"kind": "flow", "session_id": candidate.session_id,
                    "supersedes_candidate": supersedes})
    fid = repo.insert_fact(conn, w)
    return GateResult(fid, sensitivity, conf, attestation)


# ── 逐条确认 / 拒绝 ──────────────────────────────────────────────
def issue_confirm_ticket(conn, pending_id: int, *, session_id: str) -> str:
    """面板展示一条候选时签发的票据。绑 (会话, 候选哈希, 队列行)。

    ★ 面板必须先拿票据再确认 —— 票据的存在时间就是「你在看这条」的那段时间。
    """
    row = repo.get_pending(conn, pending_id)
    if row is None or row.status != "pending":
        raise GateReject("候选不存在或已处理")
    body = unseal_for_prompt_free(row.body)
    return repo.issue_ticket(conn, session_id=session_id, candidate_text=body,
                             pending_id=pending_id)


def confirm_pending(conn, pending_id: int, *, ticket_id: str, expect_sha256: str,
                    session_id: str, subject_norm: str, predicate_norm: str,
                    object_text: str) -> GateResult:
    """把一条待审候选提升为正式事实。**一次只处理一条**。

    ★★ `ticket_id` 必填,且 1.0 仍然只能由 `mint_confidence` 铸造。
       这条路径最初写成硬编码 `source_confidence=1.0` —— 那等于给自己开了一扇
       绕过票据的门:**能调到这个函数的人就能拿 1.0**,而票据机制的全部意义
       就是「1.0 必须消费一张一次性票据」。写完立刻发现,此处纠正。
       ⇒ 不变式保持为:全代码库只有 mint_confidence 会产出 1.0,且它必须消费票据。

    ★ `expect_sha256` 是另一半:它绑「面板上显示的那条」与「现在要确认的这条」。
      DB 侧 tg_pending_immutable 冻结候选内容(防被改),
      哈希回绑防的是 **你看的根本不是这条**。

    ★ 为什么没有批量版本:§4.4.2「逐条确认,禁批量」。
      DB 的 tg_no_bulk_review 只能挡「一条语句改多行」,挡不住应用层循环发 50 条;
      而「确认」的实质动作是往 l3_fact 里 INSERT,那个触发器根本不在路径上。
      ⇒ 禁批量只能靠这里:**本函数一次只收一个 id,且没有复数形式的姊妹函数**。
    """
    row = repo.get_pending(conn, pending_id)
    if row is None:
        raise GateReject("候选不存在")
    if row.status != "pending":
        raise GateReject(f"候选已处于终态 {row.status},不可重复处理")
    if row.candidate_sha256 != expect_sha256:
        raise GateReject("候选哈希与面板所示不符 —— 拒绝确认")

    body = unseal_for_prompt_free(row.body)

    # ★ 确认动作让它成为【用户直述】:用户在面板上点确认 = 把这句话说成自己的了。
    #   「它源自一次工具调用」属于溯源,记进 source_ref,不记进 provenance ——
    #   否则 (tool_result, panel_ticket) 这种组合会让派生来源冒领人类权威
    #   (DB 侧 *_panel_ticket_needs_user 已从写入侧堵死该组合)。
    conf, attestation = mint_confidence(
        provenance="user_typed", ticket_id=ticket_id,
        session_id=session_id, candidate=body,
        consume=lambda t, s, c: repo.consume_ticket(conn, t, session_id=s, candidate_text=c))
    if attestation != "panel_ticket":
        raise GateReject("确认需要一张有效的面板票据(未消费成功)")

    w = FactWrite(
        statement=seal(body, sensitivity=row.sensitivity_domain, source="user_typed"),
        subject_norm=subject_norm, predicate_norm=predicate_norm,
        object_text=seal(object_text, sensitivity=row.sensitivity_domain, source="user_typed"),
        provenance="user_typed", source_confidence=conf,
        sensitivity_domain=row.sensitivity_domain, attestation_kind=attestation,
        source_ref={"kind": "panel_confirm", "pending_id": pending_id,
                    "origin_provenance": row.provenance, "session_id": session_id})
    fid = repo.insert_fact(conn, w)

    if row.supersedes_ref:
        repo.supersede(conn, row.supersedes_ref, fid)

    repo.set_pending_status(conn, pending_id, "approved", expect_sha256=expect_sha256)
    return GateResult(fid, row.sensitivity_domain, conf, attestation)


def reject_pending(conn, pending_id: int, *, expect_sha256: str) -> None:
    """拒绝一条候选。**不产生任何内容行**,被拒正文不复制进其它表。"""
    repo.set_pending_status(conn, pending_id, "rejected", expect_sha256=expect_sha256)


def clear_backlog_breaker(conn, *, ticket_id: str, session_id: str) -> None:
    """恢复熔断。★ 裁定:恢复也走面板票据,与逐条确认同级。

    理由:熔断是「有人在往队列里灌东西」的信号,自动恢复等于把告警关掉继续跑。
    """
    if not repo.consume_ticket(conn, ticket_id, session_id=session_id,
                               candidate_text="clear_backlog_breaker"):
        raise GateReject("恢复熔断需要一张有效的面板票据")
    repo.clear_breaker(conn, "pending_backlog", cleared_by=f"panel:{session_id}")


def unseal_for_prompt_free(t: TaintedText) -> str:
    """确认流程内部取正文 —— 走具名解封点(会记账)。"""
    from tainted import unseal_for_client
    return unseal_for_client(t, caller="trusted-local")
