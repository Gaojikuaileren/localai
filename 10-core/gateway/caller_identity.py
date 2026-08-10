"""调用方 OS 身份解析(Windows)· D30 混淆代理修正

问题:网关在 127.0.0.1 上收连接,原 classify_caller 对【任意】回环调用方都返回 trusted-local。
Windows 的回环对本机任意账户开放、无 per-socket ACL —— 于是 ai-asset(跑 ComfyUI 第三方节点)
一旦经网关触达记忆(网关持 Qdrant api_key / 连 PG),就是绕过隔离读全库的混淆代理。

解法:从 TCP 连接反查调用方的真实 OS 身份 ——
  连接的【客户端源端口】→ 拥有该 socket 的 PID(GetExtendedTcpTable)
  → 进程令牌的用户 SID(OpenProcessToken + GetTokenInformation)→ 账户名。
据此拒绝隔离服务账户(ai-asset / ai-exec)。

★ 纯查询、不改任何状态。任一 Win32 调用失败 → 返回 None,由调用方决定 fail-open/closed。
★ 非 Windows 或解析不到 → None。
"""
from __future__ import annotations

import socket
import struct
import subprocess
import time
from typing import NamedTuple, Optional, Tuple

try:
    import ctypes as C
    from ctypes import wintypes as W
    _WIN = True
except Exception:  # 非 Windows
    _WIN = False

AF_INET = 2
TCP_TABLE_OWNER_PID_ALL = 5
NO_ERROR = 0
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
TOKEN_QUERY = 0x0008
_TokenUser = 1

if _WIN:
    class _MIB_TCPROW_OWNER_PID(C.Structure):
        _fields_ = [
            ("dwState", W.DWORD), ("dwLocalAddr", W.DWORD), ("dwLocalPort", W.DWORD),
            ("dwRemoteAddr", W.DWORD), ("dwRemotePort", W.DWORD), ("dwOwningPid", W.DWORD),
        ]

    _iphlp = C.windll.iphlpapi
    _k32 = C.windll.kernel32
    _adv = C.windll.advapi32

    _iphlp.GetExtendedTcpTable.restype = W.DWORD
    _iphlp.GetExtendedTcpTable.argtypes = [
        C.c_void_p, C.POINTER(W.DWORD), W.BOOL, W.ULONG, C.c_int, W.ULONG]
    _k32.OpenProcess.restype = W.HANDLE
    _k32.OpenProcess.argtypes = [W.DWORD, W.BOOL, W.DWORD]
    _k32.CloseHandle.argtypes = [W.HANDLE]
    _adv.OpenProcessToken.restype = W.BOOL
    _adv.OpenProcessToken.argtypes = [W.HANDLE, W.DWORD, C.POINTER(W.HANDLE)]
    _adv.GetTokenInformation.restype = W.BOOL
    _adv.GetTokenInformation.argtypes = [
        W.HANDLE, C.c_int, C.c_void_p, W.DWORD, C.POINTER(W.DWORD)]
    _adv.LookupAccountSidW.restype = W.BOOL
    _adv.LookupAccountSidW.argtypes = [
        W.LPCWSTR, C.c_void_p, W.LPWSTR, C.POINTER(W.DWORD),
        W.LPWSTR, C.POINTER(W.DWORD), C.POINTER(W.DWORD)]


def _port(dw: int) -> int:
    return socket.ntohs(dw & 0xFFFF)


def _addr(dw: int) -> str:
    return socket.inet_ntoa(struct.pack("<L", dw & 0xFFFFFFFF))


MIB_TCP_STATE_ESTAB = 5


def _tcp_rows():
    """返回 [(state, laddr, lport, pid)] —— **值拷贝**。

    ★★ 绝不可返回指向 buf 的 ctypes 视图(2026-07-28 审查发现的 use-after-free):
    `C.cast(整数地址, ...)` 【不会】建立 keepalive 引用(实测 `cast(int)._objects is None`,
    而 `cast(对象)._objects` 是 dict)。函数一返回,局部 buf 引用计数归零、内存被释放,
    返回的数组即成悬垂指针 —— 实测同尺寸缓冲再分配 50/50 落回同一地址,精确复刻该形状后
    返回值立刻被写坏。后果是安全性的:PID 读错 → 归到别的账户 → ai-asset 可能被判成
    可信账户【静默放行】(classify_caller 只在肯定解析到隔离账户时才拒)。
    改用 from_buffer(它会正确设置 _objects,且自带越界检查)并把值拷出来,彻底摆脱生命周期问题。
    """
    size = W.DWORD(0)
    _iphlp.GetExtendedTcpTable(None, C.byref(size), False, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0)
    if size.value == 0:
        return []
    buf = (C.c_byte * size.value)()
    if _iphlp.GetExtendedTcpTable(buf, C.byref(size), False, AF_INET,
                                  TCP_TABLE_OWNER_PID_ALL, 0) != NO_ERROR:
        return []
    n = W.DWORD.from_buffer(buf, 0).value                     # dwNumEntries
    if n == 0:
        return []
    arr = (_MIB_TCPROW_OWNER_PID * n).from_buffer(buf, C.sizeof(W.DWORD))
    return [(r.dwState, r.dwLocalAddr, r.dwLocalPort, r.dwOwningPid) for r in arr]


