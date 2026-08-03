"""隐私强制的【类型层】(§4.6.1 三层强制之一)

原设计用 `no_export=true` 属性标记来拦截。审查发现它**在比网络边界更早的地方就失效了**:

    ctx = "\\n".join(r.text for r in results)      # RAG 注入的标准写法,每次都走

`r.text` 是一个 `str`,不是 `MemoryResult` —— **引用链在属性读取那一刻就断了**。
不需要跨设备、不需要复制粘贴,程序内部每次组装 prompt 都在自动洗白。

本模块是那条链的类型层替代。两条从对抗性核验学来的硬教训写在最前面:

★★ 教训一:值【不能】存成属性,否则 `t._value` 直读绕过全部 dunder。
   `__slots__` 不阻止属性读取;`__getattr__` 只在正常查找【失败】时触发,
   而 `_value` 是 slot ⇒ 查找成功 ⇒ `__getattr__` 永不触发。
   于是 `"\\n".join(r.body._value for r in results)` 静默成功 —— 与 `r.text` 完全同构。
   → 本实现把正文放进**模块私有登记表**,对象只持一个不透明句柄。
     `t._value` 这条路径在结构上**不存在**,不是「被禁止」。

★★ 教训二:真实解封点有【四个】,不是一个。设计只承认 render_for_prompt 时,
   另外三个就是没有名字、不记账、没测试的暗门:
     ① 写库    —— repo 必须把 str 交给 psycopg 作 SQL 参数
     ② 向量化  —— 必须把正文明文 POST 给 embedding :18084
     ③ 回客户端 —— MEMORY 平面必须把正文以 JSON 交给本地面板/检索
     ④ 进 prompt —— 唯一朝模型去的那个,出境闸门挂在这里
   → 四个各有专名函数,各自记账。**没有通用的 `.value`**。

★ 诚实边界(§4.6.2):本层拦的是**意外**,不是**决心**。
  能拿到代码执行的人当然能读模块私有表。它把「每次组装 prompt 都在自动洗白」
  变成「必须显式调用一个有名字、会记账的函数」。
  **不得据此声称「记忆零外发已被证明」。**
"""
from __future__ import annotations

import secrets
import weakref
from collections import deque
from dataclasses import dataclass
from enum import Enum
from typing import Any, Dict, List, Optional

__all__ = [
    "TaintedText", "seal", "MemoryLeakError",
    "CallerTier", "Backend",
    "unseal_for_storage", "unseal_for_embedding",
    "unseal_for_client", "unseal_for_prompt",
    "equals_plaintext",
    "UnsealLedger", "current_ledger",
]


class MemoryLeakError(RuntimeError):
    """试图把记忆正文变成普通字符串。

    ★ 异常消息里**绝不带正文** —— 否则这个类自己就成了泄漏点:
      异常会进日志、进 traceback、进错误响应。
    """


# ── 正文的实际存放处:模块私有,不在对象上 ─────────────────────────
# 对象只持句柄。因此没有任何属性能读到正文 —— `t._value` 不是被禁止,是不存在。
_VAULT: Dict[str, str] = {}


@dataclass(frozen=True)
class UnsealRecord:
    handle: str
    purpose: str
    sink: str


class UnsealLedger:
    """记录本次请求里发生过的每一次解封。渲染层与出境闸门据此判断。

    ★ 它是**审计**,不是**防护** —— 防护靠「解封必须显式调用具名函数」。
    """
    # ★ 封顶(审计 2026-07-31):_LEDGER 是模块级单例、只增不减 ——
    #   存的是句柄+用途字符串(不是正文),但常驻进程里仍会慢慢隆起。
    #   用 deque 封顶:它是审计轨,只需最近足够多条供渲染/出境闸判断,不需全史。
    #   (本该是"每请求一个",说明已如此;封顶是不改架构的步进。)
    _CAP = 4096

    def __init__(self) -> None:
        self.records: deque = deque(maxlen=self._CAP)

    def note(self, handle: str, purpose: str, sink: str) -> None:
        self.records.append(UnsealRecord(handle, purpose, sink))

    def reset(self) -> None:
        """新一轮请求开始时可显式清空(现为单例,尚未接请求上下文)。"""
        self.records.clear()

    @property
    def purposes(self) -> set:
        return {r.purpose for r in self.records}

    def __len__(self) -> int:
        return len(self.records)


