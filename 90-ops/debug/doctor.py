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


def _port_owner(port: int) -> str:
    """谁在监听这个端口。★ 只读,查不出来就返回空串(不猜)。"""
    try:
        out = subprocess.run(["netstat", "-ano", "-p", "TCP"],
                             capture_output=True, text=True, timeout=8).stdout
        pid = ""
        for line in out.splitlines():
            f = line.split()
            if len(f) >= 5 and f[3].upper() == "LISTENING" and f[1].endswith(f":{port}"):
                pid = f[4]
                break
        if not pid:
            return ""
        t = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"],
                           capture_output=True, text=True, timeout=8).stdout.strip()
        return t.split(",")[0].strip('"') if t and "," in t else ""
    except Exception:                                        # noqa: BLE001
        return ""


def chat_path_autoloads():
    """聊天那条路径上,**有没有任何东西会把后端拉起来**?

    返回 (答案, 依据)。答案 True/False/None(None = 读不到源码,不猜)。

    ★★★ 为什么要**当场查**,而不是照着记忆写一句话:
      这一环原本写着「后端没在跑不一定是错的 —— D87 之后是【按需装载】,
      没人用就该是这样」。**那句话给一个不存在的机制背了书**:
      `/v1/chat/completions` 全路径(268 行)一次都没碰过 Broker / 装载器 / 租约,
      于是"后端没起"的真实后果是**聊天直接失败**,而体检报告说这可能很正常。
      ⇒ 一个在系统坏了的时候报"这可能很正常"的体检工具,正是本套工具存在的理由所指的东西。

    ★★ 而"直接把那句话改成相反的"会在**另一个方向**上重犯同样的错:
      按需装载哪天真接上了,这句话又变成新的假话,而且没有任何东西会提醒。
      ⇒ 判据必须是**当场量出来的**,让它自己跟着源码翻面。
      (ASSERTION-PITFALLS 第 9 条:判据问的是「我做不做得到」,不是「我是什么身份」。)

    ★ 只把 gateway.py 当**文本**读,不 import —— selfcheck 第 ② 组钉着
      「工具不 import 项目代码」:项目坏了的时候工具还得能跑,那正是最需要它的时刻。
    """
    gw = REPO / "10-core" / "gateway" / "gateway.py"
    if not gw.exists():
        return None, "读不到 gateway.py"
    try:
        return autoload_in(gw.read_text(encoding="utf-8", errors="replace"))
    except Exception:                                        # noqa: BLE001
        return None, "读不到 gateway.py"


#  ★ 抽成纯函数是为了**能被合成输入两个方向各问一遍**(selfcheck.py 第 ⑩ 组)。
#    ★★ selfcheck 里**不断言今天的答案是 False** —— 那条断言会在
#      「按需装载终于接上了」那天变红,而那正是我们盼着发生的事
#      (ASSERTION-PITFALLS 第 5 条:一条会因为「功能终于能用了」而变红的断言,
#       测的就不是它自称在测的东西)。⇒ 只钉**识别器**,不钉现状。
def autoload_in(src: str):
    anchor = '@app.post("/v1/chat/completions")'
    if anchor not in src:
        return None, "源码里找不到 chat 路由(路由写法变了?)"
    i = src.index(anchor)
    j = src.find("\n@app.", i + len(anchor))
    body = src[i:(j if j > 0 else len(src))]
    # 去掉整行注释:本仓的习惯是把「为什么没做」写在注释里,照字面搜会把
    # **说明它没接**的那段话读成"它接了"(ASSERTION-PITFALLS 第 1 条,已踩 9 次)。
    code = "\n".join(ln for ln in body.split("\n") if not ln.strip().startswith("#"))
    hits = sorted({w for w in ("BROKER", "gpu_broker", "ensure_loaded",
                               "ModelLoader", "_loader", "acquire_lease")
                   if w in code})
    return bool(hits), (f"chat 路径引用了 {hits}" if hits
                        else f"chat 路径 {code.count(chr(10))} 行代码里,"
                             "Broker / 装载器 / 租约**一个都没有**")


