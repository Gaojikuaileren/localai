r"""P4-S13 · 内网同步的存储层(D86)。

家庭待办 + 共享会话存在中枢,两台机器经已有的 mTLS 通道推拉。

★★★ 三条硬规矩,都是 D86 裁定的直接落点:

  ① **只收「家庭/共享」的。** 个人待办、普通会话**根本不该到这儿来** ——
     真到了也拒收,不是"顺手存了吧"。范围判据放在服务端,是因为客户端可能有 bug,
     而"把私人东西推到另一台机器上"这种错误**不可撤销**。

  ② **后到的赢,但被覆盖的那一版【存起来】。** 以中枢收到的顺序为准 ——
     中枢是单一权威,不靠两台机器的钟对表(钟本来就不准)。
     ★★ 静默丢 = 用户在副机上写的备注凭空消失,而他永远不会知道。
        与「失败必须长得和成功不一样」同一条纪律:**覆盖也是一种失败,得看得见**。

  ③ **原子写。** 写一半断电 = 共享数据损坏,而它现在是两台机器的唯一权威。
     先写临时文件再 os.replace(同一分区上是原子的)。

★ 落在 `{state}/shared/` —— `90-ops/backup/backup.ps1` 备的正是 **paths.toml 里的 `state` 根**,
  于是同步过的那部分**自动进备份**,顺带关掉 D57 末尾那条「todos.json 不进任何备份」。
  ★ 只关掉一半:个人待办不同步,所以仍然不进备份。不得声称全解决。
  ★ 这段注释里**不写具体盘符** —— §11.1 的 pre-commit 钩子连注释一起查,而它是对的:
    注释里的路径同样会过期,而且过期得更不容易被发现(代码改了、注释没跟着改)。
"""
from __future__ import annotations

import json
import os
import re
import time
import uuid
from pathlib import Path
from typing import Dict, List, Optional, Tuple

#: 允许同步的集合。★ 反向全表:客户端推一个表外的 kind → 拒收,不是默默建个新文件。
KINDS: Tuple[str, ...] = ("todos", "sessions", "messages")

#: 每个集合里,**必须**满足才收的范围判据(D86 裁定①)。
#: ★ 判据放服务端:客户端可能有 bug,而"把私人东西推到另一台机器"不可撤销。
SCOPE_RULES: Dict[str, str] = {
    "todos": "只收 scope == '家庭' 的待办;个人待办不同步(D52「默认只在本机」)",
    "sessions": "只收 shared == True 的会话;普通会话不同步",
    "messages": "只收属于已共享会话的消息(session_id 必须在 sessions 里且 shared)",
}


def _state_root() -> Path:
    """从 config/paths.toml 读 state 根。★ §11.1 路径契约:代码里不写绝对路径。"""
    here = Path(__file__).resolve()
    for p in [here.parents[i] for i in range(2, 6)]:
        toml = p / "config" / "paths.toml"
        if toml.exists():
            m = re.search(r"^\s*state\s*=\s*'([^']+)'", toml.read_text(encoding="utf-8"), re.M)
            if m:
                return Path(m.group(1))
    raise RuntimeError("paths.toml 里找不到 state —— 拒绝猜一个路径(§11.1)")