_LEDGER = UnsealLedger()


def current_ledger() -> UnsealLedger:
    return _LEDGER


class TaintedText:
    """记忆正文的密封载体。

    刻意**不实现**:`__str__` `__repr__`(不泄露) `__format__` `__add__` `__radd__`
    `__iter__` `__contains__` `__getitem__` `__bytes__` —— 任何一条被实现,
    f-string / join / % / .format / json.dumps / logging 就都能把它悄悄变回 str。

    ★ 没有 `.value` / `._value` / `.text` 属性。取值只能经四个具名解封函数之一。
    """
    # ★ __weakref__:让对象可被弱引用(seal 用 weakref.finalize 绑定正文寿命)。
    __slots__ = ("_handle", "sensitivity", "source", "__weakref__")

    def __init__(self, handle: str, sensitivity: str, source: str) -> None:
        object.__setattr__(self, "_handle", handle)
        object.__setattr__(self, "sensitivity", sensitivity)
        object.__setattr__(self, "source", source)

    # ---- 阻断一切隐式转字符串 ----
    def _blocked(self, how: str) -> "MemoryLeakError":
        return MemoryLeakError(
            f"记忆正文不能{how}(§4.6.1 类型层)。"
            f"[sensitivity={self.sensitivity} source={self.source}] "
            "取值必须显式调用四个解封函数之一:unseal_for_storage / unseal_for_embedding "
            "/ unseal_for_client / unseal_for_prompt —— 它们各自记账,而隐式转换不会。"
        )

    def __str__(self) -> str:            raise self._blocked("被 str() 转换")
    def __format__(self, spec) -> str:   raise self._blocked("进 f-string / format()")
    def __add__(self, other):            raise self._blocked("被字符串拼接")
    def __radd__(self, other):           raise self._blocked("被字符串拼接")
    def __iter__(self):                  raise self._blocked("被迭代 / join()")
    def __contains__(self, item):        raise self._blocked("被 in 检查")
    def __getitem__(self, k):            raise self._blocked("被切片 / 索引")
    def __bytes__(self):                 raise self._blocked("被 bytes() 转换")

    def __repr__(self) -> str:
        # ★ repr 必须**安全且不抛异常** —— 调试器、pytest、logging 都会调它。
        #   抛异常会让排查变成噩梦;泄露正文则等于没有这个类。故:只给元数据。
        #
        # ★★ 实测印证(2026-07-28):`logging` 在格式化失败时【不抛出】异常 ——
        #    它内部捕获,把异常和**参数的 repr** 一起打到 stderr。
        #    即 `lg.info("x=%s", t)` 不会中断程序,而是让 logging 去调 repr。
        #    ⇒ 如果这里泄露正文,logging 自己的错误处理就会把正文写进日志。
        #    「repr 安全」因此不是洁癖,是这条路径上唯一的防线。
        return f"<TaintedText sensitivity={self.sensitivity} source={self.source} sealed>"

    # 允许的元信息(刻意做成显式属性而非 __len__:取长度是个决定,不是顺手)
    @property
    def length(self) -> int:
        return len(_VAULT.get(self._handle, ""))

    def __eq__(self, other: Any) -> bool:
        """★★ 比的是【句柄】,不是内容(2026-07-28 修正)。

        原实现比 `_VAULT` 里的两段明文,注释写着「比的是内容 —— 但不暴露内容」。
        **那个推理是错的**:逐条比较内容**就是**在暴露内容,一次一个比特,
        而且不经任何解封点、不写任何账目。

        实测(规格提取时确认):
            seal('我妹妹叫小雨','S0') == seal('我妹妹叫小雨','S2')  →  True
            ledger 增量                                          →  0

        ⇒ 这是一个**猜测-确认预言机**:任何能调到 seal 的代码都能在不留痕迹的情况下
          逐条确认记忆内容。住址、生日、健康状况这类**低熵、可枚举**的内容尤其危险 ——
          攻击者不需要读出正文,只需要不断猜、看哪次返回 True。
          而本模块的承诺是「取值只能经四个具名解封函数之一」。

        改为句柄相等:两个密封对象相等 ⇔ 它们是同一次密封。
        真要比内容,用 `equals_plaintext()` —— 它会记账。
        """
        if not isinstance(other, TaintedText):
            return NotImplemented
        return self._handle == other._handle

    def __hash__(self) -> int:
        # ★ 同理:以明文为输入的 hash 会把内容泄进任何一个字典/集合的桶分布里,
        #   而且 hash 碰撞本身就能被当成一个更弱的预言机。
        return hash(self._handle)

    def __setattr__(self, k, v):
        raise MemoryLeakError("TaintedText 不可变(防止把句柄换成别的正文)")