def link_backend():
    import tomllib
    cfg = tomllib.load(open(REPO / "config" / "vram-budget.toml", "rb"))
    ports = sorted({int(c["port"]) for c in cfg["components"].values()
                    if c.get("port") and c.get("model_rel")})
    live, impostor = [], []
    for p in ports:
        st, _ = _http(f"http://127.0.0.1:{p}/health")
        who = _port_owner(p)
        if st is not None and 200 <= st < 300:
            # ★★ 2026-08-05:/health 回 2xx **不证明那是我们的后端**。
            #   实测撞见过:另一条车道的一次性 spike(AcSpike,跑在 %TEMP% 里)
            #   临时占住了 18081。那种情况下 llama-server 根本绑不上,
            #   而只看 2xx 的话这一环会报"后端在跑" —— 又是"看着好、实际不是那个东西"。
            #   ⇒ 顺带把**监听者是谁**查出来。查不出来不猜(返回空串),但对不上就点名。
            if who and "llama" not in who.lower():
                impostor.append(f"{p}(被 {who} 占着)")
            else:
                live.append(p if not who else f"{p}({who})")
        elif who:
            # 端口被占着但 /health 不通 —— 这是最坏的一种:我们的后端起不来,而且不明显
            impostor.append(f"{p}(被 {who} 占着且不应答 /health)")
    if impostor:
        return False, f"★ 后端端口被**别的进程**占着:{impostor}" \
                      + (f";正常在跑的:{live}" if live else ""), \
               "我们的后端绑不上这个端口 —— 先看那个进程是什么。" \
               "本机撞见过另一条车道的一次性 spike 占用 18081"
    if not live:
        # ★★★ 这一环此前写着「不一定是错的 —— D87 之后是【按需装载】,没人用就该是这样」。
        #   **那是替一个不存在的机制背书。** 现在当场去量(见 chat_path_autoloads)。
        auto, why = chat_path_autoloads()
        if auto is True:
            return None, f"模型后端**没有在跑**(查过 {ports})", \
                   f"按需装载**已接线**({why})—— 没人用就该是这样。" \
                   "要它常驻请跑 90-ops\\start-stack.ps1"
        if auto is None:
            # ★ 读不到 ⇒ 不猜。**也不许倒回那句好听的话** ——
            #   "读不到" 与 "没问题" 必须长得不一样。
            return False, f"模型后端**没有在跑**(查过 {ports})", \
                   f"★ 而且查不出有没有按需装载({why})—— 在查清楚之前," \
                   "别把「没在跑」读成「正常」。先跑 90-ops\\start-stack.ps1"
        return False, f"模型后端**没有在跑**(查过 {ports})", \
               f"★★ 没有任何东西会把它拉起来:{why}。" \
               "⇒ 现在去聊天会**直接失败**,不是「等它按需装载」。" \
               "先跑 90-ops\\start-stack.ps1;【按需装载】仍是 P4 的未完项"
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


#  ── 产物身份:版本戳 → (时间戳, 提交号, 脏树指纹)──────────────────────
#  ★ 抽成纯函数是为了能被合成输入两个方向各问一遍(selfcheck.py 第 ⑪ 组)。
_BUILD_ID = re.compile(r"版本戳:\s*([0-9]{8}-[0-9]{4})\+([0-9a-fA-F]{7,40})(?:\.dirty-(\w+))?")


def parse_build_id(version_txt: str):
    """从 VERSION.txt 里取出产物的身份。取不到返回 (None, None, None)。

    ★★★ 为什么这个函数必须存在:
      `.dirty` 那一半此前是**唯一**被检查的东西 —— 它只回答「出包那一刻工作区干不干净」。
      它**从来不问「这份产物是哪个提交出的、那个提交是不是最新的」**。
      于是最常见的那种不一致完全不被发现:**在一个干净的旧提交上出的包**。
      版本戳里明明写着提交号(`20260806-1655+4e5da1f`),而没有任何东西读它。
      ⇒ 「产物落后于源码」此前**只能靠人记得出包**,而这个项目已经被
        「跑的不是刚改的那个产物」咬过 4 次(ASSERTION-PITFALLS 第 3 条)。
    """
    m = _BUILD_ID.search(version_txt)
    if not m:
        return None, None, None
    return m.group(1), m.group(2).lower(), m.group(3)


