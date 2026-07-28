# -*- coding: utf-8 -*-
"""L4 程序记忆(L4-proc)—— 独立写路径 · 哈希绑定批准 · executor(§4.4.3)

★★ L4-proc 比 L1-L3 危险:它是可执行工作流,可能直通代码执行。所以三道门,缺一不可:
    ① 内容哈希      sha256(canonical(body))
    ② 签名          对 (name|version|sha256) 用本机批准密钥签,证明"是机主批准的"
    ③ 执行前哈希复核 执行时重算当前 sha256,与【最近批准的哈希】字节级比对,不等即拒

★ "L4 仅 trusted-local"在 DB 层无法强制(ai_mem_local / ai_mem_remote 两角色分不出
  trusted-local 与 trusted-lan)。所以门槛在【应用层】:本模块每个改动/执行函数都收
  caller: CallerTier,非 TRUSTED_LOCAL 一律拒。DB 层是第二道(远程角色对表零授权)。

★ 三种"哈希绑定批准"载荷不可合并(规格提取指出):
    · L4-proc  : hash = 内容哈希(本模块)
    · 凭证     : hash = H(ref‖sink),绝不对值取(§6.9.6)
    · 文件操作 : hash = 具体计划哈希(§12.4)
  本模块只管 L4-proc 这一种,不复用通用 plan_hash。

★ 批准密钥放 {state}/secrets(强 ACL + 排除出备份)。用 HMAC-SHA256 作签名 ——
  单机单人场景下"持有本机批准密钥"即等价于"机主批准"。密钥不进 10-core、不进备份、
  换机重新生成(与 CA 私钥同一套 secrets 纪律)。
"""
from __future__ import annotations

import hashlib
import hmac
import json
import tomllib
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional

import repo
from tainted import CallerTier

PATHS_TOML = Path(__file__).resolve().parents[2] / "config" / "paths.toml"


class L4Error(Exception):
    pass


class L4Denied(L4Error):
    """档位不足 —— 非 trusted-local 不得碰 L4。"""


class L4HashMismatch(L4Error):
    """执行前哈希复核失败 —— 内容与最近批准的不一致,拒绝执行。"""


def _require_local(caller: CallerTier) -> None:
    if not isinstance(caller, CallerTier):
        raise TypeError("caller 必须是 CallerTier 枚举")
    if caller is not CallerTier.TRUSTED_LOCAL:
        raise L4Denied(f"{caller.value} 不得读写/执行 L4 程序记忆(§4.4.3 仅 trusted-local)")


# ── canonical 化 + 哈希 ───────────────────────────────────────────
def canonical(body: Dict[str, Any]) -> str:
    """★ 规范序列化:排序键、无多余空白。哈希必须对【字节确定】的形态取,
    否则同内容不同序会算出不同哈希,把哈希复核变成运气。"""
    return json.dumps(body, sort_keys=True, ensure_ascii=False, separators=(",", ":"))


def content_sha256(body: Dict[str, Any]) -> str:
    return hashlib.sha256(canonical(body).encode("utf-8")).hexdigest()


# ── 批准密钥 ─────────────────────────────────────────────────────
def _secrets_dir() -> Path:
    with open(PATHS_TOML, "rb") as f:
        return Path(tomllib.load(f)["state"]["secrets"])


def _approval_key() -> bytes:
    """读(或首次生成)本机 L4 批准密钥。★ 在 {state}/secrets 下,不进备份。"""
    import secrets as _sec
    kf = _secrets_dir() / "l4_approval.key"
    if not kf.exists():
        kf.parent.mkdir(parents=True, exist_ok=True)
        kf.write_bytes(_sec.token_bytes(32))
    return kf.read_bytes()


def sign(name: str, version: str, sha256: str) -> str:
    """对 (name|version|sha256) 签名 —— 证明这个批准出自持有本机密钥的人。"""
    msg = f"{name}\x00{version}\x00{sha256}".encode("utf-8")
    return hmac.new(_approval_key(), msg, hashlib.sha256).hexdigest()


def verify_signature(name: str, version: str, sha256: str, signature: str) -> bool:
    expected = sign(name, version, sha256)
    return hmac.compare_digest(expected, signature)      # ★ 常数时间比较


# ── 数据形态 ─────────────────────────────────────────────────────
@dataclass
class Procedure:
    id: int
    name: str
    version: str
    git_ref: str
    sha256: str
    body: Dict[str, Any]
    signature_ref: Optional[str]
    last_approved_sha256: Optional[str]
    approved_at: Optional[str]

    @property
    def is_approved(self) -> bool:
        return self.last_approved_sha256 is not None