def seal(text: str, *, sensitivity: str, source: str) -> TaintedText:
    """把一段记忆正文密封起来。所有从库里/从用户输入来的记忆正文都应立刻经此。"""
    if isinstance(text, TaintedText):
        return text
    if text is None:
        text = ""
    if not isinstance(text, str):
        raise TypeError(f"seal() 只接受 str,收到 {type(text).__name__}")
    handle = secrets.token_urlsafe(16)
    _VAULT[handle] = text
    t = TaintedText(handle, sensitivity, source)
    # ★★ 把保险库条目的寿命绑到 TaintedText 对象的寿命上(审计 2026-07-31):
    #   以前 _VAULT 只增不减 —— 每次 seal 都新生一个句柄,而 repo 每次查询拿到的都是新 str 对象,
    #   于是同一条事实读 N 次就在进程内攒 N 份明文,永不释放。
    #   对象一走(GC),它那份正文也跟着从 _VAULT 里消失。
    #   ★ 不违反“正文不在对象上”:finalize 不给对象设任何属性,正文仍在模块级 _VAULT 里。
    weakref.finalize(t, _VAULT.pop, handle, None)
    return t


# ── 调用方档位与后端契约:两个都不能是裸字符串 ────────────────────
class CallerTier(str, Enum):
    """谁在取这段正文。

    ★★ 必须是枚举而不是 str。原实现 `unseal_for_client(t, caller="trusted-local")`
       收的是裸字符串,判据写成 `if sensitivity=="S2" and caller!="trusted-local"` ——
       **denylist 形状**:将来新增一档(局域网设备 / 外联桥),它默认落在**放行**一侧。
       这与 provenance denylist、E1 override 是同一族缺陷:判据写成放行优先,
       新增的东西默认自由。
    """
    TRUSTED_LOCAL = "trusted-local"        # 本机 OS 会话信任(D28)
    LAN_DEVICE = "lan-device"              # 已配对的局域网客户端(P3b)
    CHANNEL_RELAY = "channel-relay"        # 外联通道的桥(P3d)—— 全系统最低信任档
    REMOTE_UNAUTH = "remote-unauthenticated"
    # ↓ 两个「结构上取不到任何正文」的档位。写出来比省略强:省略会被后人当成漏了。
    RESIDENT_OBSERVER = "resident-observer"   # Vigil(§6.3 档位表)—— 只读 mem.exists + 投影
    EXT_OPERATOR = "ext-operator"             # 层二外部控制面(D66)—— 记忆维「不适用」


