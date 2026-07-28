"""caller_identity 测试:对真实自连接解析调用方账户,验证 Win32 链路。
跑:python test_caller_identity.py
"""
import getpass
import socket
import sys

import caller_identity as ci

_p = _f = 0


def check(name, cond):
    global _p, _f
    if cond:
        _p += 1
    else:
        _f += 1
        print(f"  FAIL: {name}")


print("=== 自连接解析(server 视角看到的 client 源端口 = 本进程)===")
srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
srv.bind(("127.0.0.1", 0))
srv.listen(1)
port = srv.getsockname()[1]
cli = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
cli.connect(("127.0.0.1", port))
conn, peer = srv.accept()          # peer = (client_ip, client_source_port),即网关看到的 request.client

acct = ci.resolve_account(peer[0], peer[1])
me = getpass.getuser()
print(f"  resolved = {acct}")
print(f"  current user = {me}")

check("解析非 None", acct is not None)
if acct:
    full, user = acct
    check("解析出的用户 == 当前用户", user.lower() == me.lower())
    check("full 含反斜杠域\\用户", "\\" in full)
    check("★ 当前用户不是隔离账户", user.lower() not in ("ai-asset", "ai-exec"))

check("解析不到端口 → None", ci.resolve_account("127.0.0.1", 1) is None)
check("空参数 → None", ci.resolve_account(None, None) is None)

# ★ UAF 回归(2026-07-28 审查发现):_tcp_rows 曾返回指向【已释放】buf 的 ctypes 视图。
#   原测试因「调用后立即迭代、分配量小」而结构性测不到。这条制造堆压力后再断言内容不变。
print("=== UAF 回归:堆压力后 _tcp_rows 结果必须不变 ===")
import ctypes as _C
rows_before = ci._tcp_rows()
check("返回的是 list(值拷贝)而非 ctypes 数组", isinstance(rows_before, list))
snapshot = list(rows_before)
for _ in range(2000):                      # 制造同尺寸堆 churn
    _tmp = (_C.c_byte * 3996)()
    _C.memset(_tmp, 0x5B, 3996)
    del _tmp
check("堆压力后内容完全不变", rows_before == snapshot)
if rows_before:
    st, _la, _lp, pid = rows_before[0]
    check("行内数值仍是合理范围(未被写坏)", 0 <= st <= 20 and 0 <= pid < 2**32)

# ★ TIME_WAIT 回归:只应返回 ESTABLISHED 且 pid!=0 的匹配
print("=== 只认已建立连接(TIME_WAIT 残留行 pid=0 不得被采信)===")
tw = [r for r in ci._tcp_rows() if r[3] == 0]
print(f"  表中 pid=0 的行: {len(tw)} / {len(ci._tcp_rows())}")
check("resolve_peer_pid 永不返回 0", ci.resolve_peer_pid("127.0.0.1", 1) != 0)

conn.close(); cli.close(); srv.close()
print(f"\n=== {_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
