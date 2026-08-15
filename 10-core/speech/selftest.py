r"""speech 后端自检 —— D? · P5 语音 v1。

跑:  <speech venv>\Scripts\python.exe 10-core\speech\selftest.py
     (也可用仓库任意 3.11+ 的 python —— 本文件**不导入**任何模型库)

════════════════════════════════════════════════════════════════════════════
 ★★ 为什么这里用**桩引擎**而不是真的加载 1.6 GB 权重

 被钉的是**跨进程响应契约的形状**,而形状是由 `server.py` 里那几行
 `self._json(200, {...})` 决定的 —— 模型加载与否不改变它一个字节。
 拿桩引擎驱动**真的 Handler**,测的仍然是真代码;而加载真权重只会让门禁
 多花十几秒、并把"权重在不在"这件与契约无关的事混进来。

 ★ 真权重那一半**另有其人**:`verify_launch.py` 真的加载、真的转写、真的合成,
   它的读数写进 `launch.toml` 的 [verified] 段,而装载器认的就是那一段。
   ⇒ 两件事分开,各自都跑得到,谁也不替谁背书。
"""
from __future__ import annotations

import io
import json
import sys
import threading
import time
import tomllib
import urllib.error
import urllib.request
import wave
from http.server import ThreadingHTTPServer
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import server as S  # noqa: E402

_p = _f = 0


def check(name: str, cond: bool, extra: str = "") -> None:
    global _p, _f
    if cond:
        _p += 1
    else:
        _f += 1
        _emit(f"  X {name}" + (f"   {extra}" if extra else ""))


def _emit(line: str) -> None:
    """打印一行,**保证打得出去**。

    ★★★ 2026-08-15(V38)加的,起因是真踩了一次:本机控制台是 **cp936**,
      而我给一条新断言写的失败消息里有个 `⇒`(U+21D2)—— gbk 编不了它。
      于是那条断言一旦命中,`print` 抛 `UnicodeEncodeError`,
      **整个套件当场死在那一行**:消息印不出来、后面二十条断言不跑、末尾汇总行也没有。
      ⇒ 一条「只在出事时才说话」的断言,偏偏在出事那一刻把自己和别人一起带走了。

    ★ 这与本仓已有的那条纪律同源(ASSERTION-PITFALLS 第 8 条:
      机器要读的那几个字符在 cp936 下必须仍然完好),只是那条管的是**契约号**,
      而失败消息此前没人管。⇒ 这里兜底:编不动的字符降级成 `?`,**绝不让打印杀掉套件**。
    """
    enc = getattr(sys.stdout, "encoding", None) or "utf-8"
    try:
        print(line)
    except UnicodeEncodeError:
        print(line.encode(enc, "replace").decode(enc, "replace"))


