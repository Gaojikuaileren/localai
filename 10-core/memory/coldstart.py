# -*- coding: utf-8 -*-
"""冷启动初始化会话(§4.9.1)—— 只跑一次,给记忆库播下第一批种子

★ 本模块是【驱动逻辑】,不是界面。D36 把 UI 移到 P3c 客户端;S6 本期交付的是
  "给一批用户已确认的种子事实,把它们正确种进库"的契约,交互界面在 P3c 补。

三个裁定(2026-07-28,规格未指定处):

  ① 置信度 = 1.0(不是 0.6)。§4.9.1「当场展示、允许你改」= 逐条面板确认,
     语义上就是面板票据。种子是用户亲口确认的,不是助手从对话流推断的。
     ⇒ 每条种子经 Gate + 一次性票据铸 1.0/panel_ticket。

  ② 只跑一次 = fail-closed。用 mem.system_state 的持久标记(重启不复位);
     已初始化则拒绝重跑,不静默复制并列行(§4.5)。

  ③ 归一化对齐。种子的 subject_norm/predicate_norm 用 route 的**同一套**
     normalize + apply_fold 计算 —— 否则「妹妹」写进去是「妹妹」,而查询
     「二妹叫什么」折叠后找的也是「妹妹」,两侧必须落在同一个键上才能命中。

★ S2 自动分级:种子经 Gate 的 classify_sensitivity,地址/健康/亲属细节自动标 S2
  并去 mem_s2 —— 冷启动正是最需要"手机读不到住址"的场合。
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import List, Optional

import gate
import repo
import route

COLD_START_KEY = "cold_start_completed"


class ColdStartError(Exception):
    pass


@dataclass
class Seed:
    """一条用户已确认的初始事实。

    subject / predicate 传【原始】词(如「妹妹」「名字」),本模块负责归一化对齐 ——
    调用方不需要知道 normalize/fold 的细节。
    """
    subject: str
    predicate: str
    statement: str          # 完整陈述句(会被机密定级扫描)
    object_text: str        # 答案值(「小雨」)


def is_initialized(conn) -> bool:
    return repo.get_system_state(conn, COLD_START_KEY) is not None


def _norm(text: str, lex: route.Lexicon) -> str:
    """★ 与 route.route 的查询侧【同一套】归一化 + 折叠 —— 读写落同一键的唯一保证。"""
    return lex.apply_fold(route.normalize(text))


def run_cold_start(conn, seeds: List[Seed], *, session_id: str = "cold-start",
                   lex: Optional[route.Lexicon] = None) -> List[int]:
    """播种。返回写入的 fact_id 列表。

    ★★ 幂等 fail-closed:已初始化 → 抛 ColdStartError,不写任何东西、不静默跳过。
       "跑第二次会怎样"的答案必须是"响亮拒绝",而不是"再种一遍"。
    """
    if is_initialized(conn):
        raise ColdStartError(
            "冷启动已完成过 —— 拒绝重跑(否则会静默复制并列行,§4.5)。"
            "要改已有种子请走记忆面板的编辑/删除,不是重新初始化。")
    if not seeds:
        raise ColdStartError("没有种子 —— 冷启动至少要播一条,否则标记为已完成毫无意义")

    lex = lex or route.Lexicon.load()
    written: List[int] = []
    for s in seeds:
        subj = _norm(s.subject, lex)
        pred = _norm(s.predicate, lex)
        if not subj or not pred:
            raise ColdStartError(f"种子归一化后为空: subject={s.subject!r} predicate={s.predicate!r}")

        # ★ 逐条经 Gate + 票据铸 1.0(§4.9.1 当场确认)。凭证会被拦、机密会被标 S2。
        cand = gate.CandidateIn(body=s.statement, provenance="user_typed", session_id=session_id)
        tk = repo.issue_ticket(conn, session_id=session_id, candidate_text=s.statement)
        res = gate.submit(conn, candidate=cand, subject_norm=subj, predicate_norm=pred,
                          object_text=s.object_text, ticket_id=tk)
        if not isinstance(res, gate.GateResult):
            # 派生来源不该出现在冷启动种子里;若走了队列说明调用方传错了 provenance
            raise ColdStartError(f"种子未直接写入(意外走队列):{s.subject}/{s.predicate}")
        written.append(res.fact_id)

    # ★ 全部种子写完后才置标记 —— 原子性:要么整批种下+标记,要么一条都不留。
    #   若中途失败,标记未置,事务回滚,下次仍可重跑(fail-closed 的正确方向)。
    repo.set_system_state_once(conn, COLD_START_KEY,
                               {"seed_count": len(written), "session_id": session_id})
    return written
