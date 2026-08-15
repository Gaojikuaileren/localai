r"""P5 语音 v1 · speech 后端(ASR + TTS,**全 CPU**)—— D? · 骨干链路。

════════════════════════════════════════════════════════════════════════════
 ★★★ 这个服务存在的判据,不是"我接好了",是一个**能变的量**:

   `model_loader.py` 文件头写着 ——
     「speech / vlm / comfyui 怎么起没有人验证过。装载器对它们如实报
       『启动方式尚未验证』,不猜。」

   ⇒ 本车道做完的标志 = 装载器对 speech **不再报那句话**,
     因为 `10-core/speech/launch.toml` 的 [verified] 段是真的跑出来过的。

════════════════════════════════════════════════════════════════════════════
 ★★ 为什么只用标准库的 http.server,不用 FastAPI

 实测:speech venv 里**没有** fastapi / uvicorn / starlette / pydantic
 (有的是 faster_whisper 1.2.1 · ctranslate2 4.8.1 · piper_tts 1.6.0 · onnxruntime 1.28.0)。
 装它们要么联网、要么改动 models/state 那几个根 —— 而这套装置的底线是**全本地**。
 ⇒ 用标准库,新依赖为 0。代价是要自己写路由和 JSON,收益是这个服务
   **在一台断网机器上、不装任何东西**就能起来。这条路径本身就是被测的东西。

════════════════════════════════════════════════════════════════════════════
 ★★★ 一条架构底线(STATE 明写,不许违反):**同传可以失败,用户的麦克风不可以。**

 本服务对这条底线的落实方式是**结构性的,不是 try-catch**:

   · 采集(麦克风 → 音频缓冲)**根本不在这个进程里** —— 它在客户端。
     本服务是那条数据的**消费者**,不是它的**通路**。
   · ⇒ 本服务整个挂掉 / 端口占用 / 模型权重被删,
     用户对着麦克风说话这件事**在代码路径上就到不了这里**,因此不受影响。
   · 反过来说也成立、而且是本文件的硬约束:
     **本服务绝不持有、也绝不代理任何实时音频通路** ——
     它只接收"一段已经录好的音频",返回文字;或接收文字,返回一段音频。
     一旦有人在这里加一条"实时转发麦克风"的路径,那条底线就从结构性退化成靠自觉。
     ⇒ 已用断言钉住(见 selftest 的 `no_realtime_passthrough`)。

════════════════════════════════════════════════════════════════════════════
 ★ 跨进程契约(D92/D95):本服务三条端点各有一个 `CONTRACT:speech.*` 契约号,
   服务端在这里钉顶层键集合,客户端在自己那侧钉「拿这形状能解析出目标字段」,
   并登记进 `90-ops/gate/check_contract_pairs.py`。

用法:
    python 10-core/speech/server.py            # 起服务(读 launch.toml)
    python 10-core/speech/server.py --selftest # 自检(不起服务,不加载大模型)
"""
from __future__ import annotations

import base64
import io
import json
import os
import sys
import threading
import time
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, Dict, Optional, Tuple

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]

# ── 契约号 —— ★ 两侧共用同一个锚点,且必须是 ASCII ─────────────────────────
#    (ASSERTION-PITFALLS 第 8 条:机器读的那几个字符在 cp936 下必须仍然完好)
CONTRACT_HEALTH = "CONTRACT:speech.health"
CONTRACT_ASR = "CONTRACT:speech.asr"
CONTRACT_TTS = "CONTRACT:speech.tts"

def load_contracts() -> Dict[str, frozenset]:
    """
    读 `contracts.json` —— 三条端点的**顶层键集合**。

    ★★ 服务端与消费者(gateway 侧的 speech_proxy)**读的是同一份文件**。
      两边各写一份常量的话,它们分家那天**两边都不会红** —— 那正是审计 A1 的形状。
      期望值只有一份,它没法跟自己分家。
    """
    with open(HERE / "contracts.json", "rb") as f:
        raw = json.load(f)
    return {cid: frozenset(v["keys"]) for cid, v in raw["contracts"].items()}


#: 三条端点的顶层键集合(从 contracts.json 读,**不在这里另抄一份**)。
CONTRACT_KEYS: Dict[str, frozenset] = load_contracts()

