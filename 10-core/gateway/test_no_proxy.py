"""源码级防回归:网关里的 httpx 客户端必须显式 trust_env=False。纯 assert:python test_no_proxy.py

★ 为什么要一条源码扫描测试(2026-07-31 审计):
  httpx 的默认是 trust_env=True —— 它会读 HTTP_PROXY / HTTPS_PROXY / 系统代理设置。
  网关转发的是整包 system + 全部历史(含解封后的记忆正文),本该只走 127.0.0.1:8080 回环;
  一旦有人设了代理环境变量,这些明文就被改道到外网。回环不需要代理,必须关掉。
  缺省值是 denylist 形状 —— 下一个新写的 httpx 客户端会默认信任环境,所以用源码扫描钉死。
"""

import pathlib
import re

_pass = _fail = 0


def check(name, cond, extra=""):
    global _pass, _fail
    if cond:
        _pass += 1
    else:
        _fail += 1
        print(f"  FAIL  {name}{(' — ' + extra) if extra else ''}")


HERE = pathlib.Path(__file__).parent
# ★ 平衡括号提取:httpx.AsyncClient(timeout=httpx.Timeout(...), trust_env=False) 里有嵌套的 ),
#   非贪婪正则会停在第一个 ) 上、漏掉 trust_env=False。这里手动数括号找到真正的收尾。
open_pat = re.compile(r"httpx\.(?:Async)?Client\(")


def client_args(src):
    for m in open_pat.finditer(src):
        i = m.end()
        depth = 1
        while i < len(src) and depth:
            if src[i] == "(":
                depth += 1
            elif src[i] == ")":
                depth -= 1
            i += 1
        yield src[m.end():i - 1]


scanned = 0
for p in sorted(HERE.glob("*.py")):
    if p.name.startswith("test_"):
        continue
    src = p.read_text(encoding="utf-8")
    for args in client_args(src):
        scanned += 1
        check(f"{p.name}: httpx 客户端显式 trust_env=False",
              "trust_env=False" in args,
              "系统代理/HTTP_PROXY 会把回环请求改道到外网,把整包 prompt+记忆明文送出去")

check("至少扫到一个 httpx 客户端(否则正则失效了)", scanned >= 1)
print(f"=== 无代理信任:{_pass} PASS · {_fail} FAIL ===")
raise SystemExit(1 if _fail else 0)
