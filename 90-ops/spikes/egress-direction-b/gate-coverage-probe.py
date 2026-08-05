"""闸一(_ALLOWED_CALLERS)的断言覆盖探针 —— 一次性勘察,不进门禁。

服务对象:00-docs/decision-packets/egress-b-gates-impact-2026-08-06.md

★★ 纪律:本脚本**只读** 10-core/memory/tainted.py,一个字节都不改它。
   要证明的是「把 channel-relay 加进 _ALLOWED_CALLERS['S0'] 不会让任何断言变红」,
   而证明它的正确方式**不是**去真的改那个文件(哪怕改回来)——
   那会污染一条安全判据的实测值。这里的做法是:
     ① 正常 import tainted(读磁盘上的真实定义);
     ② 只在**本进程内存里**替换 tainted._ALLOWED_CALLERS(磁盘文件不变);
     ③ 把 test_tainted.py 里守这张表的那两条断言**逐字抄过来**,对着被改过的表跑;
     ④ 看它们红不红。
   ②这一步不落盘、不留痕、进程退出即消失 —— 与「本地改了再改回来」有本质区别:
   后者会经过一个磁盘上真的错了的瞬间,而且可能被别的进程/测试读到。

跑:python gate-coverage-probe.py        (不需要 venv,tainted.py 无外部依赖)
"""
import sys
from pathlib import Path

# ★ 本机控制台默认 GBK,直接 print「⇒」会 UnicodeEncodeError 当场炸掉(实测)。
#   这是本轮第三次撞同一族编码坑(.cmd 的 GBK/UTF-8 · 系统工具的 OEM 输出 · 这里)。
#   一律钉成 UTF-8,并且 errors="replace" —— 宁可显示成问号,也不许因为编码而中断勘察。
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# 路径运行期推导 —— 代码里不写死盘符(§11.1 路径契约)
REPO = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(REPO / "10-core" / "memory"))

import tainted                                            # noqa: E402
from tainted import CallerTier, MemoryLeakError, seal, unseal_for_client   # noqa: E402

FAILED = []


def check(label, cond):
    print(f"  {'PASS' if cond else 'FAIL'}  {label}")
    if not cond:
        FAILED.append(label)
    return cond


def banner(s):
    print()
    print("=" * 70)
    print(s)
    print("=" * 70)


# ── test_tainted.py 里守这张表的两条断言(逐字抄,只把 check 换成本地的)──────
def assertion_A(allowed):
    """test_tainted.py:150-153 ——『新增档位默认无权(allowlist 形状)』"""
    return all(tier not in allowed.get("S2", frozenset())
               for tier in (CallerTier.LAN_DEVICE, CallerTier.CHANNEL_RELAY,
                            CallerTier.REMOTE_UNAUTH))


def assertion_B(allowed):
    """test_tainted.py:160-163 ——『resident-observer / ext-operator 不出现在任何 allowlist 里』"""
    return all(tier not in tiers
               for tiers in allowed.values()
               for tier in tainted.NO_PLAINTEXT_TIERS)


banner("① 磁盘上的真实定义(基线)")
real = dict(tainted._ALLOWED_CALLERS)
for sens in ("S0", "S1", "S2"):
    print(f"  {sens}: {sorted(c.value for c in real.get(sens, frozenset()))}")
check("基线:断言 A 通过", assertion_A(real))
check("基线:断言 B 通过", assertion_B(real))


banner("② 把 channel-relay 塞进 S0(只在内存里)—— 断言会不会变红?")
mutated = dict(real)
mutated["S0"] = frozenset(real["S0"] | {CallerTier.CHANNEL_RELAY})
print(f"  改后 S0: {sorted(c.value for c in mutated['S0'])}")
a, b = assertion_A(mutated), assertion_B(mutated)
check("★ 断言 A 仍然通过(它只查 S2 ⇒ 对 S0 的改动视而不见)", a)
check("★ 断言 B 仍然通过(它只覆盖 resident-observer / ext-operator)", b)
print()
if a and b:
    print("  ⇒ 结论:把全系统最低信任档加进 S0,**没有任何一条断言会变红**。")
