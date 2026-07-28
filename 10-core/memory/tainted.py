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
from dataclasses import dataclass
from typing import Any, Dict, List, Optional

__all__ = [
    "TaintedText", "seal", "MemoryLeakError",
    "unseal_for_storage", "unseal_for_embedding",
    "unseal_for_client", "unseal_for_prompt",
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
    def __init__(self) -> None:
        self.records: List[UnsealRecord] = []

    def note(self, handle: str, purpose: str, sink: str) -> None:
        self.records.append(UnsealRecord(handle, purpose, sink))

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
    __slots__ = ("_handle", "sensitivity", "source")

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
        # 只允许与同类比较,且比的是内容 —— 但不暴露内容
        if not isinstance(other, TaintedText):
            return NotImplemented
        return _VAULT.get(self._handle) == _VAULT.get(other._handle)

    def __hash__(self) -> int:
        return hash(_VAULT.get(self._handle, ""))

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
    return TaintedText(handle, sensitivity, source)


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


def unseal_for_client(t: TaintedText, *, caller: str) -> str:
    """③ 回客户端:MEMORY 平面把正文以 JSON 交给本地面板/检索。

    ★ 这是设计里【被漏掉】的那个出口(核验指出)。它必然存在 —— 记忆面板要显示正文、
      溯源展开要给出原文片段。给它名字、让它记账,好过假装它不存在。
    ★ caller 必须是本机可信调用方;远程只能经 v_memory_nons2 拿非 S2 内容(D30)。
    """
    if t.sensitivity == "S2" and caller != "trusted-local":
        raise MemoryLeakError(
            f"S2 内容不得交给 {caller}(§4.11.4 结构性隔离)。"
        )
    return _unseal(t, "client", f"client:{caller}")


def unseal_for_prompt(t: TaintedText, *, backend: str) -> str:
    """④ 进 prompt:唯一**朝模型去**的出口。出境闸门挂在这里(§4.6.3)。

    ★ 渲染层的最终强制点不在这里,而在**已组装完成的 prompt 字符串**上 ——
      因为拼接之后类型信息就没了。本函数只负责:标明用途、记账、拦住已知的出境后端。
    """
    if t.sensitivity == "S2":
        raise MemoryLeakError("S2 内容永不进 prompt(§6.9.3)。")
    return _unseal(t, "prompt", f"backend:{backend}")


# ── 给日志/序列化用的安全转换 ────────────────────────────────────
def safe_meta(t: TaintedText) -> Dict[str, Any]:
    """要往日志/审计里写「关于这条记忆」的信息时用这个,而不是想办法把正文弄出来。"""
    return {"sensitivity": t.sensitivity, "source": t.source, "length": t.length,
            "sealed": True}
