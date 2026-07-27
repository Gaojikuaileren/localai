"""E1 · 入口凭证检测器  (§6.9.0 / §6.9.4 · P2「必做且先行」)

在网关入口(用户输入 → 组装 prompt 之【前】)扫描。命中即拦下本轮:
**不发送、不落 L0、不记正文**。只把命中的【类别】交给审计(§6.9.8:
category + time + session_id,**绝不记 body / 片段 / 哈希** —— 定长凭证的哈希可爆破)。

★ 诚实边界(§4.6.2):E1 能拦住【意外】,拦不住【坚持】。
  它把「手滑把密码/账号贴进来」变成「必须显式点『这不是凭证,继续』」。
  它**不是**「记忆零外发」的证明,也拦不住手动复制粘贴。文档不得把它说成保证。

★ 不信任前端:Open WebUI 是第三方前端,E1 在网关侧做,不依赖前端做任何过滤。

类别 = mem.cred_pattern_class 枚举:
  iban · tax_id_de · card_pan · id_doc · secret_phrase · high_entropy
其中 **high_entropy 误报率高 → 只用于 E1/E4,不用于 E3 拒绝**(§6.9.4)。

设计要点:
- **归一化前置**(§6.9.4,否则语音通道命中率≈0):全角→半角 · 中文数字→阿拉伯 ·
  结构化凭证匹配时去分隔符(空格/-/./)。ASR 输出也过同一步。
- **带校验和的类别用校验和**(IBAN mod-97 / 卡号 Luhn / 德国税号 ISO 7064 MOD 11,10):
  噪声检测器会训练用户「一律点继续」,反而废掉 E1。校验和把误报压到很低。
- 检测器**只返回类别**,不返回、不保留凭证值或片段。
"""
from __future__ import annotations

import re
from collections import Counter
from dataclasses import dataclass, field
from math import log2
from typing import List, Set

# ── 类别(对齐 mem.cred_pattern_class)──────────────────────────────
IBAN = "iban"
TAX_ID_DE = "tax_id_de"
CARD_PAN = "card_pan"
ID_DOC = "id_doc"
SECRET_PHRASE = "secret_phrase"
HIGH_ENTROPY = "high_entropy"

ALL_CATEGORIES = (IBAN, TAX_ID_DE, CARD_PAN, ID_DOC, SECRET_PHRASE, HIGH_ENTROPY)
# E3(Memory Gate 拒绝写入)不可用 high_entropy —— 误报率太高(§6.9.4)
E3_CATEGORIES = tuple(c for c in ALL_CATEGORIES if c != HIGH_ENTROPY)


# ── 归一化 ────────────────────────────────────────────────────────
_CN_NUM = {
    "〇": "0", "零": "0", "○": "0", "一": "1", "二": "2", "两": "2", "三": "3",
    "四": "4", "五": "5", "六": "6", "七": "7", "八": "8", "九": "9",
}


def normalize(text: str) -> str:
    """全角→半角 · 中文数字→阿拉伯。不去分隔符(结构化匹配时才去,见各检测器)。"""
    out = []
    for ch in text:
        o = ord(ch)
        if 0xFF10 <= o <= 0xFF19:        # 全角数字
            out.append(chr(o - 0xFF10 + 0x30))
        elif 0xFF21 <= o <= 0xFF3A:      # 全角 A-Z
            out.append(chr(o - 0xFF21 + 0x41))
        elif 0xFF41 <= o <= 0xFF5A:      # 全角 a-z
            out.append(chr(o - 0xFF41 + 0x61))
        else:
            out.append(_CN_NUM.get(ch, ch))
    return "".join(out)


def _strip_sep(s: str) -> str:
    return re.sub(r"[ \-./]", "", s)


