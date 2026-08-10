r"""「**凡起模型后端的地方都必须带上钥匙**」的元规则 —— D? 硬前置。

跑:  python 90-ops\gate\check_backend_auth.py

════════════════════════════════════════════════════════════════════════════
 ★★★ 它要防的那个形状 —— 也是用户选方向 B 时**被明确告知的那笔代价**

   「加第二个后端时那扇门默认又开着,**而没有任何测试会因此变红**。」

 今天起模型后端的地方有两处(`90-ops/start-stack.ps1` 与
 `10-core/gateway/model_loader.py`),而**将来会有第三处**:语音、画图、
 第二个 llama 实例。方向 B 若只是"这一次把这两处改对了",那么它的有效期
 就是到下一个人加第三处为止 —— 而那一天不会有任何东西变红。

 ⇒ **本文件就是来消掉那笔代价的。** 没有它,B 只是这一次生效,不是从此生效。

════════════════════════════════════════════════════════════════════════════
 ★★★ 为什么这个文件在 90-ops/gate 而不在 10-core

 **守卫必须待在它所检查的范围之外**(同 `check_contract_pairs.py` 文件头)。
 它同时检查 `90-ops/*.ps1`(PowerShell)与 `10-core/gateway/*.py`(Python),
 任何一侧都装不下它;放进任何一侧,那一侧就成了自己的裁判。

════════════════════════════════════════════════════════════════════════════
 ★★★ 三个方向,缺一条这套判据就会在某一天静默失效

  ① **kind 反向全表**(遍历源是 `model_loader.SUPPORTED_KINDS`,不是本文件的表)
     —— 新支持一种后端 kind 而不登记 ⇒ **红**。这条盖住"语音/画图/第三处"。
  ② **起法逐处**(遍历源是全仓扫 `'-ngl'`,不是本文件的表)
     —— 新加一处 llama 起法而不带 `--api-key-file` ⇒ **红**;
        带了但没登记 ⇒ 也**红**(逼人来这儿写一句为什么)。这条盖住"第二个 llama 实例"。
  ③ **消费侧**(网关出站转发)—— 出站不带 Authorization 就没人拿得到那把钥匙,
     后端上了锁反而是**网关自己被锁在门外**。

 ★ 登记表 `KIND_AUTH` / `LAUNCH_SITES` 只当**期望值**用于反向全表,
   **绝不当遍历源** —— 两者的区别就是"新增一项会红"和"新增一项被跳过"
   (ASSERTION-PITFALLS 3b)。

════════════════════════════════════════════════════════════════════════════
 ★★ 本文件**不覆盖**什么 —— 明写,不许静默少盖

 ① 它看的是**源码文本**,不是跑起来的进程。"起法带了 `--api-key-file`"
    不等于"那个后端此刻真的在拒绝无钥匙的连接"。**那一条由
    `90-ops/verify-backend-auth.ps1` 对着真后端打**(反向:不带 key 必须连不上)。
    两者是广度与实证的分工,谁也替不了谁。
 ② `speech` 后端**登记为豁免**:`10-core/speech/server.py` 自己没有鉴权层,
    它不是 llama-server、吃不下 `--api-key-file`。这是**如实的欠账**,不是遗漏 ——
    今天同机任何进程仍然连得上语音后端。要还它得改 P5 那边的服务本体。
    ★ 豁免必须写在 `KIND_AUTH` 里并带理由;**默认值是"不豁免"**。
 ③ 它不校验钥匙的强度/新鲜度。那些是 `backend_key.py` 自己的 fail-closed
    (下面第 ④ 组把那几条承重性质钉住,防的是有人把它们悄悄拆掉)。
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:                                            # noqa: BLE001
    pass

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
GW = REPO / "10-core" / "gateway"
sys.path.insert(0, str(GW))

_p = _f = 0
_fails: list[str] = []
_quiet = "--quiet" in sys.argv


def check(name: str, cond: bool, extra: str = "") -> None:
    global _p, _f
    if cond:
        _p += 1
    else:
        _f += 1
        _fails.append(name)
        print(f"  X {name}" + (f"   {extra}" if extra else ""))


def sect(t: str) -> None:
    if not _quiet:
        print(f"\n=== {t} ===")


# ══════════════════════════════════════════════════════════════════════════
#  登记表 —— **期望值**,不是遍历源。
# ══════════════════════════════════════════════════════════════════════════
#: 每一种后端 kind 的鉴权状态。遍历源是 `model_loader.SUPPORTED_KINDS`。
#    keyed  —— 起它的时候带钥匙(必须能在源码里看到 `--api-key-file`)
#    exempt —— 明知没带,**并且写清为什么**。默认值不是 exempt。
KIND_AUTH: dict[str, dict] = {
    "llm": {
        "state": "keyed",
        "why": "llama-server 支持 --api-key-file(实测 version 10107)。D? 的正主。",
    },
    "speech": {
        "state": "exempt",
        "why": "10-core/speech/server.py 是自写的 FastAPI 服务,没有鉴权层,也不认 "
               "--api-key-file。★ 这是**如实欠账**:同机任何进程今天仍连得上语音后端。"
               "还它要改 P5 的服务本体(给它同一把钥匙或换命名管道),不在 D? 车道内。",
    },
}

#: 起 llama 后端的地方。遍历源是**全仓扫 `'-ngl'`**,本表只当期望值。
#  ★ 新加一处却不登记 ⇒ 红(即使它带了钥匙)—— 逼人来这儿写一句"这是第几处、为什么"。
LAUNCH_SITES: dict[str, dict] = {
    "90-ops/start-stack.ps1": {
        "count": 1,
        "why": "无 Broker 期的手工起栈路径。用户日常不走这条,但开发天天走。",
    },
    "10-core/gateway/model_loader.py": {
        "count": 1,
        "why": "P4 Broker 的按需装载。★ 用户真正走的是这条(管理端 → 网关 → 装载器)。",
    },
}

#: 起法附近多少行内必须出现 `--api-key-file`。
#  ★ 取 8:两处现状都是 +2 行。给到 8 是为了容忍换行排版,而不是容忍"写在别的函数里"。
WINDOW = 8

_NGL = re.compile(r"""['"]-ngl['"]""")
_KEYFLAG = "--api-key-file"