# ★ allowlist:键是敏感度,值是**允许**取用的档位。
#   新增一个档位而不在这里登记 ⇒ 它对任何敏感度都取不到正文(fail-closed)。
#
# ★★ RESIDENT_OBSERVER 与 EXT_OPERATOR **刻意不出现在下面任何一个集合里**。
#    这不是漏写 —— 是 §6.3 档位表与 D66 的直接兑现:
#      · Vigil 只能读 mem.exists 与投影,拿不到任何正文(§17.7 隔离设计);
#      · 层二外部控制面按 D67 只能拿结构投影,记忆库任何行都无揭示路径。
#    本结构是 {敏感度: 允许档位集},没有「把某档位登记为空集」的位置,
#    所以这条约束由 test_tainted.py 的一条正面断言守着:
#    断言这两档不出现在任何 _ALLOWED_CALLERS 的值里。
#    ——【不要】为了"看起来登记过"而把它们加进任何集合。
_ALLOWED_CALLERS = {
    "S0": frozenset({CallerTier.TRUSTED_LOCAL, CallerTier.LAN_DEVICE}),
    "S1": frozenset({CallerTier.TRUSTED_LOCAL, CallerTier.LAN_DEVICE}),
    "S2": frozenset({CallerTier.TRUSTED_LOCAL}),      # §4.11.4 结构性隔离,无提级路径
}

# ★ 无正文档位:这两档对任何敏感度都取不到正文。给它一个名字,让断言有标的。
NO_PLAINTEXT_TIERS = frozenset({CallerTier.RESIDENT_OBSERVER, CallerTier.EXT_OPERATOR})


@dataclass(frozen=True)
class Backend:
    """生成后端的契约。★ `egress` 是必填的,没有默认值。

    §4.6.1 类型层的原文判据是 `backend.egress == true 时抛 MemoryExportViolation`
    —— 判的是**后端是否出境**,与 sensitivity 无关:
    一条 S0 记忆送进云端后端,同样违反 §5.6.2 的 L5「记忆库内容,永久禁止」。
    """
    name: str
    egress: bool


# ── 四个具名解封点 ────────────────────────────────────────────────
# 每个都记账。**没有通用的 unseal()** —— 通用出口等于没有出口管理。

def _unseal(t: TaintedText, purpose: str, sink: str) -> str:
    if not isinstance(t, TaintedText):
        raise TypeError(f"解封函数只接受 TaintedText,收到 {type(t).__name__}")
    _LEDGER.note(t._handle, purpose, sink)
    return _VAULT.get(t._handle, "")


def unseal_for_storage(t: TaintedText, *, table: str) -> str:
    """① 写库:psycopg 需要 str 作 SQL 参数。**不朝模型去**,不触发出境闸门。"""
    return _unseal(t, "storage", f"pg:{table}")


def unseal_for_embedding(t: TaintedText, *, endpoint: str = "127.0.0.1:18084") -> str:
    """② 向量化:必须把正文明文 POST 给本地 embedding 服务。

    ★ 该服务以 ai-mem 运行、仅监听回环、不出网 —— 这是它可被允许的**唯一**理由。
      若将来 embedding 改成云端,本函数必须走出境闸门(§4.6.3)。
    """
    if not endpoint.startswith("127.0.0.1"):
        raise MemoryLeakError(
            f"embedding 端点 {endpoint} 不是回环地址 —— 正文不得离开本机(§4.6.3)。"
        )
    return _unseal(t, "embedding", f"http://{endpoint}")


