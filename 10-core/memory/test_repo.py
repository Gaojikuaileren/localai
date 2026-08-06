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
from tainted import seal, TaintedText, MemoryLeakError, unseal_for_client, CallerTier

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
# ★ 判据同时看【表】和【列】,而不是只看列(S3 改进)。
#   原来的写法是一张扁平的列白名单,S3 加了队列/票据/熔断三张运维表之后,
#   它开始把合法的状态机 UPDATE 也算成违规 —— 说明判据本身丢了信息。
#   规则的原意是「**记忆内容**不可覆盖」,所以判据应当是:
#     · 记忆内容表  → 只许动那三列(supersede / tombstone / 向量指针)
#     · 运维表      → 随便动,但表名必须在册 —— 新加一张表要在这里被看见
CONTENT_TABLES = {"l1_session_summary", "l2_episode", "l3_fact",
                  "entity_person", "entity_event", "entity_preference", "entity_project",
                  "entity_device", "entity_place", "entity_thing"}
# 运维表:不承载记忆内容,状态本来就要变
OPS_TABLES = {"pending_review", "write_ticket", "circuit_breaker", "gate_rejection"}
CONTENT_OK = ("superseded_by", "redacted_at", "vector_point_id")

upd = re.findall(r"UPDATE\s+mem\.(\w+)\s+SET\s+([^\n]+)", src, re.IGNORECASE)
bad_updates, unknown_tables = [], []
for tbl, setclause in upd:
    if tbl in CONTENT_TABLES:
        if not re.match(r"^\s*(%s)\s*=" % "|".join(CONTENT_OK), setclause):
            bad_updates.append(f"{tbl}: {setclause.strip()}")
    elif tbl not in OPS_TABLES:
        unknown_tables.append(tbl)      # 既不是内容表也不在运维表清单里 —— 必须有人看一眼

check("★ 记忆内容表的 UPDATE 只动 superseded_by / redacted_at / vector_point_id",
      not bad_updates, f"→ {bad_updates}")
check("★ 没有对未登记表的 UPDATE(新表必须先进清单)",
      not unknown_tables, f"→ {sorted(set(unknown_tables))}")
check("判据确实区分了内容表与运维表(否则本测试退化成扁平列表)",
      len(CONTENT_TABLES & OPS_TABLES) == 0 and len(OPS_TABLES) > 0)
check("有 supersede 函数", hasattr(repo, "supersede"))
check("有 redact(tombstone)函数", hasattr(repo, "redact"))
check("★ 没有任何名字像 update_fact/edit_fact 的函数",
      not any(n in src for n in ("def update_fact", "def edit_fact", "def modify_fact")))

print("=== 7. from None:切断异常链,防原异常经 traceback 泄露 ===")
check("connect 用了 from None", "raise _sanitize(e) from None" in src)
check("★ 每个 except psycopg.Error 都 from None",
      src.count("from None") >= src.count("except psycopg.Error"))

print("=== 7b. ★★ 只读护栏【本身】要被钉住(D91:不记技术债,当场补)===")
# ══════════════════════════════════════════════════════════════════════════
#  形状照抄 test_route.py:98-100 —— 那里用 inspect.getsource 扫 route.py,
#  断言它连 psycopg / connect( 都不许出现。**但两者的强度完全不同**:
#    · route.py 是**结构性**切断:导入闭包止于标准库,socket 物理上不可能存在;
#    · 本文件不是。psycopg 早就 import 进来了(repo.py:32),
#      §8 能进自动门禁靠的只是「connect 包在 try 里、失败即 skip」这一条**代码分支**。
#
#  ★★★ 代码分支不是被断言的性质。有人把那个 try 去掉,今天**不会有任何东西变红**,
#    而门禁从此每跑一次都真的去拨生产记忆库。这条断言就是补上那个缺口。
#    (3 号如实指出了这个洞并建议记技术债;协调层裁定不记债 —— 在同一次提交里补上。
#     "后果轻一点"不是护栏。)
#
#  ★ needle 一律**拼接构造**,不写成整串字面量 —— 否则这段代码自己会被算进 count,
#    那是 ASSERTION-PITFALLS 第 1 条(已踩 9 次):断言撞在描述它自己的那行字上。
# ══════════════════════════════════════════════════════════════════════════
_self = Path(__file__).read_text(encoding="utf-8")
_conn_call = "repo." + "connect()"
_sec8 = _self[_self.rindex("=== 8. " + "活库集成"):]
check("★ 全文件只有一处 " + _conn_call + " —— 多一处就多一条绕过护栏的路",
      _self.count(_conn_call) == 1, _self.count(_conn_call))
check("★★★ 那一处必须包在 try 里(这是本套件唯一的只读护栏)",
      "try" + ":" in _sec8[:_sec8.index(_conn_call)])
check("★★★ 连不上要走 skip() 而不是抛 —— 在门禁里连不上是【常态】,不是失败",
      "skip" + "(" in _sec8 and "except " + "Exception as ex:" in _sec8)
check("★★ 活库段整体由 if conn 守着(connect 失败之后不得继续往下写库)",
      "if " + "conn:" in _sec8)

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
            # ★ 不能再用 `==` 比内容 —— TaintedText.__eq__ 已改为比句柄
            #   (内容比较是一个不留痕迹的猜测-确认预言机,见 tainted.py)。
            #   要比内容就显式解封,让它记账。
            check("★ object 取的是 object 列不是 subject_norm",
                  unseal_for_client(row.object_text, caller=CallerTier.TRUSTED_LOCAL) == "小雨")
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
