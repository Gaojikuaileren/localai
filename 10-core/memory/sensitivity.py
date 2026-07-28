# -*- coding: utf-8 -*-
"""机密定级(域 S2)—— 与凭证检测是**两件事**,必须分开(§4.11.4 · §6.9.4)

★★ 本模块存在的理由:规格把两件事写成了「且」,于是 S2 成了空集。

  §6.9.4 要求正则族「一族两用」:命中 →(a)E3 拒绝写入 **且**(b)已写入的强制改 S2。
  §4.11.4 又把「**地址模式**」放进同一族正则。
  两条同时成立时的实际后果:
    · 凡能被正则判成 S2 的内容都在 E3 被拒 → 永远走不到落库那步
    · 若真按 §4.11.4 补上地址正则,E3 会连「我家在 X 街」都拒写 ——
      而 D23 明说**住址 / 健康 / 关系细节仍在记忆库**
  ⇒ 整套 S2 隔离(v_memory_nons2 · mem_s2 · 远程永久不可读)守的是一个**空集合**,
    而规格自己没有指定谁来填它。2026-07-28 规格提取实证。

★ 分叉:动作不同,判据就不能共用

  | | 判据 | 命中后 | 误报的代价 |
  |---|---|---|---|
  | **凭证**(e1_detector) | IBAN · 税号 · 卡号 · 证件号 ·「密码是」 | **拒绝写入,不落盘** | **贵** —— 挡住正常写入 |
  | **机密**(本模块)      | 地址 · 健康 · 关系细节 · 用户手动标记 | **照写,但强制标 S2** | **便宜** —— 只是处理更严 |

  ⇒ 两者的误报代价**方向相反**,因此必须分开调参:
    凭证检测要**保守**(宁可漏报,不能把正常写入打死);
    机密定级要**激进**(宁可多标 S2,漏标才是真损失 —— 漏标意味着住址进了
    v_memory_nons2、进了 mem_main、将来会经外联通道出门)。
    把它们塞进同一份正则,必然有一方被另一方的调参需求拖坏。

★ 本模块**永不拒绝写入**。它只回答一个问题:这条内容该不该标 S2。
"""
from __future__ import annotations

import re
from typing import Set, Tuple

# ── 机密判据 ──────────────────────────────────────────────────────
# ★ 调参方向:宁可多标。漏标的后果是住址出现在远程可读视图里;
#   多标的后果只是它被关进 mem_s2 且远程读不到 —— 而那本来就是用户的东西,
#   本机面板照样能看。两种错误的代价不对称,所以偏向多标。

ADDRESS = "address"
HEALTH = "health"
KINSHIP_DETAIL = "kinship_detail"
MANUAL = "manual"          # 用户在面板上手动标记 —— 没有正则,由动作产生

ALL_CLASSES: Tuple[str, ...] = (ADDRESS, HEALTH, KINSHIP_DETAIL, MANUAL)

_PATTERNS = {
    ADDRESS: [
        # 德国:街道名 + 门牌号(Straße/Str./Weg/Platz/Allee/Gasse/Ring/Damm/Ufer)
        re.compile(r"[A-ZÄÖÜ][\wäöüß\-]{2,}\s?(?:stra(?:ß|ss)e|str\.|weg|platz|allee|"
                   r"gasse|ring|damm|ufer)\s+\d{1,4}\s*[a-z]?", re.IGNORECASE),
        # 德国邮编 + 城市(5 位数字后跟大写开头的词)
        re.compile(r"\b\d{5}\s+[A-ZÄÖÜ][\wäöüß\-]{2,}"),
        # 中文:…路/街/巷/号/室/楼/单元 + 数字
        re.compile(r"[一-龥]{2,}(?:路|街|巷|大道|胡同)\s*\d{1,4}\s*(?:号|號)?"),
        re.compile(r"\d{1,4}\s*(?:号|號)\s*(?:楼|樓|单元|單元|室)"),
    ],
    HEALTH: [
        re.compile(r"(?:确诊|診斷|诊断|病历|病歷|处方|處方|服用|药量|藥量|过敏|過敏)"),
        re.compile(r"\b(?:diagnos|prescription|medication|allerg)\w*", re.IGNORECASE),
    ],
    KINSHIP_DETAIL: [
        # 亲属 + 出生/生日/身份信息 —— 单纯"我妹妹叫小雨"不算(那是 S0 的关系事实)
        re.compile(r"(?:父亲|母亲|爸爸|妈妈|妹妹|姐姐|哥哥|弟弟|儿子|女儿|配偶|伴侣)"
                   r"[^。;;\n]{0,12}(?:生日|出生|身份证|護照|护照|保险号|社保)"),
    ],
}


def scan(text: str) -> Set[str]:
    """返回命中的机密类别。**不拒绝任何东西** —— 只回答"该不该标 S2"。"""
    if not text:
        return set()
    return {name for name, pats in _PATTERNS.items() if any(p.search(text) for p in pats)}


def is_confidential(text: str) -> bool:
    return bool(scan(text))


def classify(text: str) -> Tuple[str, Set[str]]:
    """(sensitivity_domain, 命中类别)。命中机密 → S2;否则 S0。

    ★ 不产生 S1:P3b 之前 S0/S1 不区分(§4.11.4「两级不是三级」),
      三级分域要等一个月真实读取日志(Backlog B9)。写死成 S0 而不是随手分个 S1,
      是为了让"什么时候开始区分"成为一个显式决定而不是既成事实。
    """
    hits = scan(text)
    return ("S2" if hits else "S0"), hits