# ══════════════════════════════════════════════════════════════════════════
#  ★★★ provenance —— 这是**安全判据**,不是一个配置字段
#
#  任务原话:「只有本机 / 已认证 LAN 设备的麦克风才可用 provenance `user_voice_asr`。
#            来源档位由**通道**决定,不由调用方自报。」
#
#  ⇒ 本服务**永远不读**请求体里的 provenance。它由**连接是怎么进来的**推出:
#      · 回环(127.0.0.1)              -> user_voice_asr   本机麦克风
#      · 带了 Edge 注入的设备指纹头     -> user_voice_asr   已认证 LAN 设备
#        (那个头由 lan-edge 在 mTLS 校验之后**覆盖写入**,客户端伪造不了 ——
#         lan-edge 会先剥掉客户端自带的 X-LocalAI-*,再写自己验过的那个)
#      · 其余一切                       -> untrusted_audio 记忆写入侧据此拒绝
#
#  ★ 为什么这条必须在**服务端**定:客户端自报的东西一律不可信,
#    而"这段音频是不是真的来自一支可信的麦克风"决定了它能不能直通记忆库。
#    让调用方自己说,等于把记忆库的准入交给调用方。
# ══════════════════════════════════════════════════════════════════════════
PROV_TRUSTED = "user_voice_asr"
PROV_UNTRUSTED = "untrusted_audio"

#: lan-edge 在 mTLS 校验后注入的**已验证**指纹头(客户端自带的同名头会被它剥掉)。
VERIFIED_FP_HEADER = "X-LocalAI-Cert-Sha256"


def load_launch_spec(path: Optional[Path] = None) -> Dict[str, Any]:
    """读 launch.toml。★ 缺 [verified] 段就**不是**一份可用的规格 —— 见文件头。"""
    import tomllib

    p = path or (HERE / "launch.toml")
    with open(p, "rb") as f:
        spec = tomllib.load(f)
    if "verified" not in spec:
        raise RuntimeError(
            f"{p} 缺 [verified] 段 —— 未经实测的启动规格一律不认(见 model_loader 文件头那条规矩)"
        )
    return spec


def add_cuda_dll_dirs() -> list:
    """
    让 `device = "cuda"` **真的能用** —— 否则它是一颗「装得上、一转写就炸」的雷。

    ★★★ 实测(V38 · 2026-08-15,`measure_asr_latency.py`):把 device 换成 `cuda` 之后
      `WhisperModel(...)` **加载成功**(1.27s,看起来一切正常),而**第一次** `transcribe()`
      抛 `RuntimeError: Library cublas64_12.dll is not found or cannot be loaded`。
      ⇒ 这正是 `model_loader.py` 文件头点名要防的那个形状:
        **看起来支持,第一次真用时才炸**。所以它是缺陷,不是配置问题。

    原因:本机 CUDA 运行时是 **pip wheel** 装的(`nvidia-cublas-cu12` 等),
    DLL 躺在 `<venv>/Lib/site-packages/nvidia/*/bin`,而那几个目录**不在 DLL 搜索路径上**。

    ★★ 为什么用 `PATH` 而不是 `os.add_dll_directory()`:两个都试过,
      **只有 PATH 那条有效**。`add_dll_directory` 只影响 `LoadLibraryEx` 带
      `LOAD_LIBRARY_SEARCH_*` 标志的调用;而 CTranslate2 是从**原生代码**里加载 cuBLAS 的,
      走的是标准搜索序 ⇒ 它看不见 `add_dll_directory` 的名单,只看得见 `PATH`。
      (PyTorch 自己会做这件事,CTranslate2 **不做** —— 所以这里补。)

    ★ 只在 device 非 cpu 时调用:CPU 那条路今天一个字节都不该受影响。
    """
    import glob

    added = []
    for d in sorted(glob.glob(str(Path(sys.prefix) / "Lib" / "site-packages" / "nvidia" / "*" / "bin"))):
        if os.path.isdir(d):
            os.environ["PATH"] = d + os.pathsep + os.environ["PATH"]
            added.append(d)
    return added


