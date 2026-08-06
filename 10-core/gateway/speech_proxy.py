r"""网关侧的 **speech 后端消费者** —— D? · P5 语音 v1。

════════════════════════════════════════════════════════════════════════════
 ★★ 这个文件是那三条契约的**客户端半边**(D92/D95 的成对断言)

 speech 后端是一个**独立进程**(10-core/speech/server.py,自己的 venv)。
 网关要把语音能力接进别名体系,就得跨进程去读它的应答 ——
 而"跨进程读应答"正是审计 A1 死掉的那一步:
   服务端测「顶层有哪些键」,消费者测「这个形状能不能解析」,**各测各的**,
   中间那条缝谁也没看。

 ⇒ 所以本文件的每个 `parse_*` 都拿 `10-core/speech/contracts.json` 核对键集合,
   而**服务端读的是同一个文件**。期望值只有一份,它没法跟自己分家。

 ★ 认不出的形状一律返回 (None, why),**不挑着能读的字段拼一个出来** ——
   拼出来的那一份会以一个可信的样子进到上层,而上层没有任何办法发现它是残的。
"""
from __future__ import annotations

import json
from pathlib import Path
from typing import Any, Dict, Optional, Tuple

HERE = Path(__file__).resolve().parent
_SPEECH_DIR = HERE.parent / "speech"

CONTRACT_HEALTH = "CONTRACT:speech.health"
CONTRACT_ASR = "CONTRACT:speech.asr"
CONTRACT_TTS = "CONTRACT:speech.tts"

#: ★ 只有**本机麦克风 / 已认证 LAN 设备**这两种通道才配得上这个来源档位。
#  它由 speech 服务端按连接判定并写进应答;网关这边**只读不改**,更不许自己填一个。
PROV_TRUSTED = "user_voice_asr"


def _contracts() -> Dict[str, Any]:
    with open(_SPEECH_DIR / "contracts.json", "rb") as f:
        return json.load(f)["contracts"]


def expected_keys(cid: str) -> frozenset:
    """某条契约登记的顶层键集合。★ 与服务端读同一份文件。"""
    return frozenset(_contracts()[cid]["keys"])


def _match(obj: Any, cid: str) -> Optional[str]:
    """形状核对。返回 None = 对得上;否则返回一句**说得出实际是什么**的话。"""
    if not isinstance(obj, dict):
        return f"{cid}:应答不是一个对象(实得 {type(obj).__name__})"
    want = expected_keys(cid)
    got = frozenset(obj)
    if got != want:
        # ★ 集合相等,不是"包含" —— 「包含」放过"多发一个键"和"改了名还留着旧的",
        #   而那两种正是字段搬家的实际形状。
        return (f"{cid}:顶层键与登记的契约对不上("
                f"多 {sorted(got - want)} 少 {sorted(want - got)})")
    return None


def parse_health(obj: Any) -> Tuple[Optional[Dict[str, Any]], Optional[str]]:
    """解析 /health。★ 目标字段:ready(装载器的就绪判据靠它)。"""
    why = _match(obj, CONTRACT_HEALTH)
    if why:
        return None, why
    return {"ready": bool(obj["ready"]), "tier": obj["tier"],
            "asr_loaded": bool(obj["asr_loaded"]), "tts_loaded": bool(obj["tts_loaded"]),
            "detail": obj["detail"]}, None


def parse_asr(obj: Any) -> Tuple[Optional[Dict[str, Any]], Optional[str]]:
    """
    解析 ASR 应答。★★ 目标字段里最要紧的是 `provenance` ——
    它决定这段文字能不能**直通记忆写入**,而它必须是**服务端算出来的**那一个。
    """
    why = _match(obj, CONTRACT_ASR)
    if why:
        return None, why
    return {"text": obj["text"], "language": obj["language"],
            "duration_s": float(obj["duration_s"]), "tier": obj["tier"],
            "provenance": obj["provenance"]}, None


def parse_tts(obj: Any) -> Tuple[Optional[Dict[str, Any]], Optional[str]]:
    """解析 TTS 应答。★ 目标字段:能不能真的拿到一段可播的音频。"""
    why = _match(obj, CONTRACT_TTS)
    if why:
        return None, why
    return {"audio_b64": obj["audio_b64"], "sample_rate": int(obj["sample_rate"]),
            "format": obj["format"], "voice": obj["voice"], "frames": int(obj["frames"])}, None


def may_write_memory(asr_parsed: Dict[str, Any]) -> bool:
    """
    这段转写**能不能**直通记忆写入。

    ★★★ 判据只有一条:`provenance` 是不是服务端给出的可信档位。
      任务原话:「只有本机 / 已认证 LAN 设备的麦克风才可用 provenance user_voice_asr。
                这条是安全判据不是配置:来源档位由**通道**决定,不由调用方自报。」
    ★ 网关这边**不补救**、不放宽:拿不到可信档位就是不能写,
      而不是"退一步记成低可信度" —— 记忆库里一条来源可疑的记录,
      与一条没有的记录相比,坏处是它会被当成事实用下去。
    """
    return asr_parsed.get("provenance") == PROV_TRUSTED
