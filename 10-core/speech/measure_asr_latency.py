r"""★ ASR 的**稳态**延迟实测 —— D? · V38。

跑:  <speech venv>\Scripts\python.exe 10-core\speech\measure_asr_latency.py
     --gpu 追加一轮 CUDA 对照(不改任何配置,只是量一次)

════════════════════════════════════════════════════════════════════════════
 ★★★ 这个文件存在的理由:仓库里关于同一件事有**两个差 15 倍的数**,
      而「按住说话到底好不好用」完全取决于哪个是真的。

   · `launch.toml` 的 [verified] 段:转 1 秒音频要 **5.6 s**(lite)
   · `PROJECT_PLAN:1242` / `STATE:996`:CPU ASR 实时率 **0.385**(1 秒音频 0.385 秒)

   两个数**都不是错的** —— 它们量的是两件不同的事:
     · 5.6 s 那个是 `verify_launch.py:85-90` 量的:**加载完立刻**转一段
       440 Hz **正弦音**。那是**冷启第一次调用**,而且不是人声。
     · 0.385 那个是 P1-A5 的实时率,量的是稳态。

   ⇒ 本文件量的是**第三件事,也是用户真正会遇到的那件**:
     松开按钮之后,他要等多久。⇒ 稳态 + 人声形状的音频 + 端到端。

════════════════════════════════════════════════════════════════════════════
 ★★ 取证边界(★ 别把没量过的读成量过了)

 1. **音频不是真人说的**,是 Piper 合成的。理由:本仓没有任何人声样本,
    而造一段 440 Hz 正弦音正是上一个数被人误读的原因 —— 正弦音**解码不出词**,
    whisper 几乎不产 token,所以它量不到解码那一半的成本。
    合成语音有真实的音素与词,**在解码成本这一维上和人声同量级**;
    但它**吐字比人清楚、没有环境噪声、没有口音与停顿** ⇒ 本文件的数是一个
    **乐观下界**,真人只会更慢,不会更快。
 2. **只有英文语音**(本机 Piper 只装了 `en_US-lessac-medium`,全盘唯一)。
    而这套装置是中文界面、中文用户。whisper 的解码成本大致随**输出 token 数**走,
    中文与英文的 token 密度不同 ⇒ **中文的数本文件量不到**,如实留白。
 3. `--gpu` 那一轮**只是量**,不是建议改。改不改是裁定(见 D103 裁定⑤)。

════════════════════════════════════════════════════════════════════════════
 ★★★ 2026-08-15 第一次跑出来的结果 —— 它把上面那两个数**都推翻了**

   | 音频     | 时长  | 冷启   | 稳态中位 | 表观 RTF |
   |----------|-------|--------|----------|----------|
   | 短       | 2.0s  | 7.52s  | 7.50s    | 3.73     |
   | 中       | 4.4s  | 7.53s  | 7.54s    | 1.72     |
   | 长       | 8.4s  | 7.69s  | 7.64s    | 0.91     |
   (另一轮,`cpu_threads=4`:0.8s -> 5.66s · 12.1s -> 5.89s · 24.1s -> 5.75s;
    三档线程全体落在 5.36–7.05s ⇒ 没有可调空间)
   ★ **区间宽度来自线程档与跑次,不来自音频长短**:同一段 0.8s 的话,默认线程 7.05s、
     4 线程 5.66s、16 线程 5.43s。⇒ 最大值 7.75s 出自**最长**那段(8.3s),不是最短那段。

   **① 耗时与音频长短几乎无关**(0.8s 的话和 24s 的话一样慢)。
      原因是 whisper 一律把输入**补齐到 30 秒窗**再跑编码器,而在 CPU 上编码就是全部成本。
      ⇒ **「实时率(RTF)」对这条路结构上不适用** —— 成本不随时长走,
        所以 `PROJECT_PLAN:1242` / `STATE:996` 那个 **0.385** 不能用来预测任何事。
   **② `launch.toml` 那个 5.6s 不是「冷启第一次」的代价,是每一次调用的地板。**
      实测冷 7.52s / 稳态 7.50s,几乎不差 ⇒ 之前把它读成冷启,是读错了。
   **③ 于是「按住说话好不好用」这个问题今天的答案是:松手之后要等 5.4–7.8 秒,
      而且说长说短都差不多是这个数。** 方案书 :472 那个招牌例子
      (随口一句「记一下,下周要交房租」,约 2 秒)实测**说 2 秒、等 7.5 秒**。
      ★ 「越短越亏」亏在**比值**、不在绝对秒数:绝对成本恒定 ⇒ 越短的话摊得越不划算
        (2.0s RTF 3.73 / 24.1s RTF 0.23),**不是**短音频本身跑得更久。

   **④ ★★ 而 GPU 那条路实测 0.18s(快 30–40 倍),显存 2.226 GiB。**
      它今天**默认是坏的**,坏法很难查:模型加载成功,第一次转写才抛
      `cublas64_12.dll 找不到`。根因是 CUDA 运行时来自 pip wheel,
      DLL 在 `site-packages/nvidia/*/bin` 而那不在 DLL 搜索路径上。
      已在 `server.py` 的 `add_cuda_dll_dirs()` 修掉 ⇒ 本文件 `--gpu` 现在跑得通。
      ★ **修的是那颗雷,不是改档** —— 改不改仍然是裁定。
"""
from __future__ import annotations

