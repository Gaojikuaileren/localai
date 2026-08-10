r"""P4-S14 · 装载器本体 —— Broker 真的会起停后端进程(D87)。

★★★ 这是 P4 清单最后一条,也是本项目第一次**真的动系统状态**:起进程、杀进程。
   在此之前所有事务的终点都是 `loader_absent` —— 那是**有意的**:
   给装载器一个空实现会让每次事务都"成功"、四个集合全等、不变式永远绿,
   **而显存里一个字节都没有**。

★★★ 它同时把 S7 欠的那笔账还上:`actual_resident` 终于有了**独立事实源**。
   S7 当时如实写着:
     「actual_resident 今天不是独立观测 —— 没有装载器 + WDDM 不暴露逐进程显存
       ⇒ 它就是 Broker 自己的账本,用自己的账本跟自己的账本比【永远相等】」
   现在「哪些组件真的装着」= **哪些后端进程活着且 /health 回 2xx** ——
   那是一个与账本**无关**的事实源。
   ⇒ 钉住那条恒真性的断言会红,而那正是它被写下来的目的。

────────────────────────────────────────────────────────────────────
★★ 三条硬规矩:

  ① **按 kind 分派,未登记的 kind fail-closed。**
     `start-stack.ps1` 从来只起过 8B 一个后端 —— speech / vlm / comfyui
     **怎么起没有人验证过**。装载器对它们如实报「启动方式尚未验证」,**不猜**。
     猜一套参数的后果不是"可能起不来",是**看起来支持而第一次真用时才炸**。

  ② **就绪判据是 /health 回 2xx,不是"进程起来了"。**
     llama-server 在加载模型时 /health 回 **503** ——
     `start-stack.ps1` 的注释写着「不加 -f 会误以为已就绪(踩过)」。
     进程存在 ≠ 能服务。

  ③ **认领已经在跑的(孤儿进程)。**
     中枢重启后,上一批后端还活着。不认领的话:Broker 以为没装、再起一个 → 端口冲突;
     或者以为装了、其实是别人起的 → 账本与现实分家。
     ⇒ 启动时**先探一遍端口**,活着的就认领 —— 以现实为准,不以账本为准。
"""
from __future__ import annotations

import asyncio
import os
import re
import subprocess
from pathlib import Path
from typing import Dict, List, Optional, Set

import httpx

import backend_key

#: 已验证过启动方式的 kind。★ 反向全表:不在表里的一律拒绝,不试着用别的方式起。
#
#  ★★★ 2026-08-06(P5 语音 v1 / D?):`speech` **加进来了**,而加它的依据不是
#    "我写了一份启动规格",是 `10-core/speech/verify_launch.py` **真的起过一次**:
#      · 两个 ASR 档位都在 HF_HUB_OFFLINE=1 + local_files_only=true 下加载成功
#        (turbo 1.9s / large-v3 3.6s),转写跑通;
#      · Piper 加载 0.99s、合成 0.07s、22050 Hz 出声。
#    读数写在 `10-core/speech/launch.toml` 的 [verified] 段,而
#    `_speech_spec()` **只认带 [verified] 段的规格** —— 没有它就退回本文件头那句
#    「启动方式尚未验证」。⇒「改了启动参数但没重新验证」是一件**会红**的事,
#    而不是一件"下次真去起的时候才发现"的事。
#
#  ★ vlm / comfyui 仍然不在表里 —— 它们的启动方式**依然没有人验证过**。
#    别看见 speech 进来了就顺手把它们也加上:那正是这条规矩要防的动作。
SUPPORTED_KINDS: Set[str] = {"llm", "speech"}

#: speech 的启动规格所在(**不在 config/vram-budget.toml 里**)。
#  ★ 为什么:那份文件是准入闸(peak)的唯一数据源,归 config 车道;
#    而启动规格该和它所启动的那个服务放在一起 —— 改了服务却忘了改规格,
#    放在同一个目录里比隔着两条车道更难发生。
#  ★★ peak(显存)**仍然只由 vram-budget.toml 说了算**,launch.toml 里不重复登记:
#    两处都写一个数,迟早对不上,而准入闸会照着错的那个放行。
SPEECH_SPEC_REL = "10-core/speech/launch.toml"

#: 就绪轮询:llama-server 加载中 /health 回 503,必须等到 2xx。
READY_TIMEOUT_S = 180.0
READY_POLL_S = 1.0


