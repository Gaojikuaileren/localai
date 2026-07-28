"""双轨检索的选路器(§4.2)—— **纯函数,不碰数据库、不碰网络、不问 LLM**

为什么是纯函数:选路结果必须**可判定、可测试、可回归**。若词表从库里加载(比如「已知实体名」),
选路结果就会随数据变化 —— eval 用例会被后写入的一行数据悄悄作废,100% 闸变成 flaky,
而且失败时间点与代码改动无关。(2026-07-28 对抗性核验实测指出。)

★ 关键分工:**选路只决定「走哪轨、谁能填 answer」,不负责认实体。**
  「我妹妹」到底指哪个人,是结构化轨自己的事。选路不需要知道库里有谁。

★★ 两条从核验里学到的硬教训:

1. **默认扇出,不默认单轨。** 原设计的公式在「疑问词不在表内」时会落到单轨向量 ——
   而那恰恰是最需要两轨都跑的情形(外语、生僻口语、ASR 误码全属此类)。
   本实现:除非有**正面强信号**,一律 BOTH。走错轨的代价从「静默答错」降为「多花几十毫秒」。

2. **只有结构化轨能填 answer。** 向量轨永远只返回片段(passages),永不产出 answer。
   否则「一条 rerank 片段」就能冒充确切答案 —— 这是静默答错的正门。
   「我妹妹叫什么名字」若结构化轨零命中,正确行为是**说不知道**,不是拿相关片段顶上。
"""
from __future__ import annotations

import tomllib
import unicodedata
from dataclasses import dataclass
from enum import Enum
from pathlib import Path
from typing import Dict, List, Optional, Tuple

LEXICON_PATH = Path(__file__).resolve().parents[2] / "config" / "retrieval-lexicon.toml"


class Route(str, Enum):
    """封闭枚举 —— 没有「其他 / 自动 / 由模型决定」这种没法验收的档。

    ★ 两个档都会**跑两轨**,区别只在:谁领跑、谁能填 answer。
      不设 *_ONLY:单轨兜底正是核验指出的静默答错来源。
    """
    STRUCT_FIRST = "STRUCT_FIRST"   # 结构化轨领跑且【只有它】能填 answer
    VECTOR_FIRST = "VECTOR_FIRST"   # 向量轨领跑;answer 仍只能由结构化轨填
    BOTH         = "BOTH"           # 无强信号 —— 扇出,answer 仍只能由结构化轨填


@dataclass(frozen=True)
class Decision:
    route: Route
    rule_id: str
    signals: Dict[str, Optional[str]]   # 命中的原文片段(便于溯源与调试)

    @property
    def answer_allowed_from(self) -> str:
        """谁被允许填 answer —— 恒为结构化轨,与路由无关。写成属性是为了让这条不可配置。"""
        return "struct"


# ── 归一化(★ 与 E1 的 normalize 【刻意不共用】,原因见词表文件头)──────────
def normalize(text: str) -> str:
    """全角→半角 · 小写 · 压空白。

    ★ **不做**中文数字→阿拉伯:那是 E1 为还原被念成「一二三」的卡号设计的,
      套到称谓上会把 二姐→2姐 · 三妹→3妹 · 老三→老3 —— 把最常见的一族排行称谓
      从词表里永久打掉,而且 fold 救不回来(fold 在归一化之后)。
    """
    if not text:
        return ""
    out = []
    for ch in text:
        o = ord(ch)
        if 0xFF01 <= o <= 0xFF5E:          # 全角 ASCII → 半角
            out.append(chr(o - 0xFEE0))
        elif o == 0x3000:                   # 全角空格
            out.append(" ")
        else:
            out.append(ch)
    s = unicodedata.normalize("NFKC", "".join(out)).lower()
    return " ".join(s.split())