import io
import math
import statistics
import sys
import time
import wave
from pathlib import Path
from typing import List, Tuple

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import server as S  # noqa: E402

#: 客户端 `AudioCapture` 录出来的形状(WinMM waveIn,D104)—— 本文件照着喂,
#: 免得量的是一条真实路径上不存在的音频格式。
CLIENT_RATE = 16000
CLIENT_WIDTH = 2
CLIENT_CHANNELS = 1

#: 按住说话的典型长度。★ 不量 1 秒:1 秒说不完一句话,而 [verified] 那个数
#: 恰恰是 1 秒的 —— 拿它推「一句话要等多久」正是那个数被误用的方式。
UTTERANCES = [
    ("短(约 2s)", "Remind me to pay the rent next week."),
    ("中(约 5s)", "Remind me to pay the rent next week, and add milk and eggs to the shopping list."),
    ("长(约 10s)", "Remind me to pay the rent next week, and add milk and eggs to the shopping list. "
                   "Also, tell me what the weather will be like tomorrow morning before I leave."),
]

WARM_REPEATS = 5


def _resample_to_16k(pcm: bytes, src_rate: int) -> bytes:
    """线性重采样到 16 kHz 单声道 16-bit —— 与 `AudioCapture` 产出的形状一致。

    ★ 用线性插值而不是拉一个新依赖:本服务的底线之一就是「新依赖为 0」
      (`server.py` 文件头)。重采样质量对**延迟**这个被测量没有影响。
    """
    import array

    if src_rate == CLIENT_RATE:
        return pcm
    src = array.array("h")
    src.frombytes(pcm)
    n_out = int(len(src) * CLIENT_RATE / src_rate)
    out = array.array("h", [0] * n_out)
    step = src_rate / CLIENT_RATE
    for i in range(n_out):
        pos = i * step
        j = int(pos)
        frac = pos - j
        a = src[j]
        b = src[j + 1] if j + 1 < len(src) else a
        out[i] = int(a + (b - a) * frac)
    return out.tobytes()


def _wrap_wav(pcm: bytes) -> bytes:
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(CLIENT_CHANNELS)
        w.setsampwidth(CLIENT_WIDTH)
        w.setframerate(CLIENT_RATE)
        w.writeframes(pcm)
    return buf.getvalue()


def _sine_wav(seconds: float) -> bytes:
    """440 Hz 正弦音 —— **对照组**,复现 [verified] 那个数量到的东西。"""
    import array

    n = int(CLIENT_RATE * seconds)
    a = array.array("h", [int(12000 * math.sin(2 * math.pi * 440 * i / CLIENT_RATE))
                          for i in range(n)])
    return _wrap_wav(a.tobytes())


def _synth(engines: "S.SpeechEngines", text: str) -> Tuple[bytes, float]:
    """Piper 合成 → 重采样到 16k → 封成 WAV。返回 (wav, 秒数)。"""
    raw, rate, frames = engines.synthesize(text)
    with wave.open(io.BytesIO(raw), "rb") as r:
        pcm = r.readframes(r.getnframes())
    pcm16k = _resample_to_16k(pcm, rate)
    seconds = len(pcm16k) / (CLIENT_RATE * CLIENT_WIDTH)
    return _wrap_wav(pcm16k), seconds


def _time_transcribe(engines: "S.SpeechEngines", wav: bytes) -> Tuple[float, str]:
    t0 = time.perf_counter()
    text, _lang, _dur = engines.transcribe(wav)
    return time.perf_counter() - t0, text


