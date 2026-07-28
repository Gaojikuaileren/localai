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

conn.close(); cli.close(); srv.close()
print(f"\n=== {_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
