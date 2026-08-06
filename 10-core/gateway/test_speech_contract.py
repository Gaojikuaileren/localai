r"""speech 三条契约的**客户端(消费者)半边** —— D? · P5 语音 v1。

跑:  py -3 10-core\gateway\test_speech_contract.py

本文件是这三条契约的**消费者半边**(契约号写成字面量 —— 欠债表按文本找这一半,
而下面代码里用的是常量):
  CONTRACT:speech.health · CONTRACT:speech.asr · CONTRACT:speech.tts

════════════════════════════════════════════════════════════════════════════
 ★★ 服务端半边在 `10-core/speech/selftest.py`(桩引擎驱动**真 Handler**,钉顶层键集合)。
 这一半钉的是另一件事:**拿那个形状能不能解析出目标字段**。
 A1 就死在这两件事之间 —— 两边各测各的,而客户端喂给自己的是**自己造的**形状,
 于是服务端把字段搬了家也照样绿。

 ⇒ 所以这里的形状**由 contracts.json 生成**,而服务端读的是同一个文件。
   期望值只有一份,它没法跟自己分家。
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.stdout.reconfigure(encoding="utf-8", errors="replace")

import speech_proxy as SP  # noqa: E402

_p = _f = 0


def check(name: str, cond: bool, extra: str = "") -> None:
    global _p, _f
    if cond:
        _p += 1
    else:
        _f += 1
        print(f"  X {name}" + (f"   {extra}" if extra else ""))


def _val(k: str):
    """按键名给一个**类型对得上**的值 —— 类型给错的话红的是类型,判据就说不清话了。"""
    return {
        "ok": True, "ready": True, "kind": "speech", "tier": "lite",
        "asr_loaded": True, "tts_loaded": True, "detail": "就绪",
        "text": "你好", "language": "zh", "duration_s": 1.25,
        "provenance": SP.PROV_TRUSTED,
        "audio_b64": "UklGRg==", "sample_rate": 22050, "format": "wav",
        "voice": "en_US-lessac-medium", "frames": 100,
    }[k]


def shape(cid: str, **over):
    """★ 形状**由登记表生成**,不手抄 —— 手抄的话服务端搬了家这一半照样绿。"""
    o = {k: _val(k) for k in SP.expected_keys(cid)}
    o.update(over)
    return o


def main() -> int:
    print("=== speech 契约 · 客户端(消费者)半边 ===")
    parsers = {
        SP.CONTRACT_HEALTH: SP.parse_health,
        SP.CONTRACT_ASR: SP.parse_asr,
        SP.CONTRACT_TTS: SP.parse_tts,
    }

    # ── 正向:登记表生成的形状,真解析器解得出目标字段 ─────────────────
    got, why = SP.parse_health(shape(SP.CONTRACT_HEALTH))
    check(f"★★★ {SP.CONTRACT_HEALTH} 客户端半边:解得出 ready(装载器的就绪判据靠它)",
          got is not None and got["ready"] is True, str(why))

    got, why = SP.parse_asr(shape(SP.CONTRACT_ASR))
    check(f"★★★ {SP.CONTRACT_ASR} 客户端半边:解得出 text / language / provenance",
          got is not None and got["text"] == "你好" and got["provenance"] == SP.PROV_TRUSTED, str(why))

    got, why = SP.parse_tts(shape(SP.CONTRACT_TTS))
    check(f"★★★ {SP.CONTRACT_TTS} 客户端半边:解得出可播音频(base64 + 采样率)",
          got is not None and got["sample_rate"] == 22050 and got["format"] == "wav", str(why))

    # ── 反向:少一个键 ⇒ 整条判失败,不拼半份出来 ─────────────────────
    #   ★ 遍历源是**登记表**,不是手写名单(ASSERTION-PITFALLS 3b):
    #     表里加一个键,这里自动多测一条;而不是静默漏掉新的那个。
    for cid, fn in parsers.items():
        for drop in sorted(SP.expected_keys(cid)):
            o = shape(cid)
            o.pop(drop)
            g, w = fn(o)
            check(f"★★ 反向 {cid}:少了 `{drop}` ⇒ 判失败并说得出实际是什么",
                  g is None and w is not None and drop in w, f"{g} / {w}")
        # 多一个键同样要红 —— 「包含」放过的正是这一种
        o = shape(cid)
        o["surpriseKey"] = 1
        g, w = fn(o)
        check(f"★★ 反向 {cid}:**多**一个键也判失败(集合相等,不是包含)",
              g is None and w is not None and "surpriseKey" in w, str(w))

    # ── ★★★ 安全判据:能不能直通记忆写入,只看服务端给的 provenance ──
    ok_parsed, _ = SP.parse_asr(shape(SP.CONTRACT_ASR))
    check("★★★ 可信来源(user_voice_asr)⇒ 允许直通记忆写入", SP.may_write_memory(ok_parsed))
    bad_parsed, _ = SP.parse_asr(shape(SP.CONTRACT_ASR, provenance="untrusted_audio"))
    check("★★★ 不可信来源 ⇒ **拒绝**写入,而不是退一步记成低可信度 —— "
          "记忆库里一条来源可疑的记录会被当成事实用下去",
          not SP.may_write_memory(bad_parsed))
    check("★★ 反向:调用方自报也没用 —— 这个函数只看 provenance 一个字段,"
          "而那个字段是**服务端**按连接算出来的",
          not SP.may_write_memory({"provenance": "user_voice_asr ", "claimed_trusted": True})
          or SP.may_write_memory({"provenance": SP.PROV_TRUSTED}))

    # ── 元断言:登记表里每一条都要有消费者半边 ───────────────────────
    reg = set(json.loads((HERE.parent / "speech" / "contracts.json").read_text(encoding="utf-8"))["contracts"])
    check("★★★ 元断言:contracts.json 里每一条契约都有消费者半边 —— 缺:"
          + str(sorted(reg - set(parsers))), reg == set(parsers))
    check("★ 元断言另一个方向:契约数 > 0(零命中也判红)", len(reg) > 0)

    print("-" * 70)
    print(f"  === speech 契约 · 消费者半边:{_p} PASS · {_f} FAIL ===")
    return 1 if _f else 0


if __name__ == "__main__":
    sys.exit(main())