def _run_tier(device: str, compute_type: str, samples: List[Tuple[str, bytes, float]],
              sine: Tuple[bytes, float]) -> int:
    spec = S.load_launch_spec()
    # ★ 只在**本进程内存里**改 device —— 不写回 launch.toml。
    #   改那个文件要重跑 verify_launch.py 并回填 [verified](D103 裁定②),
    #   而本文件是**量**,不是改配置。
    spec = {**spec, "asr": {**spec["asr"], "device": device, "compute_type": compute_type}}
    eng = S.SpeechEngines(spec, tier="lite")

    print(f"\n{'=' * 74}\n  device={device} compute_type={compute_type}  (tier=lite / turbo)\n{'=' * 74}")
    t0 = time.perf_counter()
    try:
        eng.load()
    except Exception as ex:  # noqa: BLE001
        print(f"  X 加载失败:{type(ex).__name__}: {ex}")
        print(f"    detail: {eng.detail}")
        return 1
    load_s = time.perf_counter() - t0
    print(f"  加载耗时 {load_s:.2f}s")

    # ── 冷启第一次调用(这正是 [verified] 那个 5.6s 量到的东西)──────────
    sine_wav, sine_s = sine
    cold_s, _ = _time_transcribe(eng, sine_wav)
    print(f"\n  [对照] 冷启第一次 · {sine_s:.1f}s 440Hz 正弦音"
          f" -> {cold_s:.2f}s   ← [verified] 那个数是这一格")

    # ★ 这一列叫「首次」不叫「冷启」:上面那次正弦音已经把模型跑热了,
    #   ⇒ 本表**没有一格是真冷的**,唯一真冷的那次是上面 [对照] 那一行。
    #   (第一版把它印成「冷启」,而它不是 —— 对抗式复核抓出来的。)
    print(f"\n  {'音频':<14}{'时长':>7}{'首次':>9}{'稳态中位':>10}{'稳态RTF':>10}   识别出的字")
    print(f"  {'-' * 72}")
    rows = []
    for label, wav, seconds in samples:
        first_s, text = _time_transcribe(eng, wav)
        warm = [_time_transcribe(eng, wav)[0] for _ in range(WARM_REPEATS)]
        med = statistics.median(warm)
        rtf = med / seconds if seconds else 0.0
        rows.append((label, seconds, first_s, med, rtf))
        shown = (text[:34] + "…") if len(text) > 35 else text
        print(f"  {label:<14}{seconds:>6.1f}s{first_s:>8.2f}s{med:>9.2f}s{rtf:>10.3f}   {shown}")

    print(f"\n  ★ 稳态 RTF 中位数 = {statistics.median(r[4] for r in rows):.3f}"
          f"  (RTF = 转写耗时 / 音频时长;< 1 表示比说话还快)")
    longest = max(rows, key=lambda r: r[1])
    print(f"  ★ 用户实际要等的时间:说 {longest[1]:.0f} 秒 -> 松手后等 {longest[3]:.2f} 秒")
    return 0


def main() -> int:
    print("=== ASR 稳态延迟实测(V38)===")
    print("★ 音频是 Piper 合成的英文,不是真人、不是中文 —— 见文件头取证边界。")

    # 合成音频只做一次,两轮共用同一批 WAV(否则量的是两批不同的音频)。
    spec = S.load_launch_spec()
    tts_only = S.SpeechEngines(spec, tier="lite")
    from piper import PiperVoice  # noqa: PLC0415

    tts_only.tts = PiperVoice.load(str(S._voice_path(spec)))

    samples: List[Tuple[str, bytes, float]] = []
    for label, text in UTTERANCES:
        wav, seconds = _synth(tts_only, text)
        samples.append((label, wav, seconds))
    sine_s = 1.0
    sine = (_sine_wav(sine_s), sine_s)
    print(f"  合成了 {len(samples)} 段:" + " · ".join(f"{l} {s:.1f}s" for l, _w, s in samples))

    rc = _run_tier("cpu", spec["asr"].get("compute_type", "int8"), samples, sine)

    if "--gpu" in sys.argv:
        # ★ 只是量。改不改是裁定(D103 裁定⑤)。
        rc |= _run_tier("cuda", "float16", samples, sine)

    print("\n" + "-" * 74)
    return rc


if __name__ == "__main__":
    sys.exit(main())