def _git(*args):
    """只读地问 git 一句。★ 用 subprocess.run(查询型),不用 Popen ——
    selfcheck 第 ④ 组钉着「doctor.py 承诺只读」,而它的识别器把 Popen 算作起进程。"""
    try:
        r = subprocess.run(("git", "-C", str(REPO)) + args,
                           capture_output=True, text=True, timeout=8)
        return r.stdout.strip() if r.returncode == 0 else ""
    except Exception:                                        # noqa: BLE001
        return ""


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
    if not v.exists():
        # ★ exe 在、身份不在 ⇒ 判红。**「说不清这是什么」不许当成「没问题」** ——
        #   一个身份不明的产物,和一个过期的产物一样不能装出去。
        return False, f"{mb:.0f} MB · **没有 VERSION.txt**", \
               "这份 exe 说不清是哪个提交出的。重新跑 90-ops\\build-client.ps1"
    raw = v.read_text(encoding="utf-8-sig")
    ver = raw.splitlines()[1].strip() if len(raw.splitlines()) > 1 else raw.strip()
    stamp, sha, dirty_id = parse_build_id(raw)
    if sha is None:
        return False, f"{mb:.0f} MB · 版本戳解析不出提交号:{ver[:60]}", \
               "★ 解析不出 ≠ 没问题:格式变了就得把 doctor.py 的 _BUILD_ID 跟着改," \
               "否则这一环从此恒绿而什么都没在查"

    # ── 产物落后于源码?──────────────────────────────────────────────
    head = _git("rev-parse", "HEAD")[:len(sha)].lower()
    if not head:
        # 问不到 git(不是仓库 / 没装 git)⇒ 不猜,但也不报"没问题"。
        return None, f"{mb:.0f} MB · {ver}", \
               "★ 问不到 git,**无法判断产物是不是落后于源码** —— 这不等于它没落后"
    if dirty_id:
        return False, f"{mb:.0f} MB · {ver}", \
               "★ 版本戳带 .dirty = 出包时工作区有未提交改动,装出去的东西与仓库对不上"
    if sha == head:
        return True, f"{mb:.0f} MB · {ver} · **与 HEAD 同一个提交**", ""
    # 落后多少个提交?算不出来也要如实说"算不出来"。
    behind = _git("rev-list", "--count", f"{sha}..HEAD")
    if not behind:
        return False, f"{mb:.0f} MB · {ver} · 提交号 {sha} **不在当前历史里**", \
               "★ 这份产物是从一个本仓已经没有的提交出的(分支被 rebase / 重写过?)" \
               " —— 它的内容无从核对。重新出包"
    return False, f"{mb:.0f} MB · {ver} · **落后 HEAD {behind} 个提交**(HEAD={head})", \
           "★ 装在用户机器上的不是当前源码。这一条此前【只查 .dirty、从不比提交号】," \
           "所以「在一个干净的旧提交上出的包」完全不会被发现。重新跑 90-ops\\build-client.ps1"


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


# ══════════════════════════════════════════════════════════════════════════
#  ⑫ CUDA sysmem fallback —— **只读一次注册表,如实上报,不判对错**
#
#  ★★★ 用户裁定(2026-08-07 · P4 收官):
#    **我们自己的闸提前介入**(D99:压力即让 → 卸载 + 任务暂停 + 主副机都收到提醒);
#    **系统注册表那个开关【不动】** —— 它是系统级驱动设置,会影响游戏等所有程序。
#  ⇒ 本环**恒返回提示态**,不返回 ✔ 也不返回 ✘:
#    判 ✘ 等于把「裁定不做」渲染成「有问题」,那会有人跑去把它关掉;
#    判 ✔ 等于声称"已经安全了",而我们并没有验过它是关的。
#    **它报的是一个事实,不是一个判决。**
#
#  ★★ 这一环存在的理由,是 `start-stack.ps1` 那段注释踩过的坑(审计 C6):
#    「sysmem fallback 关掉后是硬失败」曾被当成**已成立的前提**用了很久,
#    而它**从来没有被建立过**。⇒ 现在每次体检都把真实状态摆出来。
#
#  ★★★ **本检查覆盖不到的那一半,必须自己说出来**:
#    NVIDIA 控制面板里的「CUDA - Sysmem Fallback Policy」存在**驱动配置库二进制**
#    (`%ProgramData%\NVIDIA Corporation\Drs\nvdrsdb*.bin`)里,**不在注册表**。
#    本检查**不解析**那个二进制。
#    ⇒ **「注册表里没有覆盖项」只等于「没人从注册表改过」,不等于「fallback 已关」。**
#    不写这一句的话,这一环本身就成了「看着有防护、实际没有」。
# ══════════════════════════════════════════════════════════════════════════
_SYSMEM_ROOTS = [
    r"HKLM\SYSTEM\CurrentControlSet\Services\nvlddmkm",
    r"HKLM\SOFTWARE\NVIDIA Corporation\Global",
    r"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
]
_SYSMEM_RE = re.compile(r"sysmem|fallback", re.I)