class LoaderError(RuntimeError):
    """装载失败。★ 抛而不是返回 False —— S8 的事务靠异常走回滚路径。"""


def _paths_root(key: str) -> Path:
    """从 config/paths.toml 读一个根。★ §11.1:代码里不写绝对路径。"""
    here = Path(__file__).resolve()
    for p in [here.parents[i] for i in range(2, 6)]:
        toml = p / "config" / "paths.toml"
        if toml.exists():
            m = re.search(rf"^\s*{re.escape(key)}\s*=\s*'([^']+)'",
                          toml.read_text(encoding="utf-8"), re.M)
            if m:
                return Path(m.group(1))
    raise LoaderError(f"paths.toml 里找不到 {key} —— 拒绝猜一个路径(§11.1)")


def _repo_root() -> Path:
    """代码根。★ 同样不写绝对路径 —— 从本文件往上找带 config/paths.toml 的那一层。"""
    here = Path(__file__).resolve()
    for p in [here.parents[i] for i in range(1, 6)]:
        if (p / "config" / "paths.toml").exists():
            return p
    raise LoaderError("找不到仓库根(没有 config/paths.toml 的那一层)")


def _speech_python(spec: dict) -> Path:
    """
    speech venv 的 python。★ 路径由 paths.toml 的根推出,不写绝对路径(§11.1)。

    ★ `root = "state_sibling"` 的含义:venvs 目录与 **state 根**同级。
      ⇒ 这条推导在这里写明,而不是在别处硬编码一个带盘符的路径(§11.1)。
    """
    v = spec["venv"]
    if v.get("root") != "state_sibling":
        raise LoaderError(f"speech venv 的 root 形状不认识:{v.get('root')} —— 不猜")
    py = _paths_root("state").parent / v["rel"]
    if not py.exists():
        raise LoaderError(f"找不到 speech venv 的 python:{py}")
    return py