# ── 校验和 ────────────────────────────────────────────────────────
def iban_valid(iban: str) -> bool:
    """IBAN mod-97 == 1(ISO 13616)。"""
    iban = iban.upper()
    if not re.fullmatch(r"[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}", iban):
        return False
    rearranged = iban[4:] + iban[:4]
    digits = "".join(str(ord(c) - 55) if c.isalpha() else c for c in rearranged)
    try:
        return int(digits) % 97 == 1
    except ValueError:
        return False


def luhn_valid(num: str) -> bool:
    """信用卡/借记卡 Luhn 校验。"""
    if not num.isdigit():
        return False
    total = 0
    for i, ch in enumerate(reversed(num)):
        d = ord(ch) - 48
        if i % 2 == 1:
            d *= 2
            if d > 9:
                d -= 9
        total += d
    return total % 10 == 0


def steuer_id_valid(num: str) -> bool:
    """德国税号 Steuer-IdNr:11 位,ISO 7064 MOD 11,10 校验位。首位非 0。
    随机 11 位数字通过率约 1/10 → 配合「独立成串」边界,误报很低。"""
    if len(num) != 11 or not num.isdigit() or num[0] == "0":
        return False
    product = 10
    for ch in num[:10]:
        s = (int(ch) + product) % 10
        if s == 0:
            s = 10
        product = (s * 2) % 11
    check = (11 - product) % 10
    return check == int(num[10])


# ── 正则 ──────────────────────────────────────────────────────────
# 结构化凭证:允许内部空格/短横,匹配后去分隔符再校验
_IBAN_RE = re.compile(r"[A-Za-z]{2}[0-9]{2}(?:[ \-]?[A-Za-z0-9]){11,30}")
_CARD_RE = re.compile(r"(?<![0-9])[0-9](?:[ \-]?[0-9]){12,18}(?![0-9])")
_TAX_RE = re.compile(r"(?<![0-9])[0-9](?:[ \-/]?[0-9]){10}(?![0-9])")

# 德国证件号(最保守的一类,原文未给字段级校验):
#   护照 Reisepass:首位取自护照字母表(不含 A B D E I O Q S U)+ 8 位数字
#   身份证 Personalausweis 序列:类似字母表 + 数字混合(此处用护照式近似,标注为近似)
_PASSPORT_ALPHABET = "CFGHJKLMNPRTVWXYZ"
_ID_DOC_RE = re.compile(
    r"\b[" + _PASSPORT_ALPHABET + r"][0-9]{8}\b"       # 护照:字母 + 8 数字
    r"|\b[" + _PASSPORT_ALPHABET + r"][0-9A-Z]{8}[0-9]\b"  # 身份证序列:近似
)

# secret_phrase:两组触发短语,判定策略不同。
# 严格组(密码类):后面必须跟【疑似秘密 token】,避开「password is important」这类普通句。
_SECRET_TRIGGER_STRICT = re.compile(
    r"密码\s*(?:是|为|[:：])"
    r"|口令\s*(?:是|[:：])"
    r"|password\s*(?:is|[:=])"
    r"|passphrase|passwd",
    re.IGNORECASE,
)
# 种子/密钥组:值可能是【词表】(BIP39 助记词是纯小写单词,无数字符号)或十六进制。
_SECRET_TRIGGER_SEED = re.compile(
    r"助记词|私钥|恢复密钥|恢复短语|种子短语"
    r"|seed\s*phrase|mnemonic|private\s*key|recovery\s*key",
    re.IGNORECASE,
)
# 触发后窗口内的疑似秘密:6+ 位,且(含数字 或 含符号 或 长度≥12)
_SECRET_TAIL_RE = re.compile(r"[A-Za-z0-9!@#$%^&*_+=./\\-]{6,}")
# 助记词特征:≥4 个连续的小写单词(每个 3-9 字母,BIP39 词长范围)
_WORDLIST_RE = re.compile(r"(?:[a-z]{3,9}\s+){3,}[a-z]{3,9}")