@dataclass
class Lexicon:
    relation: List[str]
    attribute_q: List[str]
    episodic: List[str]
    fold: Dict[str, str]

    @staticmethod
    def load(path: Path = LEXICON_PATH) -> "Lexicon":
        with open(path, "rb") as f:
            raw = tomllib.load(f)
        def flat(name: str) -> List[str]:
            g = raw["signals"][name]
            terms = [normalize(t) for lang in ("zh", "de", "en") for t in g.get(lang, [])]
            # ★ 长片段在前 —— 「叫什么名字」必须先于「叫什么」命中,否则溯源片段是错的
            return sorted({t for t in terms if t}, key=len, reverse=True)
        return Lexicon(
            relation=flat("relation"),
            attribute_q=flat("attribute_q"),
            episodic=flat("episodic"),
            fold={normalize(k): normalize(v) for k, v in raw.get("fold", {}).items()},
        )

    def apply_fold(self, s: str) -> str:
        for k in sorted(self.fold, key=len, reverse=True):
            s = s.replace(k, self.fold[k])
        return s


_LEX: Optional[Lexicon] = None


def lexicon() -> Lexicon:
    global _LEX
    if _LEX is None:
        _LEX = Lexicon.load()
    return _LEX


def _first_hit(text: str, terms: List[str]) -> Optional[str]:
    """★ 子串匹配,不是集合成员。terms 已按长度降序 ⇒ 天然最长匹配。

    集合成员判定是原设计的纸面失败点:「叫什么名字」不等于表里的「叫什么」或「什么名字」,
    于是验收例句在还没跑起来之前就注定走错轨。
    """
    for t in terms:
        if t and t in text:
            return t
    return None


def route(question: str, lex: Optional[Lexicon] = None) -> Decision:
    """问题 → (走哪轨, 规则号)。纯函数:同样的输入永远给同样的输出。"""
    lex = lex or lexicon()
    q = lex.apply_fold(normalize(question))

    rel = _first_hit(q, lex.relation)
    att = _first_hit(q, lex.attribute_q)
    epi = _first_hit(q, lex.episodic)
    sig = {"relation": rel, "attribute_q": att, "episodic": epi}

    # R-STRUCT-01:问某人/某物的一个【确切值】—— 向量轨给不出确切值,必须结构化轨主答。
    #   「我妹妹叫什么名字」「Wie heißt meine Schwester」「what's my sister's name」
    #   ★ 即便同时有情节词(「上次你说我妹妹叫什么来着」),仍优先结构化 ——
    #     用户要的是那个名字,不是那次对话。
    if rel and att:
        return Decision(Route.STRUCT_FIRST, "R-STRUCT-01", sig)

    # R-VECTOR-01:明确指向过去某次对话/事件,且不是在问确切属性值。
    #   「上次聊的那个灯光问题」
    if epi and not att:
        return Decision(Route.VECTOR_FIRST, "R-VECTOR-01", sig)

    # R-STRUCT-02:有属性疑问但没有关系词(「生日是几号」「wo wohnt sie」)——
    #   仍是要确切值,结构化轨领跑,但信号弱于 01。
    if att:
        return Decision(Route.STRUCT_FIRST, "R-STRUCT-02", sig)

    # R-DEFAULT-00:无强信号 → 扇出。
    #   ★ 这里【不能】退化成单轨向量:外语、生僻口语、ASR 误码都会落到这儿,
    #     而那正是最需要两轨都跑的情形。
    return Decision(Route.BOTH, "R-DEFAULT-00", sig)


if __name__ == "__main__":
    import sys
    for q in (sys.argv[1:] or [
        "我妹妹叫什么名字", "上次聊的那个灯光问题",
        "Wie heißt meine Schwester", "what's my sister's name",
        "我二姐的生日是几号", "帮我总结一下今天",
    ]):
        d = route(q)
        hits = " ".join(f"{k}={v!r}" for k, v in d.signals.items() if v)
        print(f"  {d.route.value:13} {d.rule_id:14} {q}\n{'':32}{hits}")