class SyncStore:
    """共享数据的唯一权威。★ 单写者:所有写都经 put(),世代号在同一次调用里 +1。"""

    def __init__(self, root: Optional[Path] = None):
        self._root = Path(root) if root else (_state_root() / "shared")
        self._root.mkdir(parents=True, exist_ok=True)
        (self._root / "superseded").mkdir(exist_ok=True)
        self._gen = 0
        self._cache: Dict[str, Dict[str, dict]] = {}
        for k in KINDS:
            self._cache[k] = self._load(k)
        # 世代号从已有数据的最大 rev 起步 —— 重启后不该退回 0 让客户端以为全变了
        self._gen = max([0] + [int(r.get("rev", 0)) for k in KINDS for r in self._cache[k].values()])

    # ── 落盘 ────────────────────────────────────────────────────────
    def _path(self, kind: str) -> Path:
        return self._root / f"{kind}.json"

    def _load(self, kind: str) -> Dict[str, dict]:
        p = self._path(kind)
        if not p.exists():
            return {}
        try:
            return {r["id"]: r for r in json.loads(p.read_text(encoding="utf-8"))}
        except Exception:                                    # noqa: BLE001
            # ★ 坏档**不当成空表**:当成空会让下一次推送把它整个覆盖掉,
            #   等于一次解析失败吃掉全部共享数据。改名留证,并**拒绝服务**。
            bad = p.with_suffix(f".corrupt-{int(time.time())}.json")
            try:
                p.rename(bad)
            except Exception:                                # noqa: BLE001
                pass
            raise RuntimeError(f"{kind}.json 解析失败,已改名留证:{bad.name}。拒绝在坏档上继续写")

    def _save(self, kind: str) -> None:
        """★ 原子写:先写临时文件再 replace。写一半断电 = 共享数据损坏,
        而它现在是两台机器的唯一权威。"""
        p = self._path(kind)
        tmp = p.with_suffix(f".tmp-{uuid.uuid4().hex[:8]}")
        tmp.write_text(json.dumps(list(self._cache[kind].values()), ensure_ascii=False, indent=1),
                       encoding="utf-8")
        os.replace(tmp, p)

    # ── 范围判据(D86 裁定①)────────────────────────────────────────
    @staticmethod
    def in_scope(kind: str, rec: dict, known_sessions: Optional[Dict[str, dict]] = None,
                 existing: Optional[Dict[str, dict]] = None) -> Tuple[bool, str]:
        """这条该不该收。★ 返回 (收不收, 为什么) —— 拒收要说得出理由。"""
        # ══════════════════════════════════════════════════════════════
        #  ★★★ 删除 = 墓碑(2026-08-05 用户实测「删除时还是没法同步删除」)。
        #
        #  没有删除语义时,「连上就对齐」会把对方删掉的东西**推回去**:
        #  A 删了 → B 开机不知情 → B 把本地那份又推上来 → A 那边复活。
        #  所以删除必须是**一条会传播的记录**,不能是"把行去掉"。
        #
        #  ★ 判据:**只能删已经共享过的**。库里没有这个 id 就拒 ——
        #    否则一条伪造的墓碑能凭空在别人机器上删东西,而且它不需要带 scope,
        #    也就绕过了范围闸。fail-closed:认不出来源的删除一律不收。
        # ══════════════════════════════════════════════════════════════
        if rec.get("deleted"):
            rid = str(rec.get("id") or "")
            if rid and (existing or {}).get(rid) is not None:
                return (True, "")
            return (False, f"删除的记录 {rid[:12]}… 不在共享库里 —— 只能删已经共享过的")
        if kind == "todos":
            sc = str(rec.get("scope") or "")
            return (sc == "家庭", "" if sc == "家庭" else f"待办范围是「{sc or '未标'}」,不是家庭 —— 个人待办不同步(D52)")
        if kind == "sessions":
            return (bool(rec.get("shared")), "" if rec.get("shared") else "会话未提升为共享 —— 普通会话不同步(D52)")
        if kind == "messages":
            sid = str(rec.get("session_id") or "")
            s = (known_sessions or {}).get(sid)
            if s is None:
                return (False, f"消息所属会话 {sid[:8]}… 不在共享会话里 —— 不收")
            # ★ 会话已被删(墓碑)⇒ 它的消息也不再收。否则删掉一个共享会话之后,
            #   另一台的对齐还会把整段消息推回来,而会话本身已经没了 —— 变成孤儿消息。
            if s.get("deleted"):
                return (False, f"所属会话 {sid[:8]}… 已删除 —— 不收")
            return (bool(s.get("shared")), "" if s.get("shared") else "所属会话未共享 —— 不收")
        return (False, f"未登记的集合 {kind}")

    # ── 读 ──────────────────────────────────────────────────────────
    def snapshot(self, since_rev: int = 0) -> dict:
        """全量或增量。★ since_rev=0 即全量;客户端重连后拿它对齐。"""
        out = {}
        for k in KINDS:
            out[k] = [r for r in self._cache[k].values() if int(r.get("rev", 0)) > since_rev]
        return {"generation": self._gen, "since_rev": since_rev, "data": out,
                "counts": {k: len(self._cache[k]) for k in KINDS}}

    # ── 写 ──────────────────────────────────────────────────────────
    def put(self, kind: str, rec: dict, device: str) -> dict:
        """收一条。返回 {ok, rev, superseded?} —— 被覆盖时如实回报。

        ★★ D86 裁定③:后到的赢,**但被覆盖的那一版存起来**。
           以中枢收到的顺序为准 —— 不靠两台机器的钟对表。
        """
        if kind not in KINDS:
            return {"ok": False, "code": "unknown_kind", "message": f"未登记的集合 {kind}"}
        rid = str(rec.get("id") or "")
        if not rid:
            return {"ok": False, "code": "missing_id", "message": "记录缺 id"}
        ok, why = self.in_scope(kind, rec, self._cache.get("sessions"), self._cache.get(kind))
        if not ok:
            # ★ 拒收不是错误,是**按设计**。但必须说清为什么,否则客户端只会看到"没同步"。
            return {"ok": False, "code": "out_of_scope", "message": why}

        prev = self._cache[kind].get(rid)
        self._gen += 1
        rec = dict(rec)
        rec["rev"] = self._gen
        rec["synced_at"] = time.time()
        rec["device"] = device
        self._cache[kind][rid] = rec

        superseded = None
        if prev is not None and _content_of(prev) != _content_of(rec):
            # ★★ 被覆盖的那一版**存起来**。静默丢 = 用户在副机上写的备注凭空消失,
            #    而他永远不会知道。覆盖也是一种"失败",得看得见。
            superseded = {"kind": kind, "id": rid, "at": time.time(),
                          "overwritten_by": device, "was_from": prev.get("device", "?"),
                          "record": prev}
            f = self._root / "superseded" / f"{kind}-{rid[:12]}-{self._gen}.json"
            f.write_text(json.dumps(superseded, ensure_ascii=False, indent=1), encoding="utf-8")
        self._save(kind)
        return {"ok": True, "rev": self._gen, "superseded": superseded is not None,
                "superseded_from": (prev or {}).get("device") if superseded else None}

    def superseded_for(self, kind: str, rid: str) -> List[dict]:
        """某条记录被覆盖过的历史。★ 界面据此提示「这条被另一台改过」。"""
        out = []
        for f in sorted((self._root / "superseded").glob(f"{kind}-{rid[:12]}-*.json")):
            try:
                out.append(json.loads(f.read_text(encoding="utf-8")))
            except Exception:                                # noqa: BLE001
                continue
        return out


def _content_of(rec: dict) -> dict:
    """比内容时**忽略同步元数据** —— 否则每次推送都会因为 rev/synced_at 变了
    而被判成"内容改了",于是每一次同步都留一份"被覆盖"记录,噪声淹掉真冲突。"""
    return {k: v for k, v in rec.items() if k not in ("rev", "synced_at", "device")}


#: 进程内单例 —— 与 Broker 同款「单一权威」。
STORE: Optional[SyncStore] = None


def store() -> SyncStore:
    global STORE
    if STORE is None:
        STORE = SyncStore()
    return STORE