SCAN_EXT = {".py", ".ps1", ".cs"}
#: ★★★ `.claude` 必须在这张表里 —— **这条是从主工作树跑第一次时当场被咬出来的**:
#  `.claude/worktrees/` 下挂着**别的车道的整份仓库副本**,rglob 会一头扎进去,
#  于是本门禁把隔壁车道的 `model_loader.py` / `start-stack.ps1` 全当成"未登记的
#  新起法"报红(实测一次 29 条假红)。假红比漏报更能毁掉一道门禁:它会训练人
#  去 `--no-verify`(ASSERTION-PITFALLS 第 5 条量过这个代价)。
#  ★ 注意它**只在主工作树里才会发生** —— 在车道自己的工作树里跑是绿的。
#    所以「在我这儿是绿的」不构成证据,门禁要到**主工作树**跑一次才算数。
SKIP_DIRS = {".git", ".claude", "00-docs", "obj", "bin", "dist", "node_modules"}

#: ★★ **本文件自己**要被排除出扫描 —— 第 ⑤ 组的正反样本里就写着 `'-ngl'`,
#  不排除的话守卫会当场绊倒自己(同款先例:`90-ops\debug\selfcheck.py` 的文件头)。
#  ★★★ 但"排除"就是一个藏东西的地方,所以它被钉成**恰好一条、且必须是本文件** ——
#    往这张表里再加一个文件名,下面那条断言立刻红。
SELF_EXCLUDE = {"90-ops/gate/check_backend_auth.py"}


def _scan_files():
    for p in REPO.rglob("*"):
        if p.suffix.lower() not in SCAN_EXT or not p.is_file():
            continue
        rel = p.relative_to(REPO)
        if any(part in SKIP_DIRS for part in rel.parts):
            continue
        if rel.as_posix() in SELF_EXCLUDE:
            continue
        yield p


def find_launch_sites(text: str) -> list[int]:
    """返回每一处 llama 起法所在的行号(1 起)。★ 提取器,下面第 ⑤ 组两个方向都钉它。"""
    return [i + 1 for i, line in enumerate(text.splitlines()) if _NGL.search(line)]


def site_has_key(text: str, lineno: int, window: int = WINDOW) -> bool:
    """这一处起法附近有没有带上钥匙。"""
    lines = text.splitlines()
    lo = max(0, lineno - 1 - 2)
    hi = min(len(lines), lineno - 1 + window + 1)
    return _KEYFLAG in "\n".join(lines[lo:hi])


