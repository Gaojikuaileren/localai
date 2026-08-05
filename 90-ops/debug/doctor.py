r"""链路体检 —— 从模型文件一路查到客户端,逐环报 up/down **并说清为什么**。

跑:  python 90-ops\debug\doctor.py

★★★ 这个工具存在的全部理由:把「不好使」变成「**第 3 环挂了,因为 X**」。
   2026-08-05 那次「软件打不开」花了几分钟排查,而根因(我把 185KB 的 apphost
   覆盖到了 81MB 的单文件产物上)本来一眼就能看出来 —— 只要有人列一下
   「dist/client 里那个 exe 多大、是不是自包含」。

★ **只读。** 一个字节都不写,一个进程都不起。
★ 不吞异常:探测器自己出错("工具坏了")与探到坏消息("系统坏了")**分开报** ——
  两者的下一步完全不同。
"""
from __future__ import annotations

import json
import os
import re
import socket
import subprocess
import sys
from pathlib import Path
from typing import List, Optional, Tuple

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:                                            # noqa: BLE001
    pass

REPO = Path(__file__).resolve().parents[2]

OK, BAD, WARN, ERR = "✔", "✘", "!", "?"
_rows: List[Tuple[str, str, str, str]] = []      # (环, 状态, 事实, 该做什么)


def row(link: str, status: str, fact: str, todo: str = "") -> None:
    _rows.append((link, status, fact, todo))


def probe(link: str, fn, todo_on_fail: str = "") -> None:
    """跑一次探测。★ 探测器抛异常 ⇒ 报 `?`(工具坏了),不报 `✘`(系统坏了)。"""
    try:
        ok, fact, todo = fn()
        row(link, OK if ok is True else (WARN if ok is None else BAD), fact, todo or todo_on_fail)
    except Exception as e:                                   # noqa: BLE001
        row(link, ERR, f"探测器自己出错:{type(e).__name__}: {e}",
            "这是**工具**的问题,不是系统的问题 —— 修 doctor.py")


# ── 工具 ────────────────────────────────────────────────────────────
def _paths(key: str) -> Optional[Path]:
    t = REPO / "config" / "paths.toml"
    if not t.exists():
        return None
    m = re.search(rf"^\s*{re.escape(key)}\s*=\s*'([^']+)'", t.read_text(encoding="utf-8"), re.M)
    return Path(m.group(1)) if m else None


def _listening(port: int, host: str = "127.0.0.1") -> bool:
    with socket.socket() as s:
        s.settimeout(0.6)
        return s.connect_ex((host, port)) == 0


def _http(url: str, timeout: float = 2.0) -> Tuple[Optional[int], str]:
    # ★★ 2026-08-05 修:原来是 `r.read(400)` —— 只读 400 字节。
    #   响应一长就被**从中间截断**,调用方 json.loads 抛 JSONDecodeError,
    #   于是体检报「探测器自己出错」。实测触发条件:GPU 快照加了两个字段、
    #   同步快照里有了记录,双双越过 400 字节。
    #   ⇒ 读全(留个上限防着有人往这儿塞流)。**截断的 JSON 与坏掉的服务长得一样**,
    #     而这两件事该做的下一步完全不同。
    try:
        import urllib.request
        with urllib.request.urlopen(url, timeout=timeout) as r:
            return r.status, r.read(1_000_000).decode("utf-8", "replace")
    except Exception as e:                                   # noqa: BLE001
        return None, f"{type(e).__name__}: {e}"


# ── 逐环 ────────────────────────────────────────────────────────────
def link_paths():
    m = _paths("models")
    if m is None:
        return False, "config/paths.toml 读不到 models 根", "检查 paths.toml 是否存在且有 models 键"
    return (m.exists(), f"models 根 = {m}({'在' if m.exists() else '**不存在**'})",
            "" if m.exists() else "盘符变了?改 config/paths.toml 一个文件即可(§11.1 就是为这个)")


def link_models():
    root = _paths("models")
    if root is None or not root.exists():
        return False, "models 根不可用(见上一环)", ""
    import tomllib
    cfg = tomllib.load(open(REPO / "config" / "vram-budget.toml", "rb"))
    miss, have = [], []
    for cid, c in cfg["components"].items():
        rel = c.get("model_rel")
        if not rel:
            continue                                          # 没有启动规格的组件不在此环
        (have if (root / rel).exists() else miss).append(cid)
    if miss:
        return False, f"有启动规格的 {len(have)+len(miss)} 个组件里,{len(miss)} 个模型文件**缺失**:{miss}", \
               "下载模型或改 vram-budget.toml 的 model_rel"
    return True, f"{len(have)} 个有启动规格的组件,模型文件都在", ""