def resolve_peer_pid(client_ip: str, client_port: int) -> Optional[int]:
    """回环上,拥有 local==client_ip:client_port 那个【已建立】socket 的进程 = 调用方。

    ★ 必须按 state 过滤:表里有大量 TIME_WAIT 残留行且 owner PID=0(本机实测数百行)。
      临时端口被系统回收再分配时,若旧 TIME_WAIT 行还在,只按 ip:port 匹配会先命中 PID=0
      那行 → pid_to_account(0) 失败 → 返回 None → chat 路径 fail-open。这是独立于 UAF 的
      第二条 fail-open 路径。
    """
    for state, laddr, lport, pid in _tcp_rows():
        if (state == MIB_TCP_STATE_ESTAB and pid
                and _port(lport) == client_port and _addr(laddr) == client_ip):
            return pid
    return None


# ── PID → owner ───────────────────────────────────────────────────
# ★★ 关键(实测 + Windows 安全模型):令牌读取【跨用户会失败】。
#    标准用户的主令牌 DACL 只授本人 + SYSTEM 的 TOKEN_QUERY;低权限网关(ai-mem)
#    读不到另一个低权限账户(ai-asset)的令牌 → 令牌法对【正是要拦的调用方】返回 None,
#    且对可信的人类账户也返回 None(两者都读不到 → 无法区分)。
#    ∴ 跨用户身份【必须走 WMI GetOwner】(WMI 服务以 SYSTEM 执行,任意进程 owner 可得,
#    本地 Authenticated Users 有 Execute-Methods 权 → 低权限网关也能调)。令牌法仅作
#    【同用户快路径】(如 ai-mem 自己的 memory-service 调网关)。
_owner_cache: dict = {}       # pid -> (result, expiry)
_CACHE_TTL = 15.0             # 短 TTL:压 PID 复用被另一账户占用的窗口(安全 > 性能)


def _owner_via_token(pid: int) -> Optional[Tuple[str, str]]:
    """同用户快路径。跨用户会因令牌 DACL 失败返回 None(见上)。"""
    h = _k32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h:
        return None
    try:
        tok = W.HANDLE()
        if not _adv.OpenProcessToken(h, TOKEN_QUERY, C.byref(tok)):
            return None
        try:
            size = W.DWORD(0)
            _adv.GetTokenInformation(tok, _TokenUser, None, 0, C.byref(size))
            if size.value == 0:
                return None
            info = (C.c_byte * size.value)()
            if not _adv.GetTokenInformation(tok, _TokenUser, info, size, C.byref(size)):
                return None
            # TOKEN_USER { SID_AND_ATTRIBUTES User } ; SID_AND_ATTRIBUTES 首字段 = PSID
            sid_ptr = C.cast(info, C.POINTER(C.c_void_p))[0]
            name = C.create_unicode_buffer(256)
            dom = C.create_unicode_buffer(256)
            ncb = W.DWORD(256)
            dcb = W.DWORD(256)
            use = W.DWORD()
            if _adv.LookupAccountSidW(None, sid_ptr, name, C.byref(ncb),
                                      dom, C.byref(dcb), C.byref(use)):
                return (f"{dom.value}\\{name.value}", name.value)
            return None
        finally:
            _k32.CloseHandle(tok)
    finally:
        _k32.CloseHandle(h)


def _owner_via_wmi(pid: int) -> Optional[Tuple[str, str]]:
    """跨用户 owner(WMI GetOwner · SYSTEM 代理)。~100ms,故按 PID 缓存。"""
    ps = (f'$o=Get-CimInstance Win32_Process -Filter "ProcessId={pid}" | '
          f'Invoke-CimMethod -MethodName GetOwner; '
          f'if($o.ReturnValue -eq 0){{ "$($o.Domain)\\$($o.User)" }}')
    try:
        r = subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command", ps],
            capture_output=True, text=True, timeout=6,
            creationflags=0x08000000,  # CREATE_NO_WINDOW
        )
    except Exception:
        return None
    out = (r.stdout or "").strip()
    if out and "\\" in out:
        _, user = out.rsplit("\\", 1)
        return (out, user)
    return None