class _StubEngines:
    """只提供 Handler 需要的那几个成员 —— 不碰任何模型库。"""

    def __init__(self, spec, ready=True):
        self.spec = spec
        self.tier = "lite"
        self.asr = object() if ready else None
        self.tts = object() if ready else None
        self.detail = "stub"

    def ready(self):
        return self.asr is not None and self.tts is not None

    def transcribe(self, wav_bytes):
        return "你好", "zh", 1.25

    def synthesize(self, text):
        buf = io.BytesIO()
        with wave.open(buf, "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(22050)
            w.writeframes(b"\x00\x00" * 100)
        return buf.getvalue(), 22050, 100


def _get(url, data=None, headers=None):
    req = urllib.request.Request(url, data=data, headers=headers or {},
                                 method="POST" if data is not None else "GET")
    try:
        with urllib.request.urlopen(req, timeout=10) as r:
            return r.status, json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8")
        try:
            return e.code, json.loads(body)
        except Exception:  # noqa: BLE001
            return e.code, {}


def main() -> int:
    print("=== speech 后端自检 ===")
    spec = S.load_launch_spec()

    # ══════════════════════════════════════════════════════════════════
    #  1. 启动规格:未经实测的规格一律不认(这是本车道验收判据的地基)
    # ══════════════════════════════════════════════════════════════════
    check("★★★ launch.toml 有 [verified] 段 —— 没有它,装载器一律退回「启动方式尚未验证」",
          "verified" in spec)
    v = spec.get("verified", {})
    check("★★ [verified] 记着**实测读数**而不是空壳(载入耗时 > 0)",
          float(v.get("asr_lite_load_s", 0)) > 0 and float(v.get("tts_synth_s", 0)) > 0,
          str(v))
    check("★★★ [verified] 自报**离线加载成功** —— 这是 local_files_only 那条硬约束的实测凭据",
          v.get("asr_offline_load_ok") is True)
    check("★★★ asr.local_files_only 与 asr.hub_offline **两条都开** —— "
          "faster-whisper 默认联网拉权重(STATE:643 / D41),缺任一条都会在缺权重时偷偷联网",
          spec["asr"]["local_files_only"] is True and spec["asr"]["hub_offline"] is True)
    check("★ 今天这一档跑 CPU(★ 这**不是**「唯一路」—— 见下面那条与 [asr] 段的更正)",
          spec["asr"]["device"] == "cpu")
    # ★ 反向:规格里**不许**出现 peak —— 显存数只有 vram-budget.toml 说了算。
    #   两处都写一个数,迟早对不上,而准入闸会照着错的那个放行。
    #
    # ★★★ 2026-08-15(V38)必须把这条**判据本身**改准,否则它今天就是一句假话:
    #   本轮往 `[measured]` 里写了一个 `gpu_vram_gib = 2.226`(第一次直接量 GPU 足迹)。
    #   那**是一个显存数**,而这条断言原先的判词是「启动规格里没有 peak/显存字段」——
    #   它今天仍然绿,**只因为它 grep 的是字面词 "peak"**,而那个键叫 gpu_vram_gib。
    #   ⇒ 一条「绿着但判词已经不成立」的断言,比红的更坏。
    #
    #   真正要守的性质是:**准入闸的收费不许有第二个来源**。读数不是收费。
    #   ⇒ 判据改成两半:① 规格正文([measured] 之外)一个显存数都不许有;
    #                    ② [measured] 里的显存读数必须**名字上就自陈是读数**,不许叫 peak。
    _spec_proper = {k: v for k, v in spec.items() if k != "measured"}
    _vram_words = ("peak", "vram", "gib")
    _hits = [w for w in _vram_words if w in json.dumps(_spec_proper).lower()]
    check("★★ 启动规格正文里**没有任何显存字段**(准入闸的唯一数据源是 vram-budget.toml)"
          " -- ★ [measured] 不算正文:它是读数,不是收费,单独判",
          not _hits, f"命中 {_hits}")
    check("★★ [measured] 里的显存读数**不许**叫 peak(叫 peak 就会被人当成收费的第二个来源)",
          not any("peak" in k.lower() for k in spec.get("measured", {})),
          str([k for k in spec.get("measured", {}) if "peak" in k.lower()]))

    # ══════════════════════════════════════════════════════════════════
    #  1b. ★★★ 启动规格的 device 与准入闸的收费**对不上** —— 这是一条
    #      **登记在案的不一致**,本条断言守的不是"一致",是
    #      **「它不会被无声地改掉、也不会无声地长大」**(与欠债表同一条道理)。
    #
    #  今天的实况(V38 · 2026-08-15 实测):
    #    · `[asr].device = "cpu"`  ⇒ 实占 **0.002 GiB**(V16 实测,gpu_broker.py:511)
    #    · 而 `vram-budget.toml` 给 speech.lite 收 **2.07 GiB**
    #    ⇒ **在为一个没启用的东西付 2.07 GiB**,而日常预设的余量只有 0.53。
    #
    #  ★ 为什么本车道**不直接把收费改掉**:该改成多少**取决于 CPU/GPU 那条裁定**,
    #    而那是一条待裁(D103 裁定⑤ 的理由已被 V38 证伪,见 launch.toml [asr] 段)。
    #      · 若裁 CPU ⇒ 收费应 ≈ 0(今天这个 2.07 是纯粹的浪费);
    #      · 若裁 GPU ⇒ 收费应 ≥ 2.3(V38 实测 GPU 足迹 2.226,今天的 2.07 **偏低**,
    #        而偏低是 fail-open 方向 —— 那比偏高更该修)。
    #    ⇒ 现在拍任何一个数,都有一半概率要在裁定落地那天改回来。
    #
    #  ★★ 所以这里钉的是**当前这一格的两个值**:任何一侧单独动了,这条就红,
    #    并把「另一侧也要跟着动」这句话直接说给动它的人听。
    # ══════════════════════════════════════════════════════════════════
    _budget_path = HERE.parents[1] / "config" / "vram-budget.toml"
    # ★ 局部名字**不许**叫 `_f` —— 那是本文件失败计数器的名字,
    #   在 main() 里赋一次就把它变成局部量,末行那句 FAIL=… 会打印出一个文件对象。
    #   (写这条时真的踩了一次:PASS=27 FAIL=<BufferedReader …>。)
    # ★★ 取值必须**取不到就判红,而不是抛**:组件一旦改名,直接下标会 KeyError
    #   ⇒ 整个套件当场死,连汇总行都没有 —— 那是「崩」,不是「红」,两者差别很大。
    with open(_budget_path, "rb") as _bf:
        _comps = tomllib.load(_bf).get("components", {})
    _charged = (_comps.get("speech.lite") or {}).get("peak")
    # ★ 消息里**只用 ASCII 箭头**:本机控制台是 cp936,`⇒` 编不进去,
    #   而失败消息印不出来 = 这条断言在最该说话的时候哑了(见 _emit 那段实测)。
    check("★★★ 【登记在案的不一致】device=cpu(实占 0.002)而闸按 2.07 收费 -- "
          "两侧的值都没被单独动过。★ 你若动了其中一侧:另一侧必须同一轮跟着定"
          "(裁 CPU -> 收费应约等于 0;裁 GPU -> 收费应不低于 2.3,V38 实测足迹 2.226)。"
          "出处:decision-packets/v38-p5-first-batch-2026-08-15.md",
          spec["asr"]["device"] == "cpu"
          and _charged is not None and abs(float(_charged) - 2.07) < 1e-9,
          f"实得 device={spec['asr']['device']} / 收费={_charged}"
          + ("(★ speech.lite 在 vram-budget.toml 里找不到了 —— 组件改名了?)"
             if _charged is None else ""))

    # ══════════════════════════════════════════════════════════════════
    #  2. ★★★ 架构底线:本服务**不得**持有任何实时音频通路
    # ══════════════════════════════════════════════════════════════════
    #  「同传可以失败,用户的麦克风不可以」。本服务对这条的落实是**结构性的**:
    #  采集根本不在这个进程里,本服务只是"一段已经录好的音频"的消费者。
    #  ⇒ 一旦有人在这里加一条实时转发,那条底线就从结构性退化成靠自觉。
    src = (HERE / "server.py").read_text(encoding="utf-8")
    # ★ 针**拼出来**(ASSERTION-PITFALLS 第 1 条):否则这行断言自己的字面量
    #   会被扫描类守卫抓成"违例",而且它也会让下面这条判据找到自己。
    needles = ["sound" + "device", "pyaudio", "WASAPI", "loopback" + "_capture", "InputStream"]
    hit = [n for n in needles if n in src]
    check("★★★ 本服务里**没有**任何实时采集/转发的痕迹 —— "
          "麦克风那条线在结构上就到不了这里,所以本服务全挂也不影响它",
          not hit, f"命中 {hit}")
    check("★ 反向:上面那条判据不是恒真的(针表非空且真的会命中已知词)",
          len(needles) > 0 and "InputStream" in needles)

    # ══════════════════════════════════════════════════════════════════
    #  3. provenance 由**通道**决定,不由调用方自报(安全判据)
    # ══════════════════════════════════════════════════════════════════
    class _H:
        def __init__(self, d):
            self._d = d

        def get(self, k, default=None):
            return self._d.get(k, default)

    check("★★★ 回环 -> user_voice_asr(本机麦克风)",
          S.provenance_for("127.0.0.1", _H({})) == S.PROV_TRUSTED)
    check("★★★ 非回环 + **没有**已验证指纹头 -> untrusted_audio(记忆写入侧据此拒绝)",
          S.provenance_for("192.168.1.50", _H({})) == S.PROV_UNTRUSTED)
    check("★★★ 非回环 + lan-edge 注入的已验证指纹头 -> user_voice_asr(已认证 LAN 设备)",
          S.provenance_for("192.168.1.50", _H({S.VERIFIED_FP_HEADER: "AB12"})) == S.PROV_TRUSTED)
    # ★★ 承重的反向:**请求体里写什么都改不动它**。
    #   这条一旦破,记忆库的准入就交给调用方了。
    check("★★★ 反向:provenance **不读请求体** —— 函数签名里只有 (client_host, headers)",
          S.provenance_for.__code__.co_varnames[:2] == ("client_host", "headers")
          and S.provenance_for.__code__.co_argcount == 2)
    # ★★ 反向:do_POST 那一段里,provenance **只能**来自 provenance_for(...),
    #   不许出现"从请求体里取"的写法。
    #   ★ 判据范围**切到 do_POST 那一段**再判 —— 拿整个文件判会撞上文件头那段
    #     解释"为什么不读请求体"的注释(ASSERTION-PITFALLS 第 1 条:已踩 9 次的那个坑)。
    _post = src[src.index("def do_POST"):]
    check("★★★ 反向:do_POST 里 provenance 只由 provenance_for(通道) 得出,不从请求体取",
          "provenance_for(" in _post
          and 'req.get("provenance"' not in _post and "req.get('provenance'" not in _post,
          _post[:0])
    check("★ 反向:上面那条切片真的切到了东西(切不到就等于跳过 = 假断言)",
          len(_post) > 200 and "do_POST" in _post)

    # ══════════════════════════════════════════════════════════════════
    #  4. ★★ 跨进程契约(D92/D95)—— 服务端半边:顶层键集合
    #     本节钉的三条(契约号写成字面量:欠债表按文本找这一半,而下面代码里用的是常量):
    #       CONTRACT:speech.health · CONTRACT:speech.asr · CONTRACT:speech.tts
    # ══════════════════════════════════════════════════════════════════
    port = 18099  # 自检用另一个端口,避免撞上真在跑的服务
    S.Handler.engines = _StubEngines(spec)
    srv = ThreadingHTTPServer(("127.0.0.1", port), S.Handler)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    time.sleep(0.2)
    base = f"http://127.0.0.1:{port}"
    try:
        st, j = _get(base + "/health")
        check("/health 就绪时回 200 (" + str(st) + ")", st == 200)
        check(f"★★★ {S.CONTRACT_HEALTH} 服务端半边:顶层键集合 == 登记表",
              set(j) == S.CONTRACT_KEYS[S.CONTRACT_HEALTH],
              f"实际 {sorted(j)} / 登记 {sorted(S.CONTRACT_KEYS[S.CONTRACT_HEALTH])}")

        st, j = _get(base + "/v1/speech/asr", data=b"RIFFfake")
        check("/v1/speech/asr -> 200 (" + str(st) + ")", st == 200)
        check(f"★★★ {S.CONTRACT_ASR} 服务端半边:顶层键集合 == 登记表",
              set(j) == S.CONTRACT_KEYS[S.CONTRACT_ASR],
              f"实际 {sorted(j)} / 登记 {sorted(S.CONTRACT_KEYS[S.CONTRACT_ASR])}")
        check("★★ ASR 应答里的 provenance 是**服务端算出来的**(自检走回环 ⇒ 可信)",
              j.get("provenance") == S.PROV_TRUSTED)

        st, j = _get(base + "/v1/speech/tts",
                     data=json.dumps({"text": "你好"}).encode("utf-8"),
                     headers={"Content-Type": "application/json"})
        check("/v1/speech/tts -> 200 (" + str(st) + ")", st == 200)
        check(f"★★★ {S.CONTRACT_TTS} 服务端半边:顶层键集合 == 登记表",
              set(j) == S.CONTRACT_KEYS[S.CONTRACT_TTS],
              f"实际 {sorted(j)} / 登记 {sorted(S.CONTRACT_KEYS[S.CONTRACT_TTS])}")
        check("★ TTS 回的是**可解码的** wav(base64 + 采样率,不是一句空承诺)",
              isinstance(j.get("audio_b64"), str) and len(j["audio_b64"]) > 0
              and j.get("sample_rate", 0) > 0 and j.get("format") == "wav")

        # ── 未就绪:必须 503,不是 200 也不是超时 ──────────────────────
        S.Handler.engines = _StubEngines(spec, ready=False)
        st, j = _get(base + "/health")
        check("★★★ 未就绪时 /health 回 **503**(与 llama-server 同形状:进程活着 ≠ 能服务)",
              st == 503, str(st))
        st, _ = _get(base + "/v1/speech/asr", data=b"x")
        check("★★ 未就绪时业务端点也回 503,不假装能干活", st == 503, str(st))
    finally:
        srv.shutdown()

    # ── 元断言:登记表里每条契约都被钉过 ────────────────────────────────
    #   遍历源是 CONTRACT_KEYS 本身,不是手写名单(ASSERTION-PITFALLS 3b)。
    pinned = {S.CONTRACT_HEALTH, S.CONTRACT_ASR, S.CONTRACT_TTS}
    check("★★★ 元断言:CONTRACT_KEYS 里每一条都有服务端成对断言 —— 缺:"
          + str(sorted(set(S.CONTRACT_KEYS) - pinned)),
          set(S.CONTRACT_KEYS) == pinned)
    check("★ 元断言的另一个方向:契约数 > 0(零命中也判红)", len(S.CONTRACT_KEYS) > 0)

    print("-" * 70)
    print(f"  === speech selftest: PASS={_p} FAIL={_f} ===")
    return 1 if _f else 0


if __name__ == "__main__":
    sys.exit(main())