class ModelLoader:
    """真的起停后端进程。

    ★ 与 Broker 的分工:Broker 决定**该装哪些**(三道闸 + 事务),
      装载器只负责**把决定变成现实**,并如实回报现实是什么。
      装载器**不做任何准入判断** —— 那会变成第二个判定内核(§8.1 规则 18 禁止)。
    """

    def __init__(self, cfg=None):
        self._cfg = cfg
        self._procs: Dict[str, subprocess.Popen] = {}
        self._adopted: Set[str] = set()      # 认领的孤儿(不是我起的,所以也不该由我杀)
        self._llama: Optional[Path] = None
        self._models: Optional[Path] = None
        self._speech: Optional[dict] = None   # speech 的启动规格(只认带 [verified] 的)

    # ── 配置 ────────────────────────────────────────────────────────
    @property
    def cfg(self):
        if self._cfg is None:
            import vram_gate
            self._cfg = vram_gate.load_config()
        return self._cfg

    def _spec(self, cid: str) -> dict:
        """取组件的启动规格。★ 缺任何一项都 fail-closed 并说清缺什么。"""
        c = self.cfg.components.get(cid)
        if c is None:
            raise LoaderError(f"未登记的组件 {cid}(准入白名单里没有)")
        kind = str(c.get("kind") or "")
        if kind not in SUPPORTED_KINDS:
            raise LoaderError(
                f"组件 {cid} 的 kind='{kind}' **启动方式尚未验证** —— "
                f"start-stack.ps1 从来只起过 llm 一个后端。"
                f"已验证的只有 {sorted(SUPPORTED_KINDS)}。"
                f"★ 这里拒绝猜一套参数:猜错的后果不是「可能起不来」,"
                f"是【看起来支持而第一次真用时才炸】")
        # ★ 按 kind 分派要什么参数。speech 的启动规格不在 vram-budget.toml 里
        #   (它只管 peak),而在 10-core/speech/launch.toml —— 见 SPEECH_SPEC_REL。
        if kind == "speech":
            self._speech_spec()          # 只为触发 fail-closed 校验;缺 [verified] 会抛
            if c.get("port") in (None, ""):
                raise LoaderError(f"组件 {cid} 缺 port(见 config/vram-budget.toml)")
            return c
        for need in ("model_rel", "ctx", "ngl", "port"):
            if c.get(need) in (None, ""):
                raise LoaderError(f"组件 {cid} 缺启动参数 {need}(见 config/vram-budget.toml)")
        return c

    def _speech_spec(self) -> dict:
        """
        读 speech 的启动规格。★★ **只认带 [verified] 段的规格**。

        没有 [verified] ⇒ 抛出本文件头那句「启动方式尚未验证」——
        因为一份没被真的起过一次的规格,和"猜一套参数"是同一件事,
        而猜错的后果不是"可能起不来",是**看起来支持而第一次真用时才炸**。
        """
        import tomllib

        if self._speech is None:
            p = _repo_root() / SPEECH_SPEC_REL
            if not p.exists():
                raise LoaderError(
                    f"speech 的**启动方式尚未验证**:找不到 {SPEECH_SPEC_REL}")
            with open(p, "rb") as f:
                spec = tomllib.load(f)
            if "verified" not in spec:
                raise LoaderError(
                    f"speech 的**启动方式尚未验证**:{SPEECH_SPEC_REL} 缺 [verified] 段 —— "
                    f"请跑 `10-core/speech/verify_launch.py` 真的起一次,再把读数写回去")
            if not spec["verified"].get("asr_offline_load_ok"):
                raise LoaderError(
                    f"speech 的**启动方式尚未验证**:[verified].asr_offline_load_ok 不为真 —— "
                    f"权重没在本地离线加载成功过,而本服务不会联网补齐")
            self._speech = spec
        return self._speech

    def _llama_exe(self) -> Path:
        if self._llama is None:
            self._llama = _paths_root("models").parent / "tools" / "llama.cpp" / "llama-server.exe"
        if not self._llama.exists():
            raise LoaderError(f"找不到 llama-server:{self._llama}")
        return self._llama

    def _model_path(self, rel: str) -> Path:
        if self._models is None:
            self._models = _paths_root("models")
        p = self._models / rel
        if not p.exists():
            raise LoaderError(f"找不到模型文件:{p}")
        return p

    # ── 就绪判定 ────────────────────────────────────────────────────
    #  ★★★ V16:**「能服务」与「还活着」是两件事**,此前只有前者。
    #
    #  `_health_ok` 回答的是「能不能服务」(2xx),它是**装载就绪**的判据,那部分没错。
    #  但拿它去回答「进程还在不在」是**错的方向**:llama-server 加载模型时回 503
    #  (本文件头 :24-27 自己写着),而**那时它已经占满了显存**。
    #  ⇒ 一次 503 或一次超过 2 秒的响应,在旧代码里等价于「不在了」——
    #    于是 `running()` 会把一个**活着且占着 6 GiB** 的后端从账本上抹掉(:256-261),
    #    而抹掉之后再也没有任何一条路径会回来探它。
    #  ⇒ 分成三态,并让「卸载核实」与「孤儿检测」用 alive 那一档,不用 ready。
    PORT_READY = "ready"    # 有响应且 2xx  —— 能服务
    PORT_ALIVE = "alive"    # 有响应但非 2xx(如加载中的 503)—— **占着显存**
    PORT_DOWN  = "down"     # 连不上 —— 端口上确实没有人
    #: 驻留真相探针的超时。★ 比就绪判定短:它挂在 1 Hz 采样循环上,而就绪判定
    #  是一次性的开机等待。两个用途、两个数,**不共用一个常量**。
    TRUTH_PROBE_TIMEOUT_S = 1.0

    #  ★ 这两个是**实例方法**而不是 staticmethod:全模块的探网**只从这一个洞出去**,
    #    于是断言可以子类化装载器、把端口的真假**注入**进来,而不必去真的占用
    #    18081/18085 这些生产端口(测试里绑生产端口 = 测试去动真实系统的状态,
    #    正是 test_gpu_broker 那段 `_NoAdoptLoader` 的理由所要防的事)。
    async def _port_state(self, port: int, timeout: float = 2.0) -> str:
        """端口三态。★ 这是**独立于任何账本**的观测:只问端口,不问我们记了什么。"""
        try:
            async with httpx.AsyncClient(timeout=timeout, trust_env=False) as c:
                r = await c.get(f"http://127.0.0.1:{port}/health")
        except Exception:                                    # noqa: BLE001
            # ★ 连不上 = down。**超时也算 down** —— 诚实边界:一个卡死到连 TCP 都不回的
            #   后端,我们区分不出它是"死了"还是"僵着"。宁可在这里说 down,
            #   也不要在 `residency_truth` 里凭空多报一个孤儿。见该方法的诚实边界段。
            return ModelLoader.PORT_DOWN
        return (ModelLoader.PORT_READY if 200 <= r.status_code < 300
                else ModelLoader.PORT_ALIVE)

    async def _health_ok(self, port: int, timeout: float = 2.0) -> bool:
        """★ 判据是 **2xx**,不是"连得上"。llama-server 加载中回 503。
        ★ V16:实现改走 `_port_state`,判据一个字没变 —— 只是把"还活着"那一档分了出来。"""
        return await self._port_state(port, timeout) == self.PORT_READY

    async def _auth_ok(self, port: int, timeout: float = 5.0) -> bool:
        """**带钥匙**打一次受保护的端点 —— 回答的是「钥匙对不对」,不是「起来没起来」。

        ★★★ 它为什么必须存在(2026-08-10 实测):llama-server 的 `/health` 与
          `/v1/models` **不受 api-key 约束**,不带 key 也回 200。
          ⇒ 只看 `/health` 的就绪闸**看不见钥匙不匹配** —— 栈会"启动成功",
            然后每一次对话都 401。那正是 §12.3 禁止的静默降级:
            失败发生在启动,却要等用户第一次说话才显形。
        ★ 打 `/props`(GET,不吃 token、不占 slot)而不是真发一次对话。
        ★ 独立于 `_port_state` 另开一个洞是**有意的**:那个洞回答"端口三态",
          判据是状态码分档;这个洞回答"我方身份被不被接受",判据是 2xx/401。
          混进同一个方法会让 401 变成 PORT_ALIVE,而 `running()` 会据此说它活着 —— 它确实活着,
          但**我们用不了它**,那是两件事。
        """
        try:
            hdrs = backend_key.auth_header()
        except backend_key.BackendKeyError:
            return False
        try:
            async with httpx.AsyncClient(timeout=timeout, trust_env=False) as c:
                r = await c.get(f"http://127.0.0.1:{port}{backend_key.AUTH_PROBE_PATH}",
                                headers=hdrs)
        except Exception:                                    # noqa: BLE001
            return False
        return 200 <= r.status_code < 300

    async def _wait_ready(self, cid: str, port: int, auth_required: bool = False) -> None:
        loop = asyncio.get_running_loop()
        deadline = loop.time() + READY_TIMEOUT_S
        while True:
            if await self._health_ok(port):
                if auth_required and not await self._auth_ok(port):
                    raise LoaderError(
                        f"{cid} 的后端起来了,但**不认我们的钥匙**(带 key 打 "
                        f"{backend_key.AUTH_PROBE_PATH} 没回 2xx)。\n"
                        f"  ★ /health 是绿的 —— 它本来就不受 key 约束,所以这一格只有带鉴权探一次才看得见。\n"
                        f"  最常见的成因:端口上那个后端是**上一把钥匙**起的(密钥文件被删过/换过)。\n"
                        f"  修法:把这个端口上的 llama-server 停掉再装一次。")
                return
            proc = self._procs.get(cid)
            if proc is not None and proc.poll() is not None:
                # ★ 进程自己退了 —— 立刻报,不要傻等到超时。
                #   最常见的原因是显存不够(而三道闸放行了,说明闸的判据与现实有出入,值得查)。
                raise LoaderError(
                    f"{cid} 的后端进程启动后**自己退出了**(退出码 {proc.returncode})。"
                    f"★ 三道闸放行了却起不来 —— 值得查闸的判据与现实是不是出了偏差")
            if loop.time() >= deadline:
                raise LoaderError(f"{cid} 等了 {READY_TIMEOUT_S:.0f} 秒仍未就绪(/health 一直不是 2xx)")
            await asyncio.sleep(READY_POLL_S)

    # ── ★ 独立事实源 ───────────────────────────────────────────────
    async def running(self) -> List[str]:
        """**真的装着**的组件 —— 进程活着**且** /health 回 2xx。

        ★★★ 这是 `actual_resident` 的独立事实源(S7 欠的那笔账)。
          它不查 Broker 的账本,只看现实:端口上有没有一个能服务的后端。
        ★ 同端口多组件(8b 的三档共用 18081):端口活着只能证明**其中之一**在跑,
          所以只报**我们自己起的那个**;认领来的孤儿按端口归属报第一个匹配的,
          并且**如实标注这是推断** —— 见 adopt() 的说明。

        ★★★ V16 · 这个方法**结构上不可能报出「账本说卸了、进程还在」**,
          原因写在这里免得下一个人又拿它当驻留真相的判据:
          它的候选池是 `_procs ∪ _adopted`,也就是**账本自己** ——
          它只能确认或否定账本已经相信的条目,永远探不到账本里没有的那一条。
          ⇒ 要那条**能为假**的判据,看 `residency_truth()`(候选池是**全部登记端口**)。
        """
        out = []
        for cid, proc in list(self._procs.items()):
            if proc.poll() is not None:
                # ★ 进程死了就从账上去掉 —— 留着就是"以为装着实际没有",
                #   而那正是 I2 存在的理由所要禁止的事。
                self._procs.pop(cid, None)
                continue
            port = int(self.cfg.components[cid]["port"])
            if await self._port_state(port) == self.PORT_READY:
                out.append(cid)
        for cid in list(self._adopted):
            port = int(self.cfg.components[cid].get("port") or 0)
            st = await self._port_state(port) if port else self.PORT_DOWN
            if st == self.PORT_READY:
                out.append(cid)
            elif st == self.PORT_DOWN:
                # ★★★ V16 修:这里原来写的是 `if port and await self._health_ok(port): … else: discard`
                #   —— 于是**非 2xx 就丢账**。而 llama-server 加载中回 503(本文件头 :24-27),
                #   `_health_ok` 的超时又只有 2 秒 ⇒ 一次加载中、一次慢响应,
                #   一个**活着且占着 6 GiB** 的后端就被从账本上永久抹掉了,
                #   而抹掉之后 `unload()` 走到 `_kill()` 时 `_procs.pop` 返回 None、静默成功。
                #   ⇒ 这不是"没抓到孤儿",是 `running()` **自己在制造孤儿**。
                #   ★ 只有 down(连不上)才丢账;alive(有响应但没就绪)保留在账上,
                #     它下一轮还会被探到,而且此刻它确实占着显存。
                self._adopted.discard(cid)
        return sorted(set(out))

    # ══════════════════════════════════════════════════════════════════
    #  ★★★ V16 · 驻留真相探针 —— **候选池是登记表,不是账本**
    #
    #  用户裁定(V16)原文:「『Broker 说已卸载』与『进程真的没了』之间要有一条
    #  **能为假**的判据」。`running()` 做不到这件事,理由见它的说明:
    #  它的候选池就是账本,账本忘了的那一条它永远不会去探。
    #
    #  ⇒ 本方法的候选池是 `cfg.components` 里**全部登记过的端口**,去重。
    #    它问的是一个账本回答不了的问题:「哪个端口上有人,而我们说不出他是谁?」
    #    这条判据**能为假**,而且今天就真的会为假(V16 实机复现:孤儿 llama-server
    #    活着占 6.5 GiB,三条不变式全绿)。
    #
    #  ★ 诚实边界,一条都不许省:
    #    ① **同端口多组件**:8b 的 8k/16k/32k 都是 18081(config/vram-budget.toml)。
    #       端口活着只能证明**其中之一**在跑 —— 所以本方法报的是**端口**,不是组件名。
    #       报组件名就是在假装分得清,而 `adopt()` 的诚实边界早就写明分不清。
    #    ② **只判 down/非 down**:一个卡到连 TCP 都不回的后端会被判成 down,
    #       而它可能还占着显存。⇒ 本探针**只会漏报孤儿,不会误报孤儿**,方向是有意选的:
    #       误报会让 I3 因为一次网络抖动就红,而"经常误红的告警"等于没有告警。
    #    ③ port = 0/None 的组件(comfyui)**不在探测范围内** —— 它们不监听端口,
    #       本探针对它们**什么都不知道**,而不是"知道它们不在"。
    # ══════════════════════════════════════════════════════════════════

    def ledger_ports(self) -> Dict[int, List[str]]:
        """账本认为**应该有人**的端口 → 是账本里哪几条撑着它。★ 纯读,不探网。"""
        out: Dict[int, List[str]] = {}
        for cid in list(self._procs) + sorted(self._adopted):
            c = self.cfg.components.get(cid) or {}
            port = int(c.get("port") or 0)
            if port:
                out.setdefault(port, []).append(cid)
        return out

    def registered_ports(self) -> Dict[int, List[str]]:
        """全部**登记过**的端口 → 共用它的组件。★ 候选池就是这里,与账本无关。"""
        out: Dict[int, List[str]] = {}
        for cid, c in self.cfg.components.items():
            port = int(c.get("port") or 0)
            if port:
                out.setdefault(port, []).append(cid)
        return out

    async def residency_truth(self) -> Dict[str, object]:
        """★ 能为假的那条判据:探全部登记端口,报出**账本认不下来**的那些。

        返回 `{"live_ports": [...], "ledger_ports": [...], "orphan_ports": [...],
                "orphan_candidates": {port: [可能是它们中的一个]}, "probed": n}`。
        `orphan_ports` 非空 = 有东西活着占着显存,而我们的账本说不出他是谁。
        """
        reg = self.registered_ports()
        led = self.ledger_ports()
        ports = sorted(reg)
        # ★★ **并发**探,而且用更短的超时。这条探针挂在 1 Hz 采样循环上:
        #   逐个串行探 4 个端口、每个 2 秒超时 ⇒ 一个黑洞端口能把采样循环拖成 8 秒一轮,
        #   而采样循环同时还担着压力让位与 RECONCILING 的出口判据。
        #   ⇒ 代价被 gather 收敛成"一次超时",而不是"端口数 × 超时"。
        states = await asyncio.gather(
            *(self._port_state(p, timeout=self.TRUTH_PROBE_TIMEOUT_S) for p in ports),
            return_exceptions=True)
        live = [p for p, st in zip(ports, states) if st not in (self.PORT_DOWN, )
                and not isinstance(st, BaseException)]
        orphans = [p for p in live if p not in led]
        return {
            "live_ports": live,
            "ledger_ports": sorted(led),
            "orphan_ports": orphans,
            # ★ 只能给出**候选**,给不出名字 —— 见上方诚实边界①
            "orphan_candidates": {p: sorted(reg[p]) for p in orphans},
            "probed": len(reg),
            "note": "候选池是**全部登记端口**,不是账本 —— 这是它能为假的原因。"
                    "同端口多组件时只报端口,不报组件名(分不清就不假装分得清)。",
        }

    async def verify_unloaded(self, ids: List[str], keep: List[str]) -> List[Dict[str, object]]:
        """★★★ 卸载**核实**:这几个真的没了吗?

        `keep` = 卸完之后**仍然应该活着**的组件。★ 必须传它,否则同端口多组件时
        会把「隔壁那一档还在跑」误判成「没卸干净」—— 8b 三档共用 18081。

        返回**仍然活着**的条目;空列表 = 核实通过。
        ★ 这个返回值是 V16 之前**整条卸载路径上唯一缺的东西**:
          旧的 `unload()` 返回 None,`_kill()` 永不抛异常,于是**每一次卸载都成功**。
        """
        keep_ports = set()
        for cid in keep:
            c = self.cfg.components.get(cid) or {}
            if int(c.get("port") or 0):
                keep_ports.add(int(c["port"]))
        out: List[Dict[str, object]] = []
        for cid in ids:
            c = self.cfg.components.get(cid) or {}
            port = int(c.get("port") or 0)
            if not port or port in keep_ports:
                continue                      # 不监听端口 / 该端口本来就还有人 —— 说不出话
            st = await self._port_state(port)
            if st != self.PORT_DOWN:
                proc = self._procs.get(cid)
                out.append({
                    "component": cid, "port": port, "port_state": st,
                    "we_spawned_it": proc is not None,
                    "pid": (proc.pid if proc is not None else None),
                    "why": ("我们起的进程杀不掉" if proc is not None else
                            "不是我们起的(认领来的孤儿),按边界不杀 —— 但它**还占着显存**"),
                })
        return out

    async def adopt(self) -> List[str]:
        """认领已经在跑的后端(中枢重启后的孤儿)。

        ★★ 不认领的后果二选一,都很坏:
          · Broker 以为没装 → 再起一个 → **端口冲突**,新进程起不来;
          · Broker 以为装了 → 账本与现实分家。
        ⇒ 以**现实**为准:端口上有能服务的后端,就认下来。

        ★ 诚实边界:同一个端口被多个组件共用(8b 的 8k/16k/32k 都是 18081),
          **光看端口分不出是哪一档**。这里按登记顺序取第一个匹配的,
          并把它记进 `_adopted` —— 调用方据此知道这是**推断**而非确证。
          ⇒ 真要分清,得让后端自报(llama-server 的 /props 有 n_ctx),那是后话;
            **现在不假装分得清**。
        """
        found = []
        seen_ports = set()
        for cid, c in self.cfg.components.items():
            port = int(c.get("port") or 0)
            if not port or port in seen_ports or cid in self._procs:
                continue
            if await self._health_ok(port):
                seen_ports.add(port)
                self._adopted.add(cid)
                found.append(cid)
        return sorted(found)

    async def readopt(self, ids: List[str]) -> List[str]:
        """★★★ V16:把「以为卸掉了、其实还活着」的那些**认回账上**。

        ★ 为什么必须有这个动作:`unload()` 对认领来的孤儿只是 `discard`,
          而 `verify_unloaded()` 刚刚证明它还在端口上服务着。此时账本若继续
          假装它不存在,`running()` 就再也不会探它 —— 那正是 V16 复现里
          「一个 6.5 GiB 的进程活着,而三条不变式全绿」的成因。
        ★ 认回来**不等于**我们要杀它:边界一个字没动(不是我们起的,不该由我们杀)。
          认回来只是**不再说谎**。
        """
        back = []
        for cid in ids:
            c = self.cfg.components.get(cid) or {}
            port = int(c.get("port") or 0)
            if not port or cid in self._procs:
                continue
            if await self._port_state(port) != self.PORT_DOWN:
                self._adopted.add(cid)
                back.append(cid)
        return sorted(back)

    # ── 装 / 卸 ────────────────────────────────────────────────────
    async def load(self, ids: List[str]) -> None:
        """把这些组件装上。★ 任何一个失败就抛 —— S8 的事务据此走回滚。"""
        for cid in ids:
            if cid in self._procs or cid in self._adopted:
                continue                                     # 已经在跑
            c = self._spec(cid)
            port = int(c["port"])
            if await self._health_ok(port):
                # ★ 端口已经有人 —— 认领而不是再起一个(否则端口冲突)
                self._adopted.add(cid)
                continue
            auth_required = False
            if str(c.get("kind")) == "speech":
                # ★ speech 后端:自己那个 venv 的 python 起 10-core/speech/server.py。
                #   档位由组件 id 决定(speech.full -> --full),与 D27 的档位映射一致。
                # ★★ D?:speech **不带**这把 key —— 它不是 llama-server,没有
                #   `--api-key-file`。这一条是**如实的边界,不是遗漏**:语音后端至今
                #   仍是"同机任何进程都连得上"。要堵它得给 10-core/speech/server.py
                #   自己加一层,那是 P5 的账,不在本车道。
                #   ⇒ `90-ops/gate/check_backend_auth.py` 把这条边界写成了断言:
                #     它只对 **llama 系**的起法要求带 key,而 speech 那一处**登记在案**。
                spec = self._speech_spec()
                py = _speech_python(spec)
                args = [str(py), str(_repo_root() / "10-core" / "speech" / "server.py")]
                if cid.endswith(".full"):
                    args.append("--full")
            else:
                exe = self._llama_exe()
                model = self._model_path(str(c["model_rel"]))
                # ★★★ D?:**起后端就必须上锁**。没有这一段,同机任何进程
                #   (尤其是跑成 ai-exec 的跑腿 worker)都能直连这个端口绕过网关与 E1/审计
                #   —— 回环不过防火墙、ACL 管不了 TCP,钥匙是唯一拦得住的东西。
                # ★ `ensure_key_file()` 会顺带把密钥目录的 ACL 重新收紧并核验,
                #   失败就抛 —— 于是"锁没上"永远不会长得像"起来了"。
                args = [str(exe), "-m", str(model), "-ngl", str(int(c["ngl"])),
                        "-c", str(int(c["ctx"])), "--host", "127.0.0.1", "--port", str(port),
                        "-fa", "on", "-ctk", "q8_0", "-ctv", "q8_0",
                        "--api-key-file", str(backend_key.ensure_key_file())]
                auth_required = True
            try:
                # ★ 不继承父进程的控制台:中枢将来做成服务时没有控制台可继承。
                proc = subprocess.Popen(
                    args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            except Exception as e:                           # noqa: BLE001
                raise LoaderError(f"{cid} 起不来:{type(e).__name__}: {e}") from e
            self._procs[cid] = proc
            try:
                await self._wait_ready(cid, port, auth_required=auth_required)
            except Exception:
                # ★ 就绪失败要**把自己起的那个收掉** —— 留一个半死的进程占着端口,
                #   下一次装载会以为"端口有人"而认领它,于是账本指向一个不能服务的后端。
                await self._kill(cid)
                raise

    async def unload(self, ids: List[str]) -> Dict[str, object]:
        """卸掉这些组件。★★★ V16:**返回一份如实的回执**,不再是 None。

        ★★ **认领来的孤儿不杀** —— 不是我起的进程,我不知道谁还在用它。
          杀别人起的进程是一次不可撤销的越权动作;如实跳过并让调用方知道。

        ★★★ V16 · 「如实跳过并让调用方知道」这句话**此前是假的**:
          旧签名是 `-> None`,认领来的那条 `discard` 之后直接 `continue`,
          三个调用方(apply_intended / sweep_idle_transient / yield_under_pressure)
          **一个字都收不到**。于是「我们没杀它」与「它已经没了」在账上完全同形 ——
          V16 实机复现的那台孤儿 llama-server(6.5 GiB)正是从这条缝里漏出去的。
        ⇒ 回执逐条分开,因为下一步动作完全不同:
            killed          我们起的、确认已经没了      → 正常
            skipped_adopted 不是我们起的,按边界没动它  → **它可能还占着显存**
            kill_failed     我们起的,但杀不掉          → 真故障,必须响
        """
        rep: Dict[str, object] = {"killed": [], "skipped_adopted": [], "kill_failed": []}
        for cid in ids:
            if cid in self._adopted:
                self._adopted.discard(cid)
                rep["skipped_adopted"].append(cid)            # type: ignore[union-attr]
                continue                                     # ★ 不杀别人起的
            (rep["killed"] if await self._kill(cid)            # type: ignore[union-attr]
             else rep["kill_failed"]).append(cid)              # type: ignore[union-attr]
        return rep

    async def _kill(self, cid: str) -> bool:
        """收掉自己起的那个。返回**它是不是真的没了**。

        ★★★ V16 三处修正,每一处都曾经让"杀失败"长得和"杀成功"一样:
          ① `pop` 原来是**第一句** —— 无论后面杀没杀成,句柄都已经丢了,
             于是这个进程**再也没有任何一条路径能杀它第二次**,而 `running()`
             也永远不会再探它。⇒ 改成**确认死亡之后才 pop**。
          ② `kill()` 之后**没有任何核实**就返回。Windows 上 `Popen.kill` 与
             `terminate` 都是 TerminateProcess —— terminate 失败时 kill 会以同样的
             理由失败,而旧代码把两次异常都 `pass` 掉。⇒ 硬杀后再等一小段并核实。
          ③ 返回 None ⇒ 调用方无从判断。⇒ 返回布尔,由 `unload` 汇进回执。
        """
        proc = self._procs.get(cid)
        if proc is None:
            # ★ 账本里没有这一条。**这不等于"它没了"** —— 只等于"我们没在管它"。
            #   真相由 `residency_truth()` / `verify_unloaded()` 回答,不由这里假装。
            return True
        if proc.poll() is not None:
            self._procs.pop(cid, None)
            return True
        try:
            proc.terminate()
        except Exception:                                    # noqa: BLE001
            pass
        # ★ 给它一点时间自己收(llama-server 要释放显存);超时才硬杀。
        #   硬杀本身不会让显存"丢",但驱动回收要时间 —— S8 的 _await_reclaim 会等。
        for _ in range(50):
            if proc.poll() is not None:
                self._procs.pop(cid, None)
                return True
            await asyncio.sleep(0.1)
        try:
            proc.kill()
        except Exception:                                    # noqa: BLE001
            pass
        for _ in range(20):                                  # ★ 硬杀之后**再核实一次**
            if proc.poll() is not None:
                self._procs.pop(cid, None)
                return True
            await asyncio.sleep(0.1)
        # ★★ 杀不掉 —— **句柄留在账上**。留着它,下一次卸载还能再试;
        #   丢掉它才是把一个活着的 6 GiB 进程变成谁也够不着的孤儿。
        return False

    async def shutdown(self) -> None:
        """中枢退出时收掉**自己起的**那些。★ 认领的不动(见 unload 的说明)。"""
        for cid in list(self._procs):
            await self._kill(cid)

    @property
    def adopted(self) -> List[str]:
        """认领来的组件 —— ★ 这些是**推断**出来的(见 adopt 的诚实边界),不是确证。"""
        return sorted(self._adopted)
