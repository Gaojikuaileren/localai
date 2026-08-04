"""架构断言:`gpu.apply@workstation` 永不进入任何 Agent 工具池。纯 assert:python test_gpu_tool_isolation.py

规则出处(三处一字不改地重复,说明它被反复确认过):
  · PROJECT_PLAN_v3.0.md:2288  P4 清单 —— 「`gpu.apply@workstation` 永不进入任何 Agent 工具池(P4 原条款,一字不改)」
  · PROJECT_PLAN_v3.0.md:199-201 —— 「宿主后缀不是参数,是工具身份的一部分…**做成参数它就能被不可信计划者操纵**」
  · DECISIONS.md:1091(D37 改写 P4 时明写「但另一半一个字不改」)

★★ 为什么现在写、而不是等工具池建出来再写:

  今天这条规则是**真空成立**的 —— `config/tools.toml` 不存在,`10-core/mcp-tools/{http,stdio}` 是空目录,
  所以没有任何工具池可违反它。但也**没有任何断言会在工具池被建出来的那一刻变红**。
  ⇒ 规则活在文档里,而新增注册表的那次提交不会被拦。晚一天写,它就永远是纸面条款。

  这与本项目反复吃过的亏同形:`require_trusted_local` 写好了至今无人调用 ·
  `backend_of()` fail-closed 却零生产调用点 —— 前瞻脚手架不会自己长出执行力。
  区别在于:**那两个是「写了没人用」,这一条是「不写就没人拦」**,后者的代价发生在未来某次提交里。

★ 断言分两层,第二层才是承重的:

  第一层(内容):已知的工具声明里不得出现 gpu.* —— 工具池建出来后才有东西可查。
  第二层(**反向全表**):**扫描根清单本身**要被钉住。今天扫到 0 个声明文件,这件事必须被
  显式断言并打印出来 —— 否则将来注册表落在扫描根【之外】时,第一层会继续"通过",
  而它其实什么都没查。事故的形状永远是「新加的那个不在清单里」。
"""

import re
import sys
import tomllib
from pathlib import Path

_HERE = Path(__file__).resolve().parent
_REPO = _HERE.parents[1]

_pass = _fail = 0


def check(name, cond, extra=""):
    global _pass, _fail
    if cond:
        _pass += 1
    else:
        _fail += 1
        print(f"  FAIL  {name} {extra}")


# ── 扫描根:工具声明【只允许】出现在这些地方 ────────────────────────────
#   新增一处必须改这里,而改这里会经过 code review —— 这就是这份清单的全部作用。
SCAN_ROOTS = [
    _REPO / "config" / "tools.toml",        # D68:层一工具池的**权威**表(两层 MCP 决议包 §142-143)
    _REPO / "10-core" / "mcp-tools",        # DD-2 的投喂端(stdio / HTTP 分离);今天是空目录
]

# 被禁止出现在任何 Agent 工具池里的工具 id 前缀 / 全名。
FORBIDDEN_PREFIXES = ("gpu.",)
FORBIDDEN_EXACT = ("gpu.apply@workstation",)

# 「看起来像一份工具声明」的文件名特征 —— 用于第二层反向全表。
_LOOKS_LIKE_TOOLS = re.compile(r"(^|[-_.])tools?\.(toml|json|ya?ml)$", re.I)

_SKIP_DIRS = {".git", "__pycache__", "node_modules", "obj", "bin", ".vs", "dist"}


def _iter_repo_files():
    for p in _REPO.rglob("*"):
        if not p.is_file():
            continue
        if any(part in _SKIP_DIRS for part in p.parts):
            continue
        yield p


def _under_a_root(p: Path) -> bool:
    for r in SCAN_ROOTS:
        if p == r:
            return True
        try:
            p.relative_to(r)
            return True
        except ValueError:
            continue
    return False


print("=== 1. 规则本身还在文档里(有人悄悄删掉规则,这里要响)===")
_plan = (_REPO / "00-docs" / "PROJECT_PLAN_v3.0.md").read_text(encoding="utf-8")
check("PLAN 仍写着「gpu.apply@workstation 永不进入任何 Agent 工具池」",
      "gpu.apply@workstation" in _plan and "永不进入任何 Agent 工具池" in _plan)
check("PLAN 仍写着「宿主后缀不是参数,是工具身份的一部分」(做成参数就能被操纵)",
      "宿主后缀不是参数" in _plan)

print("=== 2. ★★ 反向全表:工具声明只能落在已登记的扫描根里 ===")
#   这一条才是承重的。它管的不是"现有工具池对不对",而是
#   "有没有一份工具池落在我根本没看的地方"。
_strays = []
for p in _iter_repo_files():
    if p.suffix.lower() not in (".toml", ".json", ".yaml", ".yml"):
        continue
    if not _LOOKS_LIKE_TOOLS.search(p.name):
        continue
    if not _under_a_root(p):
        _strays.append(str(p.relative_to(_REPO)))
