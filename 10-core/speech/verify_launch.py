r"""★★★ 把「speech 怎么起」这件事**真的验证一遍** —— 它是 launch.toml [verified] 段的来源。

跑:  <speech venv>\Scripts\python.exe 10-core\speech\verify_launch.py

════════════════════════════════════════════════════════════════════════════
 为什么要有这个文件

 `model_loader.py` 文件头那条规矩:
   「speech / vlm / comfyui 怎么起没有人验证过。装载器对它们如实报
     『启动方式尚未验证』,不猜。猜一套参数的后果不是"可能起不来",
     是**看起来支持而第一次真用时才炸**。」

 ⇒ 要让装载器不再报那句话,不能靠"我写了一份规格",只能靠**真的起过一次**。
   这个脚本就是那一次:真的加载权重、真的转写、真的合成,把读数打出来。
   `launch.toml` 的 [verified] 段抄的就是这里的输出。

 ★ 它**不进门禁**:加载 1.6–2.9 GB 权重要十几秒,且它验的是"这台机器上的权重与
   运行时",换台机器就该重跑 —— 进门禁会因"换台机器"而红,而那种红会训练人
   用 --no-verify(ASSERTION-PITFALLS 第 5 条已经量过这个代价)。
   ⇒ 与 90-ops 的 verify-*.ps1 同一档:**只读、手动、写明什么时候该重跑**。

 ★★ 它与 selftest.py 分工明确,谁也不替谁背书:
   · selftest.py  钉**契约形状**(桩引擎驱动真 Handler,快,进门禁)
   · 本文件        钉**权重与运行时真的能起来**(真加载,慢,手动)
"""
from __future__ import annotations

import io
import math
import os
import struct
import sys
import time
import wave
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))


def _tone_wav(seconds: float = 1.0, rate: int = 16000, hz: float = 440.0) -> bytes:
    """造一段正弦音 —— ★ 不依赖任何素材文件,这样这个脚本在裸机上也跑得起来。"""
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        n = int(rate * seconds)
        w.writeframes(b"".join(
            struct.pack("<h", int(3000 * math.sin(2 * math.pi * hz * i / rate))) for i in range(n)))
    return buf.getvalue()


def main() -> int:
    import server as S

    spec = S.load_launch_spec()
    a = spec["asr"]
    ok = True

    # ★★★ 硬断网:任何一次联网尝试**立刻失败**,而不是慢慢超时。
    #   这一条是本次验证的核心 —— 它把"权重在不在本地"从一个猜测变成一个结论。
    if a.get("hub_offline", True):
        os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ.setdefault("HF_HOME", str(S._cache_root() / "hf"))
    print(f"[env] HF_HUB_OFFLINE={os.environ.get('HF_HUB_OFFLINE')}  HF_HOME={os.environ.get('HF_HOME')}")

    import ctranslate2
    import onnxruntime
    from faster_whisper import WhisperModel
    from piper import PiperVoice
    import faster_whisper

    print(f"[ver] faster_whisper={faster_whisper.__version__} ctranslate2={ctranslate2.__version__} "
          f"onnxruntime={onnxruntime.__version__} providers={onnxruntime.get_available_providers()}")

    tone = _tone_wav()

    # ── ASR:两个档位都真的加载一次 ────────────────────────────────────────
    for tier, repo in (("lite", a["lite_repo"]), ("full", a["full_repo"])):
        try:
            t = time.time()
            m = WhisperModel(repo, device=a["device"], compute_type=a["compute_type"],
                             local_files_only=bool(a["local_files_only"]))
            load_s = time.time() - t
            t = time.time()
            segs, info = m.transcribe(io.BytesIO(tone), beam_size=1)
            txt = "".join(s.text for s in segs)
            print(f"[asr:{tier}] LOADED {repo} in {load_s:.1f}s · transcribe {time.time()-t:.1f}s "
                  f"lang={info.language} text={txt[:40]!r}")
        except Exception as ex:  # noqa: BLE001
            ok = False
            print(f"[asr:{tier}] X 起不来:{type(ex).__name__}: {ex}")
            print("           ★ 若是找不到权重:这**正是**要的结果 —— 本服务不联网补齐。"
                  "请把权重预置到本地(见决议包里那条「权重不该住在 cache 根」的记账)。")

    # ── TTS ───────────────────────────────────────────────────────────────
    try:
        vp = S._voice_path(spec)
        t = time.time()
        v = PiperVoice.load(str(vp))
        load_s = time.time() - t
        buf = io.BytesIO()
        t = time.time()
        with wave.open(buf, "wb") as w:
            v.synthesize_wav("Hello from the local hub.", w)
        synth_s = time.time() - t
        with wave.open(io.BytesIO(buf.getvalue()), "rb") as r:
            print(f"[tts] LOADED {vp.name} in {load_s:.2f}s · synth {synth_s:.2f}s "
                  f"· {r.getframerate()} Hz {r.getnframes()} frames")
    except Exception as ex:  # noqa: BLE001
        ok = False
        print(f"[tts] X 起不来:{type(ex).__name__}: {ex}")

    print("-" * 70)
    print("  === speech verify-launch: " + ("OK —— 可以把上面的读数写回 launch.toml [verified]"
                                            if ok else "FAILED —— 不许把 [verified] 写上去") + " ===")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