# ── 写路径:提议(未批准)──────────────────────────────────────────
def propose(conn, *, caller: CallerTier, name: str, version: str, git_ref: str,
            body: Dict[str, Any]) -> int:
    """提议一条 L4 过程。★ 提议 ≠ 批准:此时 last_approved_sha256 为空,executor 会拒执行。"""
    _require_local(caller)
    sha = content_sha256(body)
    try:
        with conn.cursor() as cur:
            cur.execute("""
                INSERT INTO mem.l4_procedure
                  (name, version, git_ref, sha256, body, sensitivity_domain)
                VALUES (%s,%s,%s,%s,%s,'S0') RETURNING id
            """, (name, version, git_ref, sha, repo.as_jsonb(body)))
            return cur.fetchone()[0]
    except Exception as e:
        raise L4Error(f"提议失败(可能 name+version 已存在): {type(e).__name__}") from None


# ── 批准:记录哈希 + 签名(哈希绑定)──────────────────────────────
def approve(conn, *, caller: CallerTier, procedure_id: int, approved_by: str) -> None:
    """批准一条 L4 过程:把它【当前内容】的哈希记为 last_approved_sha256,并签名。

    ★★ 批准绑定的是"你现在看到的这份内容"的哈希。批准之后但凡 body 变一个字节,
       sha256 就变,而 last_approved_sha256 还是旧的 → executor 复核失败 → 拒执行。
    """
    _require_local(caller)
    proc = get(conn, procedure_id)
    if proc is None:
        raise L4Error("过程不存在")
    sig = sign(proc.name, proc.version, proc.sha256)
    try:
        with conn.cursor() as cur:
            cur.execute("""
                UPDATE mem.l4_procedure
                   SET last_approved_sha256=%s, signature_ref=%s, signed_at=now(),
                       approved_at=now(), approved_by=%s
                 WHERE id=%s
            """, (proc.sha256, sig, approved_by, procedure_id))
            cur.execute("""
                INSERT INTO mem.l4_approval (procedure_id, approved_sha256, signature, approved_by)
                VALUES (%s,%s,%s,%s)
            """, (procedure_id, proc.sha256, sig, approved_by))
    except Exception as e:
        raise repo._sanitize(e) from None


# ── 读 ───────────────────────────────────────────────────────────
def get(conn, procedure_id: int) -> Optional[Procedure]:
    with conn.cursor() as cur:
        cur.execute("""
            SELECT id, name, version, git_ref, sha256, body, signature_ref,
                   last_approved_sha256, approved_at
              FROM mem.l4_procedure WHERE id=%s
        """, (procedure_id,))
        r = cur.fetchone()
    if r is None:
        return None
    return Procedure(id=r[0], name=r[1], version=r[2], git_ref=r[3], sha256=r[4],
                     body=r[5] or {}, signature_ref=r[6], last_approved_sha256=r[7],
                     approved_at=r[8].isoformat() if r[8] else None)


# ── executor:执行前哈希复核 ──────────────────────────────────────
# 步骤处理器白名单(§4.7.6「确定性白名单」的形):P3a 只挂无副作用的安全处理器,
# 真正的操作库(文件/浏览器/游戏)在 P6。executor 的价值在【安全性质】——
# 证明被篡改的过程会被拒执行,而不是它能干多少活。
_HANDLERS = {
    "noop": lambda arg: {"ok": True, "arg": arg},
    "echo": lambda arg: {"echo": arg},
}


def execute(conn, *, caller: CallerTier, procedure_id: int) -> List[Dict[str, Any]]:
    """执行一条已批准的 L4 过程。★★ 执行前三重复核,任一不过即拒:

      ① 已批准(last_approved_sha256 非空)
      ② 签名验证通过(对 name|version|批准哈希)
      ③ 当前 body 的哈希 == last_approved_sha256(字节级,无 trim/大小写/编码归一化)

    只有全过才逐步派发给白名单处理器。
    """
    _require_local(caller)
    proc = get(conn, procedure_id)
    if proc is None:
        raise L4Error("过程不存在")
    if not proc.is_approved:
        raise L4HashMismatch("过程未批准 —— 拒绝执行(提议 ≠ 批准)")

    # ② 签名
    if not (proc.signature_ref and verify_signature(
            proc.name, proc.version, proc.last_approved_sha256, proc.signature_ref)):
        raise L4HashMismatch("签名验证失败 —— 拒绝执行")

    # ③ ★★ 执行前哈希复核:重算当前内容哈希,与批准的字节级比对
    current = content_sha256(proc.body)
    if not hmac.compare_digest(current, proc.last_approved_sha256):
        raise L4HashMismatch(
            f"内容与最近批准的不一致 —— 拒绝执行(§4.4.3)。"
            f"批准时哈希前8位={proc.last_approved_sha256[:8]},当前={current[:8]}。"
            "过程被改动过就必须重新批准,不能凭旧批准执行新内容。")

    # 全过 → 逐步派发
    results = []
    for step in proc.body.get("steps", []):
        op = step.get("op")
        h = _HANDLERS.get(op)
        if h is None:
            raise L4Error(f"未知步骤 op={op!r} —— 不在白名单处理器内")
        results.append(h(step.get("arg")))
    return results