def provenance_for(client_host: str, headers: Any) -> str:
    """
    由**通道**判定来源档位。★ 不看请求体,不看任何调用方自报的字段。

    ★ 回环之所以算可信:能连上 127.0.0.1 就已经在这台机器上了,
      而这台机器就是中枢本身 —— 它的麦克风就是"本机麦克风"。
    ★ LAN 设备之所以算可信:那个指纹头是 lan-edge 在 mTLS 通过**之后**写的,
      而它会先把客户端自带的同名头剥掉(见 lan-edge 的 header 处理)。
      ⇒ 头在 = 这条连接被一张 active 成员证书验过了。
    """
    if client_host in ("127.0.0.1", "::1", "localhost"):
        return PROV_TRUSTED
    fp = None
    try:
        fp = headers.get(VERIFIED_FP_HEADER)
    except Exception:  # noqa: BLE001
        fp = None
    if fp:
        return PROV_TRUSTED
    return PROV_UNTRUSTED


class SpeechEngines:
    """
    ASR + TTS 的**惰性**加载器。

    ★ 为什么惰性:/health 必须在进程刚起来时就能回 503 而不是超时 ——
      "进程活着但还没就绪"是一个要能被**观测到**的状态(与 llama-server 同形状)。
    ★ 加载失败**不吞**:记进 `detail` 并让 /health 一直回 503。
      一个"起来了但什么都做不了"的服务比没起来更坏 —— 装载器会以为它装上了。
    """

    def __init__(self, spec: Dict[str, Any], tier: str = "lite") -> None:
        self.spec = spec
        self.tier = tier
        self.asr = None
        self.tts = None
        self.detail = "尚未开始加载"
        self._lock = threading.Lock()

    # ── ASR ────────────────────────────────────────────────────────────────
    def _asr_repo(self) -> str:
        a = self.spec["asr"]
        return a["full_repo"] if self.tier == "full" else a["lite_repo"]

    def load(self) -> None:
        with self._lock:
            if self.asr is not None and self.tts is not None:
                return
            a = self.spec["asr"]
            # ★★★ 双保险的**进程层**那一半:必须在 import 之前设。
            #   HF_HUB_OFFLINE=1 让任何一次联网尝试**立刻失败**,而不是慢慢超时 ——
            #   后者会把"权重不在本地"表现成"启动很慢",而那是最难查的一种。
            if a.get("hub_offline", True):
                os.environ["HF_HUB_OFFLINE"] = "1"
            os.environ.setdefault("HF_HOME", str(_cache_root() / "hf"))

            # ★ 非 CPU 档必须先把 CUDA 的 DLL 目录喂进 PATH,否则**加载会成功、
            #   第一次转写才炸**(见 add_cuda_dll_dirs 的实测记录)。
            #   今天 launch.toml 写的是 cpu ⇒ 这一支走不到;它守的是**改档那一刻**。
            if str(a.get("device", "cpu")).lower() != "cpu":
                add_cuda_dll_dirs()

            try:
                from faster_whisper import WhisperModel

                self.asr = WhisperModel(
                    self._asr_repo(),
                    device=a.get("device", "cpu"),
                    compute_type=a.get("compute_type", "int8"),
                    # ★★ 库层那一半:默认联网拉权重(STATE:643 / D41),这里钉死。
                    local_files_only=bool(a.get("local_files_only", True)),
                )
            except Exception as ex:  # noqa: BLE001
                self.detail = (
                    f"ASR 加载失败({self._asr_repo()}):{ex}。"
                    "★ 若是找不到权重:本服务**不会**联网补齐(local_files_only + HF_HUB_OFFLINE),"
                    "这是有意的 —— 请把权重预置到本地再起。"
                )
                raise

            try:
                from piper import PiperVoice

                self.tts = PiperVoice.load(str(_voice_path(self.spec)))
            except Exception as ex:  # noqa: BLE001
                self.detail = f"TTS 加载失败:{ex}"
                raise

            self.detail = f"就绪(tier={self.tier}, asr={self._asr_repo()})"

    def ready(self) -> bool:
        return self.asr is not None and self.tts is not None

    def transcribe(self, wav_bytes: bytes) -> Tuple[str, str, float]:
        segs, info = self.asr.transcribe(io.BytesIO(wav_bytes), beam_size=1)
        text = "".join(s.text for s in segs).strip()
        return text, (info.language or ""), float(info.duration or 0.0)

    def synthesize(self, text: str) -> Tuple[bytes, int, int]:
        buf = io.BytesIO()
        with wave.open(buf, "wb") as w:
            self.tts.synthesize_wav(text, w)
        raw = buf.getvalue()
        with wave.open(io.BytesIO(raw), "rb") as r:
            return raw, r.getframerate(), r.getnframes()


