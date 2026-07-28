"""repo.py 测试(不需要活库的部分)。跑:python test_repo.py

分两段:
  A. 逻辑与架构约束 —— 不连库,任何时候都能跑
  B. 活库集成 —— 需要能以 ai_mem_local 连上 PG;连不上则跳过并明确说明(不静默假通过)
"""
import inspect
import re
import sys
from pathlib import Path

import repo
from repo import RepoError, _sanitize, FactWrite
from tainted import seal, TaintedText, MemoryLeakError

_p = _f = _s = 0
SECRET = "我妹妹叫小雨CANARY7Q4X"


def check(name, cond, extra=""):
    global _p, _f
    if cond:
        _p += 1
    else:
        _f += 1
        print(f"  FAIL: {name} {extra}")


def skip(name, why):
    global _s
    _s += 1
    print(f"  SKIP: {name} — {why}")


print("=== 1. ★ 数据库异常净化(三层都拦不住的那条泄漏路径)===")


class FakePgError(Exception):
    """模拟 psycopg 异常:PostgreSQL 会把整行正文塞进 detail。"""
    def __init__(self, sqlstate, msg, constraint=None):
        self.sqlstate = sqlstate
        super().__init__(msg)
        class D:
            pass
        self.diag = D()
        self.diag.constraint_name = constraint
        self.diag.message_detail = f"Failing row contains (12, {SECRET}, user_typed)."


e = FakePgError("23505",
                f"duplicate key value violates unique constraint \"idx_l3_lookup\"\n"
                f"DETAIL:  Key (subject_norm, predicate_norm)=(我, 妹妹) already exists.\n"
                f"Failing row contains (12, {SECRET}, ...).",
                constraint="idx_l3_lookup")
r = _sanitize(e)
check("净化后是 RepoError", isinstance(r, RepoError))
check("★ 净化后不含正文", SECRET not in str(r), str(r))
check("★ 净化后不含被泄露的键值「我, 妹妹」", "妹妹" not in str(r), str(r))
check("保留了 sqlstate", r.sqlstate == "23505")
check("保留了约束名(排查要用)", "idx_l3_lookup" in str(r))
check("给了人能懂的提示", "已存在" in str(r))

# 触发器抛的中文消息也可能带旧值片段
e2 = FakePgError("23514", f"记忆内容不可覆盖(§4.5):列 statement 试图从 {SECRET} 改为 别的")
r2 = _sanitize(e2)
check("★ 触发器消息里的正文也被滤掉", SECRET not in str(r2), str(r2))

# 未知 sqlstate 不得原样透传消息
e3 = FakePgError("XX999", f"some internal error with {SECRET}")
r3 = _sanitize(e3)
check("★ 未知错误也不透传正文", SECRET not in str(r3), str(r3))

print("=== 2. ★ 架构约束:写入必须收敛到 repo ===")
mem_dir = Path(__file__).resolve().parent
offenders = []
for py in mem_dir.glob("*.py"):
    if py.name in ("repo.py",) or py.name.startswith("test_"):
        continue
    src = py.read_text(encoding="utf-8")
    if re.search(r"\bimport psycopg\b|\bfrom psycopg\b", src):
        offenders.append(py.name)
check("★ 只有 repo.py 直接 import psycopg", not offenders, f"违规: {offenders}")

route_src = (mem_dir / "route.py").read_text(encoding="utf-8")
check("route.py 不 import repo(选路是纯函数)", "import repo" not in route_src)
tainted_src = (mem_dir / "tainted.py").read_text(encoding="utf-8")
check("tainted.py 不依赖 repo(类型层在最底)", "import repo" not in tainted_src)

print("=== 3. ★ 正文必须是 TaintedText,裸 str 要被拒 ===")
w_bad = FactWrite(statement="裸字符串", subject_norm="我", predicate_norm="妹妹",
                  object_text=seal("小雨", sensitivity="S0", source="user_typed"),
                  provenance="user_typed", source_confidence=1.0,
                  sensitivity_domain="S0", attestation_kind="panel_ticket")
try:
    repo.insert_fact(None, w_bad)
    check("裸 str 正文被拒", False, "竟然没拒")
except TypeError as ex:
    check("裸 str 正文被拒", "TaintedText" in str(ex))
except Exception as ex:
    check("裸 str 正文被拒", False, f"→ {type(ex).__name__}: {ex}")