def pid_to_account(pid: int, *, cheap_only: bool = False) -> Optional[Tuple[str, str]]:
    """PID → (账户名 'DOMAIN\\user', user)。令牌快路径(同用户)→ WMI(跨用户)。按 PID 短缓存。

    ★ `cheap_only`:只走令牌快路径,**不碰 WMI**。诊断那一趟用它 ——
      WMI 最坏要等 6 秒(subprocess timeout),而为了写一行日志再等 6 秒是本末倒置。
    """
    now = time.time()
    cached = _owner_cache.get(pid)
    if cached and cached[1] > now:
        return cached[0]
    if cheap_only:
        return _owner_via_token(pid)          # ★ 不写缓存:它不是完整答案,写进去会污染下一次真查询
    res = _owner_via_token(pid) or _owner_via_wmi(pid)
    _owner_cache[pid] = (res, now + _CACHE_TTL)
    return res


# ══════════════════════════════════════════════════════════════════════════
#  ★★★★ V32b · 认人失败要**说得出断在哪一环**(用户裁定 2026-08-10)
#
#  ★★★ 在这之前,这条链的每一个失败出口都是**一句 `return None`**,其中一条还是
#    **裸 `except Exception`**。而 `gateway.classify_caller` 每个身份分支都是
#    `if ident and …` ⇒ 拿不到 ident 就静默落到最低档。
#    ⇒ **认人抖一下,机主本人的对话就会突然降档,而屏幕上没有任何地方告诉他为什么。**
#    他看到的是:拿不到 E1 的放行按钮、拿不到 S2 正文,而这一切"看起来只是不太好用"。
#
#  ★★ 判词:**给错原因的提示比不给提示更坏,而【没有原因】也是一种给错。**
#    「没查出来」与「查出来了但不在册」今天长得**一模一样**,而它们的正确处置完全相反:
#      · 查得出来但不在册(访客 Alle / CodexSandbox*)—— 这是**稳定**的事实,按策略办;
#      · 根本没查出来 —— 这是**故障**,而且被降档的**可能就是机主自己**。
#
#  ⇒ 每个失败出口都有**名字**,并把断点带出去。下面这些常量是**跨模块约定**:
#    网关按它计数/记日志,界面按它说话。★ 用 ASCII,理由同 ASSERTION-PITFALLS 第 8 条。
# ══════════════════════════════════════════════════════════════════════════
STEP_OK = "ok"
#: 出口①-a:根本不在 Windows 上 —— 这条链**结构上不存在**,不是抖动。
STEP_NOT_WINDOWS = "not-windows"
#: 出口①-b:拿不到对端 ip/port(request.client 是 None,或端口为 0)。
STEP_NO_PEER_ADDR = "no-peer-addr"
#: 出口②:端口表里找不到那条 ESTABLISHED 行(端口被回收 / 进程已退出 / 表抖动)。
STEP_PID_LOOKUP = "pid-lookup"
#: 出口③:PID 有了,但令牌与 WMI 都拿不到 owner(跨用户 DACL / WMI 超时)。
STEP_OWNER_LOOKUP = "owner-lookup"
#: 出口④:**那条裸 except 的去处**。现在它带着异常类型与消息,不再一声不吭。
STEP_EXCEPTION = "exception"

#: 全部失败出口。★ 反向全表用:新增一个出口而不登记 ⇒ 网关那侧的穷举断言判红。
FAILURE_STEPS = (STEP_NOT_WINDOWS, STEP_NO_PEER_ADDR, STEP_PID_LOOKUP,
                 STEP_OWNER_LOOKUP, STEP_EXCEPTION)


class Resolution(NamedTuple):
    """认人的结果。★ `account is None` ⇔ 认人**失败**,而 `step` 说清断在哪一环。"""

    account: Optional[Tuple[str, str]]
    step: str
    detail: str

    @property
    def ok(self) -> bool:
        return self.account is not None