check(f"没有工具声明落在扫描根之外(实测 strays={_strays})", not _strays,
      "新增的工具注册表必须登记进 SCAN_ROOTS,否则下面那层内容检查形同虚设")

print("=== 3. 已登记扫描根里的工具声明,不得出现 gpu.* ===")
_decls = []
for r in SCAN_ROOTS:
    if r.is_file():
        _decls.append(r)
    elif r.is_dir():
        for p in r.rglob("*"):
            if p.is_file() and p.suffix.lower() in (".toml", ".json", ".yaml", ".yml"):
                _decls.append(p)

_hits = []
for d in _decls:
    try:
        raw = d.read_text(encoding="utf-8", errors="replace")
    except Exception:
        continue
    for name in FORBIDDEN_EXACT:
        if name in raw:
            _hits.append(f"{d.relative_to(_REPO)} 含 {name}")
    # 结构化再查一遍(纯文本查会被注释误伤/漏掉,两者互补)
    if d.suffix.lower() == ".toml":
        try:
            data = tomllib.loads(raw)
        except Exception:
            data = None
        if isinstance(data, dict):
            def _walk(node, path=""):
                if isinstance(node, dict):
                    for k, v in node.items():
                        if isinstance(k, str) and (k.startswith(FORBIDDEN_PREFIXES) or k in FORBIDDEN_EXACT):
                            _hits.append(f"{d.relative_to(_REPO)} 的键 {path}{k}")
                        _walk(v, path + str(k) + ".")
                elif isinstance(node, list):
                    for it in node:
                        if isinstance(it, str) and (it.startswith(FORBIDDEN_PREFIXES) or it in FORBIDDEN_EXACT):
                            _hits.append(f"{d.relative_to(_REPO)} 的列表项 {path} 含 {it}")
                        _walk(it, path)
            _walk(data)
check(f"扫描根内没有任何 gpu.* 工具 id(实测 hits={_hits})", not _hits)

print("=== 4. ★ 真空性必须是【显式】的,不能靠沉默 ===")
#   今天 _decls 是空的,所以第 3 条是真空通过。真空通过与真正查过一遍**长得不一样**才行 ——
#   否则将来注册表建好了、而扫描逻辑因为某个 bug 什么都没读到时,这里会继续绿。
print(f"    扫描根 {len(SCAN_ROOTS)} 个;其中存在的 {sum(1 for r in SCAN_ROOTS if r.exists())} 个;"
      f"扫到工具声明文件 {len(_decls)} 个")
check("扫描根清单非空(否则整套断言是空转)", len(SCAN_ROOTS) >= 2)
check("扫描根至少有一个真实存在于盘上(全都不存在 = 路径写错了,而不是'还没建')",
      any(r.exists() for r in SCAN_ROOTS),
      "config/tools.toml 与 10-core/mcp-tools 都不在 —— 先确认路径没写错")
if not _decls:
    print("    ★ 今日为【真空成立】:还没有任何工具声明。本文件的价值在于"
          "工具池被建出来的那一刻会自动开始把关,而不必有人记得回头补断言。")

print("=== 5. 红测自检:同一套判据对着一个【故意违规】的声明必须命中 ===")
#   不这么做的话,第 3 条可能因为解析器写错而永远绿 —— 那就是假断言。
_probe = tomllib.loads(
    'title = "probe"\n'
    '[pools.agent_worker]\n'
    'tools = ["fs.read", "gpu.apply@workstation"]\n'
)
_probe_hits = []
def _probe_walk(node, path=""):
    if isinstance(node, dict):
        for k, v in node.items():
            if isinstance(k, str) and (k.startswith(FORBIDDEN_PREFIXES) or k in FORBIDDEN_EXACT):
                _probe_hits.append(path + k)
            _probe_walk(v, path + str(k) + ".")
    elif isinstance(node, list):
        for it in node:
            if isinstance(it, str) and (it.startswith(FORBIDDEN_PREFIXES) or it in FORBIDDEN_EXACT):
                _probe_hits.append(path + it)
            _probe_walk(it, path)
_probe_walk(_probe)
check("红测:故意把 gpu.apply@workstation 塞进 agent 池 → 必须命中", _probe_hits == ["pools.agent_worker.tools.gpu.apply@workstation"] or len(_probe_hits) == 1,
      f"实得 {_probe_hits}")

_clean = tomllib.loads('[pools.agent_worker]\ntools = ["fs.read", "mem.search"]\n')
_clean_hits = []
_probe_hits = _clean_hits
_probe_walk(_clean)
check("红测:干净的池必须【不】命中(否则是全拦式假绿)", not _clean_hits, f"实得 {_clean_hits}")

print(f"\n=== gpu.* 工具池隔离:{_pass} PASS · {_fail} FAIL ===")
sys.exit(1 if _fail else 0)
