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

#: 已验证过启动方式的 kind。★ 反向全表:不在表里的一律拒绝,不试着用别的方式起。
SUPPORTED_KINDS: Set[str] = {"llm"}

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
        for need in ("model_rel", "ctx", "ngl", "port"):
            if c.get(need) in (None, ""):
                raise LoaderError(f"组件 {cid} 缺启动参数 {need}(见 config/vram-budget.toml)")
        return c

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
    @staticmethod
    async def _health_ok(port: int, timeout: float = 2.0) -> bool:
        """★ 判据是 **2xx**,不是"连得上"。llama-server 加载中回 503。"""
        try:
            async with httpx.AsyncClient(timeout=timeout, trust_env=False) as c:
                r = await c.get(f"http://127.0.0.1:{port}/health")
                return 200 <= r.status_code < 300
        except Exception:                                    # noqa: BLE001
            return False

    async def _wait_ready(self, cid: str, port: int) -> None:
        loop = asyncio.get_running_loop()
        deadline = loop.time() + READY_TIMEOUT_S
        while True:
            if await self._health_ok(port):
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
        """
        out = []
        for cid, proc in list(self._procs.items()):
            if proc.poll() is not None:
                # ★ 进程死了就从账上去掉 —— 留着就是"以为装着实际没有",
                #   而那正是 I2 存在的理由所要禁止的事。
                self._procs.pop(cid, None)
                continue
            port = int(self.cfg.components[cid]["port"])
            if await self._health_ok(port):
                out.append(cid)
        for cid in list(self._adopted):
            port = int(self.cfg.components[cid].get("port") or 0)
            if port and await self._health_ok(port):
                out.append(cid)
            else:
                self._adopted.discard(cid)
        return sorted(set(out))

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
            exe = self._llama_exe()
            model = self._model_path(str(c["model_rel"]))
            args = [str(exe), "-m", str(model), "-ngl", str(int(c["ngl"])),
                    "-c", str(int(c["ctx"])), "--host", "127.0.0.1", "--port", str(port),
                    "-fa", "on", "-ctk", "q8_0", "-ctv", "q8_0"]
            try:
                # ★ 不继承父进程的控制台:中枢将来做成服务时没有控制台可继承。
                proc = subprocess.Popen(
                    args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            except Exception as e:                           # noqa: BLE001
                raise LoaderError(f"{cid} 起不来:{type(e).__name__}: {e}") from e
            self._procs[cid] = proc
            try:
                await self._wait_ready(cid, port)
            except Exception:
                # ★ 就绪失败要**把自己起的那个收掉** —— 留一个半死的进程占着端口,
                #   下一次装载会以为"端口有人"而认领它,于是账本指向一个不能服务的后端。
                await self._kill(cid)
                raise

    async def unload(self, ids: List[str]) -> None:
        """卸掉这些组件。

        ★★ **认领来的孤儿不杀** —— 不是我起的进程,我不知道谁还在用它。
          杀别人起的进程是一次不可撤销的越权动作;如实跳过并让调用方知道。
        """
        for cid in ids:
            if cid in self._adopted:
                self._adopted.discard(cid)
                continue                                     # ★ 不杀别人起的
            await self._kill(cid)

    async def _kill(self, cid: str) -> None:
        proc = self._procs.pop(cid, None)
        if proc is None or proc.poll() is not None:
            return
        try:
            proc.terminate()
        except Exception:                                    # noqa: BLE001
            pass
        # ★ 给它一点时间自己收(llama-server 要释放显存);超时才硬杀。
        #   硬杀本身不会让显存"丢",但驱动回收要时间 —— S8 的 _await_reclaim 会等。
        for _ in range(50):
            if proc.poll() is not None:
                return
            await asyncio.sleep(0.1)
        try:
            proc.kill()
        except Exception:                                    # noqa: BLE001
            pass

    async def shutdown(self) -> None:
        """中枢退出时收掉**自己起的**那些。★ 认领的不动(见 unload 的说明)。"""
        for cid in list(self._procs):
            await self._kill(cid)

    @property
    def adopted(self) -> List[str]:
        """认领来的组件 —— ★ 这些是**推断**出来的(见 adopt 的诚实边界),不是确证。"""
        return sorted(self._adopted)