else:
    print("  ⇒ 结论:有断言变红 —— 与决议包的说法不符,须更正决议包。")


banner("③ 同一个洞的其它格:哪些 (档位 × 敏感度) 组合是无人看守的")
tiers = [CallerTier.TRUSTED_LOCAL, CallerTier.LAN_DEVICE, CallerTier.CHANNEL_RELAY,
         CallerTier.REMOTE_UNAUTH, CallerTier.RESIDENT_OBSERVER, CallerTier.EXT_OPERATOR]
print(f"  {'档位':<22}{'S0':<10}{'S1':<10}{'S2':<10}")
unguarded = []
for t in tiers:
    cells = []
    for sens in ("S0", "S1", "S2"):
        m = dict(real)
        m[sens] = frozenset(real.get(sens, frozenset()) | {t})
        # 加进去之后两条断言还全绿 ⇒ 这一格没人看守
        naked = assertion_A(m) and assertion_B(m)
        already = t in real.get(sens, frozenset())
        cells.append("(已在表内)" if already else ("**无人看守**" if naked else "有断言守着"))
        if naked and not already:
            unguarded.append(f"{t.value} × {sens}")
    print(f"  {t.value:<22}{cells[0]:<10}{cells[1]:<10}{cells[2]:<10}")
print()
print("  无人看守的格子:")
for u in unguarded:
    print(f"    · {u}")


banner("④ 真正的后果:改完之后那个档位能取到什么")
s0 = seal("这是一条 S0 记忆的正文", sensitivity="S0", source="user_typed")
try:
    unseal_for_client(s0, caller=CallerTier.CHANNEL_RELAY)
    print("  基线下 channel-relay 取 S0 正文:竟然成功(与预期不符)")
except MemoryLeakError as e:
    print(f"  基线下 channel-relay 取 S0 正文:被拒 ✓")
    print(f"    原文:{e}")

tainted._ALLOWED_CALLERS = mutated      # ★ 只改本进程内存,磁盘文件不动
try:
    got = unseal_for_client(s0, caller=CallerTier.CHANNEL_RELAY)
    print(f"  改后 channel-relay 取 S0 正文:**拿到了** → {got!r}")
    print("    ⇒ 出口是③【回客户端】(unseal_for_client),不是④【进 prompt】。")
    print("      这一点很重要:④ unseal_for_prompt 的签名里**没有 caller 这一维**,")
    print("      所以闸一根本不在 prompt 那条路上 —— 两道闸从不叠加。")
except MemoryLeakError as e:
    print(f"  改后仍被拒:{e}")
finally:
    tainted._ALLOWED_CALLERS = real     # 还原内存态(其实进程就要退出了)


banner("⑤ 闸二那一维:unseal_for_prompt 到底判什么")
import inspect                                            # noqa: E402
sig_client = inspect.signature(tainted.unseal_for_client)
sig_prompt = inspect.signature(tainted.unseal_for_prompt)
print(f"  unseal_for_client{sig_client}")
print(f"  unseal_for_prompt{sig_prompt}")
check("★ unseal_for_client 有 caller 维(闸一在这)", "caller" in sig_client.parameters)
check("★ unseal_for_prompt 有 backend 维(闸二在这)", "backend" in sig_prompt.parameters)
check("★★ unseal_for_prompt **没有** caller 维 ⇒ 闸一不在 prompt 路径上",
      "caller" not in sig_prompt.parameters)
check("★★ unseal_for_client **没有** backend 维 ⇒ 闸二不在回客户端路径上",
      "backend" not in sig_client.parameters)


banner("小结")
if FAILED:
    print("  以下检查为 FAIL —— 说明决议包里的对应说法要改:")
    for f in FAILED:
        print(f"    · {f}")
    sys.exit(1)
print("  全部检查符合决议包的记载。")
print()
print("  ★ 再说一次:本脚本没有修改 10-core/memory/tainted.py。")
print("    第②④步只替换了本进程内存里的 tainted._ALLOWED_CALLERS。")