def _read_paths_toml() -> Dict[str, Any]:
    import tomllib

    with open(REPO / "config" / "paths.toml", "rb") as f:
        return tomllib.load(f)


def _models_root() -> Path:
    return Path(_read_paths_toml()["roots"]["models"])


def _cache_root() -> Path:
    return Path(_read_paths_toml()["roots"]["cache"])


def _voice_path(spec: Dict[str, Any]) -> Path:
    """
    默认语音的 .onnx 路径。★ 只在 models 根下找 —— 不去 cache 根碰运气。
    (paths.toml 自己写着 cache 是"唯一可静默清理的根";把权重指到那儿,
     等于让一次正常的清理把服务弄坏。见本车道决议包里那条实测记账。)
    """
    t = spec["tts"]
    root = _models_root() / t["voices_rel"]
    want = t["default_voice"] + ".onnx"
    for p in root.rglob(want):
        return p
    raise FileNotFoundError(f"找不到 Piper 语音 {want}(在 {root} 下)")


class Handler(BaseHTTPRequestHandler):
    engines: SpeechEngines = None  # type: ignore[assignment]
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt: str, *args: Any) -> None:  # 静音默认访问日志
        pass

    # ── 工具 ───────────────────────────────────────────────────────────────
    def _json(self, code: int, obj: Dict[str, Any]) -> None:
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _body(self) -> bytes:
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n) if n else b""

    # ── 路由 ───────────────────────────────────────────────────────────────
    def do_GET(self) -> None:  # noqa: N802
        if self.path.split("?")[0] == "/health":
            e = self.engines
            ready = e.ready()
            # ★ 与 llama-server 同一条规矩:没就绪回 **503**,不是 200。
            #   进程活着 ≠ 能服务(model_loader 文件头第 ② 条)。
            self._json(200 if ready else 503, {
                "ok": True,
                "ready": ready,
                "kind": "speech",
                "tier": e.tier,
                "asr_loaded": e.asr is not None,
                "tts_loaded": e.tts is not None,
                "detail": e.detail,
            })
            return
        self._json(404, {"error": "not found"})

    def do_POST(self) -> None:  # noqa: N802
        path = self.path.split("?")[0]
        e = self.engines
        if not e.ready():
            self._json(503, {"error": "not ready", "detail": e.detail})
            return
        try:
            if path == "/v1/speech/asr":
                raw = self._body()
                # ★ provenance 由**通道**定,不看请求体 —— 见文件头那段。
                prov = provenance_for(self.client_address[0], self.headers)
                text, lang, dur = e.transcribe(raw)
                self._json(200, {
                    "text": text,
                    "language": lang,
                    "duration_s": round(dur, 3),
                    "tier": e.tier,
                    "provenance": prov,
                })
                return
            if path == "/v1/speech/tts":
                req = json.loads(self._body() or b"{}")
                text = (req.get("text") or "").strip()
                if not text:
                    self._json(400, {"error": "text is empty"})
                    return
                wav, rate, frames = e.synthesize(text)
                self._json(200, {
                    "audio_b64": base64.b64encode(wav).decode("ascii"),
                    "sample_rate": rate,
                    "format": "wav",
                    "voice": e.spec["tts"]["default_voice"],
                    "frames": frames,
                })
                return
        except Exception as ex:  # noqa: BLE001
            self._json(500, {"error": type(ex).__name__, "detail": str(ex)})
            return
        self._json(404, {"error": "not found"})


def serve(tier: str = "lite") -> int:
    spec = load_launch_spec()
    port = int(spec["service"]["port"])
    engines = SpeechEngines(spec, tier=tier)
    Handler.engines = engines

    # ★ 先起监听、再加载模型 —— 这样"正在加载"是一个**可观测**的状态(/health 503),
    #   而不是一段"端口还没开"的黑洞。装载器的就绪判据靠的就是这个。
    srv = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    threading.Thread(target=engines.load, daemon=True).start()
    print(f"[speech] listening on 127.0.0.1:{port} (tier={tier}) — /health 就绪前回 503")
    srv.serve_forever()
    return 0


if __name__ == "__main__":
    if "--selftest" in sys.argv:
        from selftest import main as _selftest_main  # type: ignore

        sys.exit(_selftest_main())
    _tier = "full" if "--full" in sys.argv else "lite"
    sys.exit(serve(_tier))