def link_llama():
    root = _paths("models")
    if root is None:
        return False, "models 根不可用", ""
    exe = root.parent / "tools" / "llama.cpp" / "llama-server.exe"
    return exe.exists(), f"llama-server = {exe}({'在' if exe.exists() else '**不存在**'})", \
           "" if exe.exists() else "装 llama.cpp 或改路径"


def link_backend():
    import tomllib
    cfg = tomllib.load(open(REPO / "config" / "vram-budget.toml", "rb"))
    ports = sorted({int(c["port"]) for c in cfg["components"].values()
                    if c.get("port") and c.get("model_rel")})
    live = []
    for p in ports:
        st, _ = _http(f"http://127.0.0.1:{p}/health")
        if st is not None and 200 <= st < 300:
            live.append(p)
    if not live:
        return None, f"模型后端**没有在跑**(查过 {ports})", \
               "这不一定是错的 —— D87 之后是【按需装载】,没人用就该是这样。" \
               "要它常驻请跑 90-ops\\start-stack.ps1"
    return True, f"后端在跑:{live}", ""


def link_gateway():
    st, body = _http("http://127.0.0.1:8080/health")
    if st is None:
        return False, f"网关 8080 **不可达**({body[:60]})", \
               "跑 90-ops\\start-stack.ps1(★ 中枢服务化归 P7,今天还得手动起)"
    return 200 <= st < 300, f"网关 8080 → {st} {body[:60]}", ""


def link_gpu_face():
    st, body = _http("http://127.0.0.1:8080/v1/gpu/snapshot")
    if st is None:
        return False, "GPU 面不可达(网关没起?)", ""
    if st != 200:
        return False, f"GPU 面 → {st}(权限?)", "检查 caller-accounts.toml 的 allowlist"
    d = json.loads(body) if body.strip().startswith("{") else {}
    if not d:
        st, body = _http("http://127.0.0.1:8080/v1/gpu/snapshot", timeout=4)
        d = json.loads(body)
    inv = {i["invariant"]: i for i in d.get("invariants", [])}
    bad = [k for k, v in inv.items() if not v.get("holds")]
    conf = {k: v.get("confidence") for k, v in inv.items()}
    return (not bad), \
        f"state={d.get('state')} committed={d.get('committed')} " \
        f"actual={d.get('sets',{}).get('actual_resident')} 不变式={conf}" \
        + (f" ★ **违反**:{bad}" if bad else ""), \
        "★ 不变式违反 = 账本与现实分家,去看 upstream_problem.jsonl" if bad else ""


def _pair_profile():
    """本机的配对档案。★ 只此一处读它 —— 两处各写一份 key 名迟早会漂。"""
    prof = Path(os.environ.get("LOCALAPPDATA", "")) / "LocalAI" / "client" / "profile.json"
    if not prof.exists():
        return None
    try:
        return json.loads(prof.read_text(encoding="utf-8"))
    except Exception:                                        # noqa: BLE001
        return None


def link_lan_edge():
    # ★★★ 2026-08-05 修:原来只探 127.0.0.1:8443,于是**永远**报「没在听」——
    #   lan-edge 只把 8443 绑在**网卡 IP** 上,回环没有监听者(这正是主机自配对
    #   被封存的原因,见 decision-packets/selfpair-review)。
    #   ⇒ 这条误报最坏的地方是它的建议:「副机连不上就是它」——
    #     指着一个**好好的**东西说它坏了,人会去重启一个不需要重启的服务,
    #     而真正的原因(比如网关没起)就在它旁边那一行。
    #   ⇒ 探地址取自**配对档里的 dial**(副机真正要连的就是那个),回环只作兜底。
    hosts = []
    dial = str((_pair_profile() or {}).get("Dial") or "")
    if ":" in dial:
        hosts.append(dial.rsplit(":", 1)[0])
    hosts.append("127.0.0.1")

    for h in hosts:
        if _listening(8443, h):
            where = "回环" if h == "127.0.0.1" else h
            return True, f"lan-edge 8443 在听({where})", ""
    return None, f"lan-edge 8443 **没在听**(试过 {hosts})", \
        "副机连不上多半是它 —— 起 dist/host/localai-lan-edge.exe。" \
        "★ 但先看上一行:网关没起的话,lan-edge 起着也没用(它的上游就是 8080)"


