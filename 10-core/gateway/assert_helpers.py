"""断言辅助 —— 共用的判据收紧工具。

★★★ 这个文件存在的理由,在 `00-docs/ASSERTION-PITFALLS.md` 第 1 条:
    「断言撞在"解释它已经被删了"的那句注释上」在 2026-08-04 一天里踩了 **5 次**。
    用户当天的裁定:**同一个陷阱踩了三次以上就要记下,防止以后再踩。**

    五次分别是:`_notify_locked` 的 `await` · `set_power` 注释里的 `_intended`
    (那句话**正是在说明"不碰"**)· `Body()` · `e4_egress` ·
    C# 侧的 `ModelCatalog.All` / `chat.8b`。

    根因是结构性的,不是手滑:**一段负责任的删除必然会在注释里提到被删的东西** ——
    不提,后人就不知道这里为什么空着、以及不许再加回来。
    ⇒ 越是写得清楚的代码,越容易把反向断言弄红。

★ 绝对不许的两种"修法":
    ① 把断言删掉 —— 那正是这条坑最想诱你做的事;
    ② 把注释改写成绕开断言的样子 —— 那让注释为了迁就测试而说不清话。
    唯一正确的方向是**收紧判据**,也就是本文件。

C# 侧的同款工具是 `20-client-win/app/Selftest.cs` 里的 `CodeOnly()`。
"""
import inspect
import re

_DOCSTRING = re.compile(r'"""(?:.|\n)*?"""')


def _strip_line_comment(line: str) -> str:
    """去掉行尾 `#` 注释,但**不动字符串里的 `#`**。

    ★★ 这一条是堵一个 fail-open,不是洁癖:原来各文件用的是裸 `re.sub(r"#.*", "", src)`,
      它会把 `detail = "a #b backend"` 砍成 `detail = "a `。
      于是一条「源码里不得出现 backend」的断言会**因为砍掉了真违规而变绿** ——
      去注释的方向天然是 fail-open,所以宁可少去一点,不能多去。
    """
    q = None          # 当前所在的引号种类;None = 不在字符串里
    i = 0
    while i < len(line):
        c = line[i]
        if q:
            if c == "\\":
                i += 2
                continue
            if c == q:
                q = None
        elif c in "\"'":
            q = c
        elif c == "#":
            return line[:i]
        i += 1
    return line


def code_only(obj_or_src) -> str:
    """只留【真正会执行的代码】:去掉 docstring 与 `#` 注释。

    接受源码字符串,或任何 `inspect.getsource` 能处理的对象(函数/类/模块)。

    ★ 用法:凡是「源码里**不得**出现 X」的断言,一律先过这个函数。
    ★ 字符串**字面量保留**(与 C# 侧的 CodeOnly 不同):Python 这边的断言经常要
      判「某个错误码/字段名有没有出现在响应体里」,那正是字符串。
    """
    src = obj_or_src if isinstance(obj_or_src, str) else inspect.getsource(obj_or_src)
    src = _DOCSTRING.sub("", src)
    return "\n".join(_strip_line_comment(l) for l in src.split("\n"))


def lock_bodies(src: str, lock_expr: str = "async with self._lock:"):
    """精确取出每个 `async with ...:` 的【缩进块】。

    ★ 见 ASSERTION-PITFALLS 第 4 条。原来用的是 `(.*?)(?=\\n    def )` ——
      一路捕到下一个方法定义。S8 之前每个锁块恰好是方法最后一段所以看着对;
      `apply_intended` 里锁块后面还有【缩进已退回去】的代码,被误算进锁内 ⇒ 三条全红。
      修法是按**缩进**取块,不是把断言删掉。

    ★★ 调用方**必须**配一条元断言:`len(bodies) == src.count(lock_expr)` 且 `> 0`。
      提取器一旦匹配不上,下游那个 for 就一次都不跑,里面的检查会**静默消失** ——
      测试仍然全绿,而它已经什么都不管了。那是本项目最恨的假断言。
    """
    lines, out = src.split("\n"), []
    pat = re.compile(r"^(\s*)" + re.escape(lock_expr) + r"\s*$")
    for i, ln in enumerate(lines):
        m = pat.match(ln)
        if not m:
            continue
        ind, body = len(m.group(1)), []
        for nxt in lines[i + 1:]:
            if nxt.strip() == "" or len(nxt) - len(nxt.lstrip()) > ind:
                body.append(nxt)
            else:
                break
        out.append("\n".join(body))
    return out


def assignments_to(src: str, attr: str):
    """找出对 `attr` 的**赋值**(排除比较)。

    ★ 见 ASSERTION-PITFALLS 第 4 条:`self._state ==`(比较)会被
      `self._state\\s*=` 匹配到 —— **前者是后者的前缀**。用 `=[^=]` 把两者分开。
    """
    return re.findall(re.escape(attr) + r"\s*=[^=]", src)