#  ★ 抽成纯函数是为了**能被合成输入两个方向各问一遍**(selfcheck.py)。
#    ★★ selfcheck 里**不断言今天的答案**(注册表今天是空的)——
#      那条断言会在用户哪天真的去关它的时候变红,而那是他的权利,不是缺陷
#      (ASSERTION-PITFALLS 第 5 条:会因为「状态变了」而红的断言,测的不是它自称在测的东西)。
#      ⇒ 只钉**识别器**:给它带 sysmem 的名字必须挑出来,不带的必须不挑。
def sysmem_overrides(scanned):
    """从 `[(根, 值名, 值)]` 里挑出 sysmem/fallback 覆盖项。**纯函数,不碰注册表。**"""
    return [(root, name, val) for root, name, val in scanned if _SYSMEM_RE.search(name)]


def _scan_sysmem_registry():
    """只读扫上面那几个根(含子键)。返回 `(scanned, errors)`。
    ★ 读不到不等于没有 —— 错误单独带回来,由调用方如实说,**不当作"没问题"**。"""
    scanned, errors = [], []
    try:
        import winreg                                        # noqa: PLC0415
    except ImportError:                                      # 非 Windows
        return scanned, ["winreg 不可用(非 Windows)"]

    def _walk(hkey, path, depth=0):
        try:
            k = winreg.OpenKey(hkey, path, 0, winreg.KEY_READ)
        except OSError as e:
            errors.append(f"{path}: {type(e).__name__}")
            return
        with k:
            try:
                n_sub, n_val, _ = winreg.QueryInfoKey(k)
            except OSError as e:
                errors.append(f"{path}: QueryInfoKey {type(e).__name__}")
                return
            for i in range(n_val):
                try:
                    name, val, _ = winreg.EnumValue(k, i)
                    scanned.append((f"HKLM\\{path}", name, val))
                except OSError:
                    continue
            if depth < 2:                                    # 只下探两层:够到 NVTweak 那一类
                for i in range(n_sub):
                    try:
                        _walk(hkey, path + "\\" + winreg.EnumKey(k, i), depth + 1)
                    except OSError:
                        continue

    for root in _SYSMEM_ROOTS:
        _walk(winreg.HKEY_LOCAL_MACHINE, root.split("\\", 1)[1])
    return scanned, errors


def link_sysmem():
    scanned, errors = _scan_sysmem_registry()
    hits = sysmem_overrides(scanned)
    # ★ 从环境变量取,**不写死盘符** —— pre-commit 当场拦过一次(§11.1:代码里禁绝对路径)。
    #   钩子是对的:ProgramData 可以被挪走,写死的那一行会在挪走的那台机器上变成一句错话,
    #   而它的表现是「未找到 Drs 配置库」—— 一个**指向别处**的结论。
    #   ⇒ 取不到就如实说取不到,**不猜一个默认值**。
    _pd = os.environ.get("ProgramData")
    drs = Path(_pd) / "NVIDIA Corporation" / "Drs" if _pd else None
    drs_files = sorted(p.name for p in drs.glob("nvdrsdb*.bin")) if drs and drs.is_dir() else []

    if hits:
        what = " · ".join(f"{n}={v}" for _, n, v in hits[:3])
        fact = f"注册表里有 {len(hits)} 项 sysmem/fallback 覆盖:{what}"
    else:
        fact = (f"注册表里**没有**任何 sysmem/fallback 覆盖项(扫了 {len(scanned)} 个值)"
                " ⇒ 用的是**驱动默认值**,**不是「已关」**")
    if errors:
        fact += f" · ★ 有 {len(errors)} 处读不到:{errors[0]}"
    if drs_files:
        fact += f" · 控制面板那个设置在 Drs/{','.join(drs_files)}(本检查**不解析**)"
    elif drs is None:
        fact += " · ★ 读不到 %ProgramData%,**没去找** Drs 配置库(不猜路径)"
    else:
        fact += " · 未找到 Drs 配置库"

    todo = ("★ 这是**如实上报,不是判决**:用户已裁定【注册表开关不动】,"
            "拦住超配的是我们自己的三道闸 + D99 压力即让。"
            "★★ 「注册表没有覆盖项」**不等于** fallback 已关 —— "
            "控制面板那个设置在 Drs 二进制里,本检查够不着。要确认只能开控制面板看。")
    return None, fact, todo


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
    ("⑫ sysmem fallback", link_sysmem),
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