def unseal_for_client(t: TaintedText, *, caller: CallerTier) -> str:
    """③ 回客户端:MEMORY 平面把正文以 JSON 交给本地面板/检索。

    ★ 这是设计里【被漏掉】的那个出口(核验指出)。它必然存在 —— 记忆面板要显示正文、
      溯源展开要给出原文片段。给它名字、让它记账,好过假装它不存在。

    ★★ `caller` 必须是 CallerTier 枚举,不接受字符串 ——
       让「传一个新字符串进来」在类型上不可能(见 CallerTier 的注释)。
       判据是 **allowlist**:档位不在该敏感度的允许集合里就拒,默认拒绝。
    """
    if not isinstance(caller, CallerTier):
        raise TypeError(
            f"caller 必须是 CallerTier 枚举,收到 {type(caller).__name__}。"
            "裸字符串会让判据退化成 denylist —— 新增档位默认放行。")
    allowed = _ALLOWED_CALLERS.get(t.sensitivity, frozenset())
    if caller not in allowed:
        raise MemoryLeakError(
            f"{t.sensitivity} 内容不得交给 {caller.value}(§4.11.4 结构性隔离)。"
            f"该敏感度允许的档位:{sorted(c.value for c in allowed) or '无'}")
    return _unseal(t, "client", f"client:{caller.value}")


def unseal_for_prompt(t: TaintedText, *, backend: Backend) -> str:
    """④ 进 prompt:唯一**朝模型去**的出口。出境闸门挂在这里(§4.6.3)。

    ★★ 判据是 `backend.egress`,不是 sensitivity(2026-07-28 修正)。
       原实现只写了 `if t.sensitivity == "S2": raise` —— 那是 §6.9.3 的要求,
       不是 §4.6.1 的。§4.6.1 的原文判据是 **`backend.egress == true 时抛**,
       与 sensitivity 无关:一条 S0 记忆送进云端后端,同样违反 §5.6.2 的 L5
       「记忆库内容,永久禁止(出境)」。
       后果是 `unseal_for_prompt(seal(记忆,'S0'), backend='escalate.cloud')`
       **静默成功** —— 而 test_tainted.py 当时把它断言为「正常拿到正文」。

    ★ 诚实边界:渲染层的最终强制点**不在这里**,而在【已组装完成的 prompt 字符串】上
      —— 拼接之后类型信息就没了(§4.6.1 渲染层 = §6.9.4 的 E4,同一个点)。
      那一层目前**尚未实现**,本函数只是类型层的这一半。
    """
    if not isinstance(backend, Backend):
        raise TypeError(
            f"backend 必须是 Backend(name, egress),收到 {type(backend).__name__}。"
            "传字符串会让出境判据无从做起 —— egress 必须由调用方显式声明。")
    if backend.egress:
        raise MemoryLeakError(
            f"记忆正文不得进入出境后端 {backend.name}(§4.6.1 类型层 · §5.6.2 L5)。"
            "与敏感度无关 —— S0 记忆送出去同样是出境。")
    if t.sensitivity == "S2":
        raise MemoryLeakError("S2 内容永不进 prompt(§6.9.3)。")
    return _unseal(t, "prompt", f"backend:{backend.name}")


def equals_plaintext(a: TaintedText, b: TaintedText, *, reason: str) -> bool:
    """需要真的比较两段正文时用这个 —— 它**记账**。

    ★ `__eq__` 已改为比句柄(见其注释:内容比较是一个不留痕迹的猜测-确认预言机)。
      去重、幂等这类场景确实需要内容比较,那就走这里:留下"谁、为什么、比了两条"的账。
    """
    if not (isinstance(a, TaintedText) and isinstance(b, TaintedText)):
        raise TypeError("equals_plaintext 只接受两个 TaintedText")
    _LEDGER.note(a._handle, "compare", f"compare:{reason}")
    _LEDGER.note(b._handle, "compare", f"compare:{reason}")
    return _VAULT.get(a._handle) == _VAULT.get(b._handle)


# ── 给日志/序列化用的安全转换 ────────────────────────────────────
def safe_meta(t: TaintedText) -> Dict[str, Any]:
    """要往日志/审计里写「关于这条记忆」的信息时用这个,而不是想办法把正文弄出来。"""
    return {"sensitivity": t.sensitivity, "source": t.source, "length": t.length,
            "sealed": True}