def main() -> int:
    # ── ① kind 反向全表 ────────────────────────────────────────────
    sect("1. kind 反向全表(遍历源 = model_loader.SUPPORTED_KINDS)")
    try:
        import model_loader as _ml
        kinds = set(_ml.SUPPORTED_KINDS)
        imported = True
    except Exception as e:                                   # noqa: BLE001
        kinds = set()
        imported = False
        check(f"★★★ 导得进 model_loader —— 导不进就等于这条元规则没跑({type(e).__name__}: {e})",
              False)

    if imported:
        check("★★★ 每一种后端 kind 都在 KIND_AUTH 里登记过 —— 未登记的:"
              + str(sorted(kinds - set(KIND_AUTH))) +
              "  ⇒ 新支持一种后端就必须来这儿裁定它带不带钥匙,默认不是豁免",
              kinds <= set(KIND_AUTH))
        check("★ 反方向:KIND_AUTH 里没有已经不存在的 kind —— 多出的:"
              + str(sorted(set(KIND_AUTH) - kinds)),
              set(KIND_AUTH) <= kinds)
        check("★ 零命中也判红(SUPPORTED_KINDS 空了 = 这条全表什么也没盖住)", len(kinds) > 0)
        check("★★ `llm` 必须是 keyed —— 它就是 D? 要锁的那扇门",
              KIND_AUTH.get("llm", {}).get("state") == "keyed")
        for k, v in KIND_AUTH.items():
            check(f"★ kind `{k}` 的登记要带理由(豁免尤其要)—— 空理由等于没登记",
                  bool(str(v.get("why", "")).strip()) and v.get("state") in {"keyed", "exempt"})

    # ── ② 起法逐处 ────────────────────────────────────────────────
    sect("2. 起法逐处(遍历源 = 全仓扫 '-ngl')")
    found: dict[str, list[int]] = {}
    for p in _scan_files():
        try:
            text = p.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        sites = find_launch_sites(text)
        if sites:
            found[p.relative_to(REPO).as_posix()] = sites

    check("★★★ 扫到的起法文件与登记表一致 —— 只在一边的:"
          + str(sorted(set(found) ^ set(LAUNCH_SITES))) +
          "  ⇒ 新加一处 llama 起法而不登记 ⇒ 红",
          set(found) == set(LAUNCH_SITES))
    check("★ 零命中也判红(扫不到任何起法 = 提取器坏了,而不是【没有起法】)", len(found) > 0)
    check("★★★ 扫描没有走进**别的工作树**(`.claude/worktrees/` 下挂着别的车道的整份副本)"
          " —— 走进去会把隔壁车道的起法报成「未登记」,一次 29 条假红。命中的:"
          + str([r for r in found if r.startswith(".claude/")]),
          not any(r.startswith(".claude/") for r in found))
    check("★★★ 自排除表恰好只有【本文件】一条 —— 排除表就是一个藏东西的地方,"
          "所以它不许长:往里加一个文件名,这一条立刻红。实得:" + str(sorted(SELF_EXCLUDE)),
          SELF_EXCLUDE == {Path(__file__).resolve().relative_to(REPO).as_posix()})

    total_sites = 0
    for rel, lines in sorted(found.items()):
        total_sites += len(lines)
        exp = LAUNCH_SITES.get(rel, {}).get("count")
        check(f"★★ {rel} 里起法的处数与登记一致(实得 {len(lines)},登记 {exp})—— "
              f"同一个文件里多加一处也要来登记",
              exp == len(lines), str(lines))
        text = (REPO / rel).read_text(encoding="utf-8", errors="replace")
        for ln in lines:
            check(f"★★★ {rel}:{ln} 这一处起 llama 后端**带上了 `{_KEYFLAG}`** —— "
                  f"不带就是把 18081 对同机所有进程敞开(D65/STATE 已知技术债那条)",
                  site_has_key(text, ln))
    for rel, meta in LAUNCH_SITES.items():
        check(f"★ 登记项 {rel} 要带理由", bool(str(meta.get("why", "")).strip()))

    # ── ③ 消费侧:网关出站必须带 Authorization ─────────────────────
    sect("3. 消费侧(网关出站转发)")
    gw = (GW / "gateway.py").read_text(encoding="utf-8", errors="replace")
    calls = [ln for ln in gw.splitlines()
             if "upstream_url" in ln and re.search(r"_client\.\w+\(", ln)]
    check("★ 至少扫到一处出站转发调用(零命中 = 提取器坏了)", len(calls) > 0, str(len(calls)))
    keyed_calls = [ln for ln in calls if "headers=up_hdrs" in ln]
    check("★★★ **每一处**出站转发都带上了 Authorization(headers=up_hdrs)—— "
          f"实得 {len(keyed_calls)}/{len(calls)}。少一处,那一条路径就是无鉴权直连,"
          "而它长得和别的路径一模一样",
          len(calls) == len(keyed_calls) and len(calls) > 0,
          str([ln.strip()[:70] for ln in calls if ln not in keyed_calls]))
    _after = gw.split("up_hdrs = backend_key.auth_header()", 1)
    _tail = _after[1][:800] if len(_after) > 1 else ""
    check("★★★ 取不到钥匙时【拒绝转发】并回 503,而不是【不带头继续发】—— "
          "后者会在后端尚未上锁的机器上碰巧还能用,于是这条债静默重开,而测试全绿",
          "backend_key_unavailable" in _tail and "status_code=503" in _tail)
    check("★ 网关确实 import 了 backend_key(而不是自己另抄一份读密钥的逻辑)",
          re.search(r"^import backend_key$", gw, re.M) is not None)

    # ── ④ backend_key.py 的承重性质 ────────────────────────────────
    sect("4. backend_key.py 的承重性质(防有人悄悄拆掉 fail-closed)")
    bk_src = (GW / "backend_key.py").read_text(encoding="utf-8", errors="replace")
    try:
        import backend_key as _bk
        check("★★★ **空 key 文件会让 llama-server 完全不鉴权**(实测)⇒ 长度下限必须够 —— "
              f"MIN_KEY_CHARS={_bk.MIN_KEY_CHARS}", _bk.MIN_KEY_CHARS >= 32)
        check("★ 生成长度不短于下限", _bk.KEY_BYTES * 2 >= _bk.MIN_KEY_CHARS)
        bad = [b"", b"   \n", b"short", b"a" * 31]
        okc = 0
        for raw in bad:
            try:
                _bk._validate(raw, Path("x"))
            except _bk.BackendKeyError:
                okc += 1
        check("★★★ 反向:空 / 空白 / 过短的内容**一律拒绝**(这是那条 fail-open 实测的对策)"
              f" —— 拒掉 {okc}/{len(bad)}", okc == len(bad))
        try:
            _bk._validate(b"a" * 40 + b"\nb" * 40, Path("x"))
            multi_rejected = False
        except _bk.BackendKeyError:
            multi_rejected = True
        check("★★ 反向:多行的 key 文件也拒绝 —— llama-server 把**每一行**都当一把有效钥匙,"
              "而网关只发一份内容,两边从此对不齐", multi_rejected)
        good = _bk._validate(b"f" * 64 + b"\r\n", Path("x"))
        check("★ 正向:合法内容(含行尾)能通过并被剥干净", good == "f" * 64)
    except ImportError as e:
        check(f"★★★ 导得进 backend_key({e})", False)

    check("★★ `ensure_key_file` 会先收紧目录 ACL 再交出钥匙 —— "
          "{state} 根继承下来的是 Authenticated Users:(M),不断继承就等于没设",
          re.search(r"def ensure_key_file.*?harden_dir\(", bk_src, re.S) is not None)
    check("★★ 生成走原子落盘(os.replace)—— 半份文件就是 0 字节,而 0 字节 = 门大开",
          "os.replace(" in bk_src)
    check("★ 生成之后**也**要过一遍同一条校验(不是只校验读到的)",
          re.search(r"os\.replace\(.*?_validate\(", bk_src, re.S) is not None)
    check("★★ ACL 判据拿 **SID** 比,不拿显示名比 —— 显示名在别的语言版 Windows 上是另一串字",
          "S-1-5-32-544" in bk_src and "S-1-5-18" in bk_src)

    # ── ⑤ 提取器两个方向都被钉住(能抓 + 不误抓)──────────────────
    sect("5. 提取器两个方向(能抓 + 不误抓)")
    bad_sample = "\n".join(["args = [exe, '-m', model,", "        '-ngl', '99',",
                            "        '--port', '18099']"])
    good_sample = "\n".join(["args = [exe, '-m', model,", "        '-ngl', '99',",
                             "        '--api-key-file', kf]"])
    bs = find_launch_sites(bad_sample)
    gs = find_launch_sites(good_sample)
    check("★★★ **能抓**:一处不带钥匙的起法会被判红 —— 这条不成立的话,"
          "本文件的全部 PASS 都只是在说'我什么也没检查'",
          len(bs) == 1 and not site_has_key(bad_sample, bs[0]))
    check("★★ **不误抓**:带了钥匙的起法不判红", len(gs) == 1 and site_has_key(good_sample, gs[0]))
    check("★ 提取器不吃散文:注释里写 `-ngl` 三个字不算一处起法",
          find_launch_sites("# 这里解释一下 -ngl 是什么意思") == [])

    # ── 汇总 ──────────────────────────────────────────────────────
    keyed = sum(1 for v in KIND_AUTH.values() if v["state"] == "keyed")
    exempt = sum(1 for v in KIND_AUTH.values() if v["state"] == "exempt")
    print("-" * 78)
    if exempt and not _quiet:
        for k, v in KIND_AUTH.items():
            if v["state"] == "exempt":
                print(f"  ! 豁免:kind `{k}` —— {v['why']}")
    print(f"  === backend-auth: SITES={total_sites} KINDS={len(KIND_AUTH)} "
          f"KEYED={keyed} EXEMPT={exempt} ===")
    print(f"  === 后端鉴权元规则:{_p} PASS · {_f} FAIL ===")
    return 1 if _f else 0


if __name__ == "__main__":
    sys.exit(main())
