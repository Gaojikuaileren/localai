"""E4 · **方向 B**:响应回程出境闸(§5.6.2 五强制点 E4 的另一半)· D? · D81 的落地

════════════════════════════════════════════════════════════════════════
 ★★★ 先说清「方向 B」这三个字在本仓指**两件完全不同的事**

   · **D126 的「方向 B」= 后端上锁**(给 llama-server 加钥匙)—— ✅ 已完成,
     与本模块**毫无关系**;
   · **本模块的「方向 B」= 响应回程出境闸** —— 就是这里。D81(2026-08-04)立了它、
     明写「本决议**不开工**」,此后一直是空的。

 ⇒ 搜到「方向 B ✅」**不要**据此划掉 P3d / P6 的这条前置。STATE.md P6 那一行有同款警告。
════════════════════════════════════════════════════════════════════════

 方向 A(`e4_egress.py`)守的是**请求**:载荷离开受控设备集,去到别人的后端。
 本模块守的是**回程**:模型生成的**答案**离开受控设备集,去到外联通道的对面。

 D81 决定 1(1) 把这两根轴分开写过,照抄一遍免得又被合并:
     `Backend.egress` 管「**请求发给哪个模型**」—— 那是方向 A 的判据;
     D39 的 `sink`   管「**答案发到哪儿**」  —— 那是本模块的判据。
 一个 `egress=false` 的本地模型,答案照样可以顺着一条外联通道出去 ——
 方向 A 在那条路径上**全程不触发**。这就是本模块单独存在的全部理由。

════════════════════════════════════════════════════════════════════════
 ★★★ 诚实边界(D81 决定 1(3),逐字保留)——**不许在任何文档里把它写成 L5 的代码对应物**

   「外联通道只允许 S0」「最小披露」是**什么进了 prompt** 的性质。
   记忆内容被模型**复述**之后没有任何正则特征,本模块看不见它。
   真正的结构性强制写在 PLAN §4.6.3:**出境 sink 的会话不该挂载 `memory.search`**。
   ⇒ 本模块的正确定位是**凭证线 + 审计线的第二道防线**,不是记忆外发的证明。
   把它写成保证,得到的是**假的安全感**,而假的安全感比没有闸更贵。

════════════════════════════════════════════════════════════════════════
 ★★ 和方向 A 一样:**签名里没有 override / 放行 / 档位参数,以后也不该有。**

   出境不可逆:发出去就收不回,对面的日志、缓存、训练管线都不归你管。
   ⇒ 放行能力必须在**结构上不存在**,不是"默认关着"。
   ★ 本模块**另有**一条方向 A 没有的结构约束,见 `_assert_no_unlock_leak()`:
     拦截文案里**不许出现解除暗号**。理由是本机真踩过一次,见 `REPLY_SHAPES` 上方。
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Dict, List, Set, Tuple

import e1_detector as _e1
import gpu_policy as _gpu_policy

# ══════════════════════════════════════════════════════════════════════
#  ①  sink 轴:这一路回程算不算「出境」
#
#  ★★★ 反问过一遍:**新出现一个档位而没登记,默认落哪边?** —— 落 **EGRESS**(要扫)。
#    这与 `E1_OVERRIDE_ALLOWED_TIERS` 是 allowlist、与 `gpu_policy.caps_for` 表外
#    一律 `DENY_ALL` 是同一条纪律:**约束写成拒绝优先,新增的东西默认不自由**。
#
#  ★★ D81 决定 1(2) 实测过的那件事正是本表要接住的:今天真接一条桥进来,
#    `classify_caller` 造不出 `channel-relay`,它会落 `unregistered-local`,
#    于是**与"没人登记的普通本机账户"不可区分**。
#    ⇒ 本表**不去替 D81 待裁 ① 做决定**(`unregistered-local` 是不是出境 sink);
#      它做的是:今天在册的档位逐个写明,而**明天新加的那个档位默认是出境**。
#      D81 待裁 ① 的答案落在哪一侧,改的是本表的**一行**,不是本模块的形状。
# ══════════════════════════════════════════════════════════════════════

IN_SET = "in-set"          # 受控设备集内部 —— 答案没有离开你的地盘
EGRESS = "egress"          # 出境 —— 答案离开受控设备集,**不可逆**

#: 档位 → sink 类别。★ 这是**期望值表**,不是遍历源:遍历源是
#: `gpu_policy.TIER_CAPS`(见 `unregistered_tiers()`),两者对不上就该红。
SINK_CLASS: Dict[str, str] = {
    # ── 今天在册的七个档位,**逐个**写明。落 IN_SET 的理由各不相同,不许合并成一句 ──
    "trusted-local": IN_SET,
    #   本机 OS 会话信任(D28)。答案回到机主自己的屏幕上,没有离开受控设备集。
    "lan-device": IN_SET,
    #   带证书指纹、在成员表里的副机(P3b)。**它就是受控设备集的定义本身**。
    "lan-edge": IN_SET,
    #   代理进程档,非业务档。它转出去的对面仍然是 lan-device(那一跳由指纹判)。
    "unregistered-local": IN_SET,
    #   ★★ **这一行就是 D81 待裁 ①,今天按"不改变现状"落 IN_SET,不是按论证落的。**
    #   翻成 EGRESS 会改变**本机每一个未登记账户**的行为(访客 `Alle` /
    #   `CodexSandboxOffline` / `CodexSandboxOnline` 今天都启用着,2026-08-10 实跑核过)。
    #   ⇒ 那是一次有代价的裁定,不该由本车道顺手做掉。交回时列为待裁。
    "identity-unresolved": IN_SET,
    #   同上,且它是**故障档**(认人链断了,被降档的可能就是机主本人)——
    #   把一次 WMI 超时变成"你的回答发不出去",是拿故障惩罚用户。
    "denied-account": IN_SET,
    #   §6.8「绝不放行」的 ai-asset / ai-exec。★ 它在 chat 路由上**更早就 403 了**,
    #   走不到本闸。登记成 IN_SET **不是**说它安全,是说本闸不是它的防线 ——
    #   写 EGRESS 会让人以为这里在管它,而真正管它的是那个 403。
    "remote-unauthenticated": IN_SET,
    #   同上:更早 401。理由与上一行同族。
}


def unregistered_tiers() -> List[str]:
    """`gpu_policy.TIER_CAPS` 里有、而本表没登记的档位。

    ★★★ 反向全表:遍历源是**别人的表**(档位词表的今日唯一权威),
      本表只当期望值。新加一个档位而不来这儿登记 ⇒ `test_e4_response.py` 判红。
    ★ 反过来那一侧(本表有而词表没有)也一起报 —— 过期登记会让人以为某个档位还在。
    """
    known = set(_gpu_policy.TIER_CAPS)
    mine = set(SINK_CLASS)
    return sorted((known - mine) | (mine - known))


def sink_class(tier: str) -> str:
    """这个档位的回程算 IN_SET 还是 EGRESS。★ **表外一律 EGRESS**(fail-closed)。"""
    return SINK_CLASS.get(tier, EGRESS)


def is_egress_sink(tier: str) -> bool:
    return sink_class(tier) == EGRESS


def must_force_nonstream(tier: str) -> bool:
    """这一路要不要**强制**非流式。

    D81 决定 3(用户裁定 2026-08-04):
      · **出境 sink 不走流式,发一整段** —— 已发出的 chunk 收不回;
        闸门要能拦,前提是内容**还在手里**;
      · **局域网客户端照旧流式** —— 它在受控设备集内,不是出境 sink,**今天零影响**。

    D81 决定 4「共同必修」:`stream` 是**调用方控制**的 ⇒ 判据不能读它,必须**强制**。
    ★ 所以本函数只看档位,**不接受任何来自报文的输入** —— 与本模块没有 override
      是同一条理由:能被对面写进来的东西,不配当判据。
    """
    return is_egress_sink(tier)


# ══════════════════════════════════════════════════════════════════════
#  ②  字段边界:哪些字段算「答案」
#
#  ★★★ D81 决定 2(1) 实测(本车道 2026-08-22 独立复测过,数字一致):
#    **扫整个响应体 = 100% 全拦。** OpenAI 响应的 `id`(32 位随机串)
#    单独就触发 `high_entropy` —— 也就是说 naive 实现会让**每一条回答都发不出去**,
#    而当值的人的第一反应几乎必然是**把闸调松**。这条写在这里,就是为了不留给现场发现。
#
#  ⇒ 只能扫**生成内容**。而"只扫这几个字段"是 **denylist 形状** ——
#    它的失效方式永远是「上游加了个新字段,而它默认自由」。
#    D81 的处方:**立一张明写的表,并规定新字段默认落在要扫那一侧**。
#
#  ★★ 本表按**归一化路径**登记(数组下标一律折成 `[]`),不是按键名 ——
#    键名会重名:`choices[].message.tool_calls[].id` 是**随机 id**(信封),
#    而 `…function.arguments` 是**模型生成的**(答案)。只认键名会把两者混成一件事。
# ══════════════════════════════════════════════════════════════════════

ANSWER = "answer"          # 模型生成的内容 —— **要扫**
ENVELOPE = "envelope"      # 协议信封 —— **不扫**(扫了就是 100% 全拦)

#: 归一化路径 → 处置。★ 只登记**叶子**路径;中间节点由走查器自己下钻。
FIELD_DISPOSITION: Dict[str, str] = {
    # ── 顶层信封 ────────────────────────────────────────────────────
    "id": ENVELOPE,                       # ★ 就是它单独触发 high_entropy 的
    "object": ENVELOPE,
    "created": ENVELOPE,
    "model": ENVELOPE,
    "system_fingerprint": ENVELOPE,
    "service_tier": ENVELOPE,
    "usage.prompt_tokens": ENVELOPE,
    "usage.completion_tokens": ENVELOPE,
    "usage.total_tokens": ENVELOPE,
    # ── choices 那一层的信封 ────────────────────────────────────────
    "choices[].index": ENVELOPE,
    "choices[].finish_reason": ENVELOPE,
    "choices[].stop_reason": ENVELOPE,
    "choices[].matched_stop": ENVELOPE,
    "choices[].logprobs": ENVELOPE,
    "choices[].message.role": ENVELOPE,
    "choices[].delta.role": ENVELOPE,
    "choices[].message.tool_calls[].id": ENVELOPE,      # ★ 又一个随机 id
    "choices[].message.tool_calls[].type": ENVELOPE,
    "choices[].message.tool_calls[].index": ENVELOPE,
    "choices[].delta.tool_calls[].id": ENVELOPE,
    "choices[].delta.tool_calls[].type": ENVELOPE,
    "choices[].delta.tool_calls[].index": ENVELOPE,
    # ── 答案 ────────────────────────────────────────────────────────
    "choices[].message.content": ANSWER,
    "choices[].message.reasoning_content": ANSWER,
    "choices[].message.refusal": ANSWER,
    "choices[].message.tool_calls[].function.name": ANSWER,
    "choices[].message.tool_calls[].function.arguments": ANSWER,
    "choices[].delta.content": ANSWER,
    "choices[].delta.reasoning_content": ANSWER,
    "choices[].delta.refusal": ANSWER,
    "choices[].delta.tool_calls[].function.name": ANSWER,
    "choices[].delta.tool_calls[].function.arguments": ANSWER,
    "choices[].text": ANSWER,             # legacy completions 形状
}

# ── 未登记字段落哪一侧 ──────────────────────────────────────────────
#  ★★★ **这就是交回的裁定题 ②**,三个取值的具体后果逐条写在这里:
#
#    "scan"     未登记字段的**文本值也拿去扫**。
#               代价:上游(llama-server / 云端)加一个带随机串的新字段 ⇒
#                     **每条回答都被拦**,且症状与"检测器变严了"一模一样。
#    "envelope" 未登记字段**当信封放过**。
#               代价:回到 denylist —— 上游把生成内容挪进一个新字段(实际发生过:
#                     `reasoning_content` 就是这么长出来的)⇒ 闸**静默失效**,全绿。
#    "refuse"   未登记字段 ⇒ **直接拦**(D81 原话「新增字段默认落被扫一侧**或拒绝启动**」)。
#               代价:上游一次小版本升级就能让整条外联通道停摆,而这在
#                     "答案发不出去"这件事上是最响的失败 —— 也是最不容易被静默绕过的。
#
#  今天取 `"scan"`:它在**漏**与**吵**之间选了吵,而本闸今天没有任何生产消费者
#  (`SINK_CLASS` 里一个 EGRESS 都没有)⇒ 选它**今天零影响**,把代价留给裁定那一刻。
UNDECLARED_FIELD_SIDE = "scan"
_VALID_SIDES = ("scan", "envelope", "refuse")


def _walk(node: Any, path: str, out: List[Tuple[str, Any]]) -> None:
    """把响应对象铺平成 (归一化路径, 叶子值) 的清单。★ 数组下标折成 `[]`。"""
    if isinstance(node, dict):
        for k, v in node.items():
            _walk(v, f"{path}.{k}" if path else str(k), out)
    elif isinstance(node, list):
        for v in node:
            _walk(v, f"{path}[]", out)
    else:
        out.append((path, node))


@dataclass
class ResponseScan:
    """一次回程扫描的结果。

    ★ 与 `E1Result` 一样**只带类别,不带值/片段**(§6.9.8:定长凭证的哈希可爆破)。
      `undeclared` / `refused` 里装的是**路径名**,不是内容 —— 路径名是结构信息,
      它正是运维要看的那一栏(「上游加了什么字段」)。
    """

    categories: Set[str] = field(default_factory=set)
    undeclared: List[str] = field(default_factory=list)
    refused: List[str] = field(default_factory=list)

    @property
    def blocked(self) -> bool:
        return bool(self.categories) or bool(self.refused)


def extract_answer(data: Any, undeclared_side: str = "") -> Tuple[str, List[str], List[str]]:
    """从响应对象里取出**算作答案**的那部分文本。

    返回 `(要扫的文本, 未登记路径, 触发拒绝的路径)`。

    ★ 入参是**已经 JSON 解码**的对象,不是原始字节 —— 这条是硬约束,见
      `scan_response` 的文档串。
    """
    side = undeclared_side or UNDECLARED_FIELD_SIDE
    if side not in _VALID_SIDES:
        # ★ fail-closed:配错值不当成默认值,当成"拒绝"。一个拼错的配置项
        #   静默退回宽松侧,正是本仓最恨的形状。
        side = "refuse"
    leaves: List[Tuple[str, Any]] = []
    _walk(data, "", leaves)

    parts: List[str] = []
    undeclared: List[str] = []
    refused: List[str] = []
    for p, v in leaves:
        d = FIELD_DISPOSITION.get(p)
        if d == ENVELOPE:
            continue
        if d == ANSWER:
            if isinstance(v, str):
                parts.append(v)
            continue
        # 未登记
        undeclared.append(p)
        if side == "refuse":
            refused.append(p)
        elif side == "scan" and isinstance(v, str):
            parts.append(v)
    return "\n".join(parts), sorted(set(undeclared)), sorted(set(refused))


def scan_response(data: Any, undeclared_side: str = "") -> ResponseScan:
    """扫一次回程响应。

    ★★★ 入参必须是**已经 JSON 解码**的对象(D81 决定 2(2),本车道 2026-08-22 复测):
      `normalize()` 折全角、折中文数字,**不折 `\\uXXXX`** —— 而那正是模型输出
      到达时在原始 SSE 字节里的形态。实测:一个全角 IBAN 在原始字节里写作
      `\\uff24\\uff25\\uff18…`,**扫原始字节零命中,先解码再扫命中 iban**。
      ⇒ 谁把原始字节喂进来,谁就把这道闸关掉了,而且它全绿。
      本函数因此**不接受 `bytes`/`str`**:传进来直接判为拒绝,不做"顺手解码"。

    ★ 签名里【没有】override / 放行 / 档位参数,以后也不该有(同 `e4_egress.scan`)。
      `undeclared_side` **不是**放行入口:它三个取值里最宽的那个(`"envelope"`)
      仍然扫全部已登记的答案字段,而且它的方向由裁定决定、不由调用方每次决定。
    """
    if isinstance(data, (bytes, bytearray, str)):
        return ResponseScan(refused=["<raw-bytes>"])
    text, undeclared, refused = extract_answer(data, undeclared_side)
    r = _e1.scan(text)
    return ResponseScan(categories=set(r.categories), undeclared=undeclared, refused=refused)


# ══════════════════════════════════════════════════════════════════════
#  ③  拦下时对面看到什么(**裁定题 ①**,做成可配)
#
#  ★★★ 已知陷阱,本机 2026-07-28 真踩过一次(`gateway.py` 里那段长注释):
#    E1 的拦截文案**带着解除暗号**,且以 `role: assistant` 的正常消息返回 ⇒
#      第 1 轮被拦 → 前端把拦截文案**存进历史** → 第 2 轮整包重发 →
#      暗号出现在载荷里 → override 自动为真 →
#      **该会话此后每一轮全部自动解除,用户零操作**。
#    也就是说:**"像 AI 自己在说话"这个形状,恰好是把闸自己关掉的那个形状。**
#
#  ⇒ 默认取 `"notice"`。三个形状的具体后果逐条写在下面,交回裁定。
#  ★★ 无论选哪个形状,`_assert_no_unlock_leak()` 都在 import 期把
#    「文案里带解除暗号」这条路**焊死** —— 配置项可以选错,这条不行。
# ══════════════════════════════════════════════════════════════════════

_NOTICE = (
    "【本机出境闸】这条回答里检测到凭证类信息({labels}),已在发出前拦下。\n"
    "对面**没有收到**这条回答。出境不可逆,所以这条路不提供放行选项。"
)
_ASSISTANT_VOICED = (
    "抱歉,我这条回答里好像带上了不该发出去的信息({labels}),所以我没有把它发出去。"
)

#: 形状 → 给对面的文案(`None` = 对面什么都收不到)。
REPLY_SHAPES: Dict[str, Any] = {
    # 一句固定的话,**明确署名是闸在说话**,不是模型在说话。
    #   代价:对面会知道"这里有一道闸",也会知道这一轮发生了什么。
    "notice": _NOTICE,
    # 像 AI 自己在说话。
    #   ★★ 代价就是上面那段:它天生要被前端**存进对话历史**,
    #     于是拦截文案会作为**下一轮的输入**回到载荷里。本机踩过一次。
    #     选它必须同时接受"拦截文案会变成模型的上下文"这件事。
    "assistant": _ASSISTANT_VOICED,
    # 对面什么都收不到。
    #   代价:对面看到的是**一次静默的失败**(消息没来)——
    #   而"没来"与"网络断了"在对面那侧不可区分,人会重发,重发会再被拦。
    #   ★ 本机侧仍然有响应头 + 审计,所以**机主看得见**;看不见的只有对面。
    "silent": None,
}

BLOCKED_REPLY_SHAPE = "notice"


def blocked_reply_text(categories: Set[str], shape: str = ""):
    """拦下时给对面的话。`None` = 什么都不给(`"silent"` 形状)。

    ★ 与 E1 的措辞必须不同(同 `e4_egress.block_message` 的理由):
      E1 说"可以放行但你要确认",这里说"这条路没有放行" ——
      写成一样,用户会去找一个并不存在的按钮。
    """
    tpl = REPLY_SHAPES.get(shape or BLOCKED_REPLY_SHAPE, _NOTICE)
    if tpl is None:
        return None
    labels = "、".join(_e1._CAT_LABEL.get(c, c) for c in sorted(categories))
    return tpl.format(labels=labels)


def _assert_no_unlock_leak() -> None:
    """★★★ 结构性焊死:任何形状的拦截文案里都**不许**出现解除暗号。

    这不是风格检查。见本节抬头:带暗号的拦截文案 + 会被存进历史的返回形态 =
    **闸在第一次拦截之后把自己永久关掉**,而它看起来完全正常。
    ⇒ 让"文案里出现暗号"在 **import 期**就起不来,而不是等运行期。
    ★ 反问过一遍:这条判据**可以为假吗**?能 —— `test_e4_response.py` 拿一段
      合成的、确实带暗号的文案喂给它,要求它抛。恒真的护栏等于没有护栏。
    """
    for name, tpl in REPLY_SHAPES.items():
        if tpl is None:
            continue
        if _e1.OVERRIDE_PHRASE in tpl:
            raise RuntimeError(
                f"出境闸拦截文案 {name!r} 里带着 E1 解除暗号 —— "
                "它会被前端存进历史、在下一轮回到载荷里,于是该会话此后自动解除。"
                "(2026-07-28 本机实测过一次)拒绝启动。")


_assert_no_unlock_leak()