def link_client_pkg():
    p = REPO / "dist" / "client" / "localai-client.exe"
    if not p.exists():
        return False, "dist/client 里没有客户端", "跑 90-ops\\build-client.ps1"
    mb = p.stat().st_size / 1024 / 1024
    # ★★ 这一条是 2026-08-05 那次「软件打不开」的直接判据:
    #   单文件自包含发布约 80MB;若只有几百 KB,那是**裸 apphost**,缺一堆 DLL,双击没反应。
    if mb < 10:
        return False, f"客户端 exe 只有 {mb:.1f} MB —— **这是裸 apphost,不是单文件自包含产物**", \
               "别直接拷 bin/Release 里的 exe。跑 90-ops\\build-client.ps1 出包,再拷 dist/client-pack 那个"
    v = (REPO / "dist" / "client" / "VERSION.txt")
    ver = v.read_text(encoding="utf-8-sig").splitlines()[1] if v.exists() else "(无 VERSION.txt)"
    dirty = ".dirty" in ver
    return (not dirty), f"{mb:.0f} MB · {ver.strip()}", \
        "★ 版本戳带 .dirty = 出包时工作区有未提交改动,装出去的东西与仓库对不上" if dirty else ""


def link_pairing():
    prof = Path(os.environ.get("LOCALAPPDATA", "")) / "LocalAI" / "client" / "profile.json"
    if not prof.exists():
        return None, "本机没有配对档案(这台不是客户端,或还没配对)", ""
    j = _pair_profile()
    if j is None:
        return False, "配对档案**读不动**(在,但解析不了)", "档案损坏 —— 需要重新配对"
    return True, f"已配对 · dial={j.get('Dial')} hub={str(j.get('HubId'))[:12]}…", ""


def link_vram():
    try:
        r = subprocess.run(["nvidia-smi", "--query-gpu=memory.free,memory.total",
                            "--format=csv,noheader,nounits"],
                           capture_output=True, text=True, timeout=8, check=True)
        free, total = [int(x) / 1024 for x in r.stdout.strip().split(",")]
    except Exception as e:                                   # noqa: BLE001
        return False, f"读不到显存:{type(e).__name__}", "没有 N 卡 / 驱动异常"
    import tomllib
    b = tomllib.load(open(REPO / "config" / "vram-budget.toml", "rb"))["budget"]
    budget = b["total_vram"] - b["desktop_floor"] - b["safety_margin"]
    return (free > b["safety_margin"]), \
        f"free {free:.2f} / {total:.2f} GiB · AI 预算 {budget:.2f}(桌面预留 {b['desktop_floor']})", \
        "" if free > b["safety_margin"] else "★ 现在连安全余量都不够 —— 关掉占显存的程序"


def link_sync():
    st, body = _http("http://127.0.0.1:8080/v1/sync/snapshot")
    if st is None:
        return None, "同步面不可达(网关没起)", ""
    if st != 200:
        return False, f"同步面 → {st}", ""
    d = json.loads(body)
    return True, f"共享数据:{d.get('counts')} · generation={d.get('generation')}", ""


LINKS = [
    ("① 路径契约", link_paths),
    ("② 模型文件", link_models),
    ("③ llama-server", link_llama),
    ("④ 模型后端", link_backend),
    ("⑤ 网关 8080", link_gateway),
    ("⑥ GPU 面 + 不变式", link_gpu_face),
    ("⑦ 同步面", link_sync),
    ("⑧ lan-edge 8443", link_lan_edge),
    ("⑨ 客户端产物", link_client_pkg),
    ("⑩ 配对档案", link_pairing),
    ("⑪ 显存", link_vram),
]


def main() -> int:
    print("=" * 78)
    print("  LocalAI 链路体检   ★ 只读:一个字节都不写、一个进程都不起")
    print("=" * 78)
    for name, fn in LINKS:
        probe(name, fn)
    w = max(len(r[0]) for r in _rows)
    for link, st, fact, todo in _rows:
        print(f"  {st}  {link:<{w}}  {fact}")
        if todo:
            print(f"     {'':<{w}}  → {todo}")
    bad = [r for r in _rows if r[1] == BAD]
    err = [r for r in _rows if r[1] == ERR]
    warn = [r for r in _rows if r[1] == WARN]
    print("-" * 78)
    print(f"  {len(_rows)-len(bad)-len(err)-len(warn)} 正常 · {len(warn)} 提示 · "
          f"{len(bad)} **有问题** · {len(err)} 探测器自己出错")
    # ★ 退出码分开:1 = 系统有问题;2 = 工具自己坏了。两者的下一步完全不同。
    return 2 if err else (1 if bad else 0)


if __name__ == "__main__":
    sys.exit(main())