def resolve_account_detailed(client_ip: Optional[str], client_port: Optional[int],
                             *, cheap_only: bool = False) -> Resolution:
    """回环调用方 → `Resolution`。★ 每个 `return` 都带着**断在哪一环**。

    ★★ 与旧的 `resolve_account` 的区别只有一个:它不再把失败**压平成 None**。
      行为(谁能被解析成谁)一个字节都没变 —— 变的是失败**说不说得出话**。
    """
    if not _WIN:
        return Resolution(None, STEP_NOT_WINDOWS, "非 Windows:端口→进程→账户这条链不存在")
    if not client_ip or not client_port:
        return Resolution(None, STEP_NO_PEER_ADDR,
                          f"拿不到对端地址(ip={client_ip!r} port={client_port!r})")
    # ★ 两段**各自**catch,而不是外面套一个裸 except —— 一个笼统的 except 抓到之后
    #   就再也说不出是端口表炸了还是令牌读炸了,而这两件事的下一步完全不同。
    try:
        pid = resolve_peer_pid(client_ip, client_port)
    except Exception as e:                                   # noqa: BLE001
        return Resolution(None, STEP_PID_LOOKUP, f"{type(e).__name__}: {e}")
    if pid is None:
        return Resolution(None, STEP_PID_LOOKUP,
                          f"端口表里没有 {client_ip}:{client_port} 的 ESTABLISHED 行")
    try:
        acct = pid_to_account(pid, cheap_only=cheap_only)
    except Exception as e:                                   # noqa: BLE001
        return Resolution(None, STEP_OWNER_LOOKUP, f"pid={pid} {type(e).__name__}: {e}")
    if acct is None:
        return Resolution(None, STEP_OWNER_LOOKUP,
                          f"pid={pid}:令牌"
                          + ("(只走了快路径)" if cheap_only else "与 WMI 都")
                          + "拿不到 owner")
    return Resolution(acct, STEP_OK, "")


def resolve_account(client_ip: Optional[str], client_port: Optional[int]) -> Optional[Tuple[str, str]]:
    """回环调用方 → (full_name 'DOMAIN\\user', user)。任一步失败 → None(调用方决定策略)。

    ★ 保留原签名与原语义,内部改走 `resolve_account_detailed` ——
      **那条裸 `except Exception` 已经没有了**,异常按环归位并带着类型与消息。
    """
    try:
        return resolve_account_detailed(client_ip, client_port).account
    except Exception:                                        # noqa: BLE001
        # ★ 兜底:本函数的**契约是绝不抛**(调用方在请求路径上)。但兜底不等于失忆 ——
        #   走到这儿说明 `resolve_account_detailed` 自己漏了一环,那是本文件的 bug。
        #   诊断那一趟(`diagnose_from_request`)会把它记成 STEP_EXCEPTION。
        return None


def account_from_request(request) -> Optional[Tuple[str, str]]:
    """从 Starlette/FastAPI request 取客户端 (ip, port) 并解析。

    ★★ 这个函数名是**测试替身挂载点**(test_caller_policy / test_gpu_policy 都 monkeypatch 它)。
      故意保持它是"账户或 None"的形状不变 —— 换掉它会让那些替身静默失效,
      而失效的方向是"测试照样绿",那正是本仓最恨的形状。
    """
    cli = getattr(request, "client", None)
    if not cli:
        return None
    return resolve_account(getattr(cli, "host", None), getattr(cli, "port", None))


def diagnose_from_request(request) -> Resolution:
    """**只在 `account_from_request` 已经失败之后**调用:回答"断在哪一环"。

    ★★ 不重跑 WMI(`cheap_only=True`):那条路最坏等 6 秒,为了写一行日志再等 6 秒
      是本末倒置。端口表那一步是纯内存读,重跑它足以把
      「没找到 PID」与「找到了 PID 但读不到 owner」分开 —— 而这正是要分的那一刀。
    ★ 代价明写:owner 那一环在这里只走令牌快路径,所以它报 `owner-lookup` 时
      **不能**断言"WMI 也失败了"(第一趟才是完整的那次)。detail 里说了这一点。
    """
    cli = getattr(request, "client", None)
    if not cli:
        return Resolution(None, STEP_NO_PEER_ADDR, "request.client 是 None(拿不到对端地址)")
    try:
        return resolve_account_detailed(getattr(cli, "host", None),
                                        getattr(cli, "port", None), cheap_only=True)
    except Exception as e:                                   # noqa: BLE001
        # ★ 出口④:**这就是那条裸 except 的去处**。它现在带着类型与消息出去,
        #   而不是把一次故障压成"这个人没登记"。
        return Resolution(None, STEP_EXCEPTION, f"{type(e).__name__}: {e}")