print("=== 4. provenance 必须在封闭枚举内 ===")
w_bad2 = FactWrite(statement=seal("x", sensitivity="S0", source="u"),
                   subject_norm="我", predicate_norm="妹妹",
                   object_text=seal("小雨", sensitivity="S0", source="u"),
                   provenance="我自己编的来源", source_confidence=1.0,
                   sensitivity_domain="S0")
try:
    repo.insert_fact(None, w_bad2)
    check("非法 provenance 被拒", False, "竟然没拒")
except RepoError as ex:
    check("非法 provenance 被拒", "provenance" in str(ex))

print("=== 5. 服务端时间(§4.11.3 客户端时钟不可信)===")
src = inspect.getsource(repo)
# ★ 检查【实际字段集】而不是源码文本 —— 文本匹配会把文档字符串算进去(上一版就栽在这)
fields = set(FactWrite.__dataclass_fields__)
check("FactWrite 无 asserted_at 字段(调用方无法提供⇒无法伪造)", "asserted_at" not in fields, f"{fields}")
check("FactWrite 无 write_seq 字段(由 DB 触发器分配)", "write_seq" not in fields, f"{fields}")
check("FactWrite 无 confidence 字段(由服务端从 source_confidence 导出)",
      "confidence" not in fields, f"{fields}")
check("insert 用 _server_now()", "_server_now()" in src)

print("=== 6. 写路径里不存在改内容列的函数(§4.5)===")
upd = re.findall(r"UPDATE\s+mem\.\w+\s+SET\s+([^\n]+)", src, re.IGNORECASE)
# 白名单里的列都不是记忆内容:
#   superseded_by / redacted_at —— 冲突处理与 tombstone(两者在 DB 层均为单向)
#   vector_point_id            —— 索引指针;DB 层禁止「非空→另一个非空」的重指,
#                                 因为它是 tombstone 删向量时定位点的唯一依据
bad_updates = [u for u in upd
               if not re.match(r"^\s*(superseded_by|redacted_at|vector_point_id)\s*=", u)]
check("★ 所有 UPDATE 只动 superseded_by / redacted_at / vector_point_id",
      not bad_updates, f"→ {bad_updates}")
check("有 supersede 函数", hasattr(repo, "supersede"))
check("有 redact(tombstone)函数", hasattr(repo, "redact"))
check("★ 没有任何名字像 update_fact/edit_fact 的函数",
      not any(n in src for n in ("def update_fact", "def edit_fact", "def modify_fact")))

print("=== 7. from None:切断异常链,防原异常经 traceback 泄露 ===")
check("connect 用了 from None", "raise _sanitize(e) from None" in src)
check("★ 每个 except psycopg.Error 都 from None",
      src.count("from None") >= src.count("except psycopg.Error"))

print("=== 8. 活库集成 ===")
try:
    conn = repo.connect()
except Exception as ex:
    conn = None
    skip("活库集成", f"连不上 PG({type(ex).__name__})—— 需以 ai-mem 身份运行(SSPI)")

if conn:
    try:
        w = FactWrite(
            statement=seal("REPOTEST 我妹妹叫小雨", sensitivity="S0", source="user_typed"),
            subject_norm="repotest_我", predicate_norm="妹妹",
            object_text=seal("小雨", sensitivity="S0", source="user_typed"),
            provenance="user_typed", source_confidence=1.0,
            sensitivity_domain="S0", attestation_kind="panel_ticket")
        fid = repo.insert_fact(conn, w)
        conn.commit()
        check("写入成功", isinstance(fid, int))
        rows = repo.find_facts(conn, "repotest_我", "妹妹")
        check("查得到", len(rows) == 1)
        if rows:
            row = rows[0]
            check("★ 返回的正文是密封的", isinstance(row.statement, TaintedText))
            check("★ object 取的是 object 列不是 subject_norm",
                  row.object_text == seal("小雨", sensitivity="S0", source="user_typed"))
            tr = row.trace()
            check("溯源六件套齐全",
                  all(k in tr for k in ("asserted_at", "confidence", "source_ref",
                                        "origin_device_id", "write_seq", "provenance")))
            check("write_seq 由服务端分配(>0)", tr["write_seq"] > 0)
        # 清理
        with conn.cursor() as cur:
            cur.execute("DELETE FROM mem.l3_fact WHERE subject_norm='repotest_我'")
        conn.commit()
    finally:
        conn.close()

print(f"\n=== {_p} PASS · {_f} FAIL · {_s} SKIP ===")
sys.exit(1 if _f else 0)