def _secret_hit(norm: str) -> bool:
    for m in _SECRET_TRIGGER_STRICT.finditer(norm):
        tm = _SECRET_TAIL_RE.search(norm[m.end(): m.end() + 48])
        if tm and _looks_secret(tm.group()):
            return True
    for m in _SECRET_TRIGGER_SEED.finditer(norm):
        tail = norm[m.end(): m.end() + 80]
        tm = _SECRET_TAIL_RE.search(tail)
        if tm and _looks_secret(tm.group()):
            return True
        if _WORDLIST_RE.search(tail):       # 词表式助记词
            return True
    return False

# high_entropy:32+ 连续 token,Shannon 熵 ≥3.5
_TOKEN_RE = re.compile(r"[A-Za-z0-9+/=_\-]{32,}")
_ENTROPY_THRESHOLD = 3.5


def _shannon(s: str) -> float:
    n = len(s)
    if n == 0:
        return 0.0
    counts = Counter(s)
    return -sum((c / n) * log2(c / n) for c in counts.values())


def _looks_secret(tok: str) -> bool:
    if len(tok) < 6:
        return False
    has_digit = any(c.isdigit() for c in tok)
    has_symbol = any(not c.isalnum() for c in tok)
    return has_digit or has_symbol or len(tok) >= 12


# ── 主检测 ────────────────────────────────────────────────────────
@dataclass
class E1Result:
    categories: Set[str] = field(default_factory=set)

    @property
    def blocked(self) -> bool:
        return len(self.categories) > 0

    def for_e3(self) -> Set[str]:
        """Memory Gate(E3)用:剔除 high_entropy(§6.9.4)。"""
        return self.categories & set(E3_CATEGORIES)


def scan(text: str) -> E1Result:
    """扫描文本,返回命中的凭证类别集合。**不返回、不保留任何凭证值/片段。**"""
    if not text:
        return E1Result()
    norm = normalize(text)
    hits: Set[str] = set()

    for m in _IBAN_RE.finditer(norm):
        if iban_valid(_strip_sep(m.group())):
            hits.add(IBAN)

    for m in _CARD_RE.finditer(norm):
        cand = _strip_sep(m.group())
        if 13 <= len(cand) <= 19 and luhn_valid(cand):
            hits.add(CARD_PAN)

    for m in _TAX_RE.finditer(norm):
        if steuer_id_valid(_strip_sep(m.group())):
            hits.add(TAX_ID_DE)

    for m in _ID_DOC_RE.finditer(norm):
        # 至少含一位数字,排除纯字母词误报
        if any(c.isdigit() for c in m.group()):
            hits.add(ID_DOC)

    if _secret_hit(norm):
        hits.add(SECRET_PHRASE)

    for m in _TOKEN_RE.finditer(norm):
        if _shannon(m.group()) >= _ENTROPY_THRESHOLD:
            hits.add(HIGH_ENTROPY)
            break

    return E1Result(categories=hits)


# 面向用户的拦截文案(不回显任何值)——§6.9.4:两个按钮由前端渲染,网关只给文案+类别
_CAT_LABEL = {
    IBAN: "银行账号(IBAN)", TAX_ID_DE: "税号", CARD_PAN: "银行卡号",
    ID_DOC: "证件号", SECRET_PHRASE: "密码/密钥短语", HIGH_ENTROPY: "疑似密钥/高熵串",
}


def block_message(categories: Set[str]) -> str:
    labels = "、".join(_CAT_LABEL.get(c, c) for c in sorted(categories))
    return (
        f"⚠ 这一轮**没有发送,也没有记录**。检测到疑似{labels}。\n\n"
        "凭证的值不该进对话/记忆库(D23)。如果这确实是敏感信息,请删掉这段重发;\n"
        "如果这不是凭证(比如只是个订单号),重发时带上标记继续 —— 我只会记下"
        "「本轮命中类别」用于审计,不会记录你输入的内容。"
    )
