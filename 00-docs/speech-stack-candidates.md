# 语音栈 / VLM 选型 · 决策备研

> 建立于 2026-07-27(用户买菜期间)。**这是选项清单,不是决定** —— 供你回来快速拍板。
> 解锁 P1 的最后 3 项:A5(ASR+TTS 能否合并)· A8(CPU ASR 实时率)· C4(TTS 延迟 + WER)。
> 所有候选已在 HuggingFace 核实**真实存在**(2026-07-27),不是凭空推荐。

---

## 0. 先厘清一个被忽略的分工(它改变权重)

```
ASR(语音→文字) = 输入路径:你的话进记忆  →  必须多语言(中/英/德混说)
TTS(文字→语音) = 输出路径:助手念给你听  →  语言 = 助手回复的语言
```

**ASR 的多语言是硬约束**(你混说,它得听懂)。
**TTS 的多语言是「看助手用什么语言回你」** —— 若助手主要用中文/英文回,TTS 的多语言没那么关键。
这条决定了下面 TTS 那栏可以放宽。

---

## 1. ASR(4 个候选,推荐方向明确)

| 候选 | 框架 | 多语言 | 大小 | 取舍 |
|---|---|---|---|---|
| **faster-whisper large-v3** | CTranslate2 | ✅ 中/英/德全强 | int8 ~1.5 GB | **标准选择**,速度快,GPU/CPU 都行 |
| **faster-whisper large-v3-turbo** | CTranslate2 | ✅ 同上,略逊 | ~0.8 GB | 更快更小,精度略降。适合 `speech.lite` 的 GPU ASR |
| whisper large-v3-turbo(官方) | PyTorch/transformers | ✅ | ~1.6 GB | 同模型但 PyTorch 后端,慢于 CTranslate2 |
| NVIDIA Parakeet(NeMo) | NeMo | ❌ **英文为主** | ~0.6 GB | **中/德混说会翻车,排除** |

> **建议**:`speech.full` 的 ASR 用 **large-v3**(精度),`speech.lite` 的 GPU ASR 用 **large-v3-turbo**(小快)。
> 都是 CTranslate2 → A5 的合并问题只需考虑「CTranslate2 + TTS 框架」这一种组合。
> ⚠ **A8/C4 要测的语料是你本人的中/英/德混说** —— 这就是为什么 C4 的语料只有你能录。

---

## 2. TTS(这是全库空白的那项,最需要你定)

| 候选 | 框架 | 多语言 | 大小 | CPU 实时? | 取舍 |
|---|---|---|---|---|---|
| **Piper** | ONNX | 分语言模型(中/英/德各一) | ~60 MB/语言 | ✅ 快 | **speech.cpu/lite 的 CPU TTS 首选**:小、快、离线。音质中规中矩(老 VITS) |
| **MeloTTS** | PyTorch | ✅ 中/英/日等,可 CPU 实时 | ~200 MB | ✅ | MyShell 出品,CPU 实时,中英不错。**德语支持弱** |
| **Kokoro-82M** | PyTorch/ONNX | 英强,中(v1.0 加)· 德弱 | ~350 MB | △ | 音质好、活跃维护,但**德语基本没有** |
| **XTTS-v2** | PyTorch | ✅ 中/英/德 + 克隆音色 | ~1.8 GB | ❌ 需 GPU | 音质最好、可克隆你的声音,但 Coqui 公司已关(社区维护)、重、要 GPU。**适合 speech.full** |
| **F5-TTS** | PyTorch | ✅ 多语言变体 | ~1.3 GB | ❌ 需 GPU | 较新、音质高、可克隆。生态没 XTTS 成熟 |

> **两种路线,取决于你多在乎助手的音质与德语:**
>
> **路线 A(轻·实用)**:CPU TTS 用 **Piper**(分语言),speech.full 也用 Piper。
> 优点:全 CPU、极小、A5 直接「不合并」(反正 TTS 在 CPU、ASR 在 GPU)。缺点:音质一般。
>
> **路线 B(重·好听)**:speech.full 用 **XTTS-v2**(GPU,能克隆你的声音、中/英/德全支持),
> speech.lite/cpu 的 CPU TTS 退回 Piper。优点:助手声音好、支持德语、可个性化。缺点:speech.full 更重(~1.8GB+运行时)。
>
> ⚠ **§8.1.2 估的 `speech.full` = 4.0 GiB 权重** —— XTTS-v2(1.8)+ large-v3(1.5)≈ 3.3,对得上;
> 若两者对不上估算,按 A2 规则**先改 §8.1.2 再测**。

---

## 3. VLM(`vlm.small` 行 · 已实测一个候选)

| 候选 | 已测? | 大小(含 mmproj) | 取舍 |
|---|---|---|---|
| **Qwen2.5-VL-3B**(已测) | ✅ 4.35 GiB | model 1.80 + mmproj 1.25 | 已在 llama.cpp 跑通,与主力 Qwen3 同家族。q8_0 mmproj 可压到 ~3.9 |
| MiniCPM-V-2.6 | ✗ | ~5-6 GB(8B 级) | 更大更强,但超 vlm.small 定位 |
| InternVL2-2B | ✗ | ~2.5 GB | 更小,中文 OCR 强 |

> **建议**:就用 **Qwen2.5-VL-3B**(已测、在工具链内、同家族)。若嫌 4.35 重,换 q8_0 mmproj 省 0.46。
> VLM 定位是「截图理解 / 未知状态诊断」(§6.6 游戏诊断),3B 够用。

---

## 4. A5 的合并问题,答案已经浮现

A5 问「ASR + TTS 能否合进一个进程省一个 CUDA context(~0.4 GiB)」。

| ASR | TTS | 能合并? |
|---|---|---|
| faster-whisper(CTranslate2)| Piper(ONNX,**跑 CPU**)| **无需合并** —— TTS 在 CPU 不占 CUDA context |
| faster-whisper(CTranslate2)| XTTS(PyTorch,GPU)| 可塞进一个 Python 进程共享 CUDA context,省 ~0.4 GiB,但两个运行时(CT2+PyTorch)增加复杂度 |

> **所以 A5 的结论多半是**:走路线 A(TTS 在 CPU)→ 天然不用合并;走路线 B(XTTS GPU)→ 可合并省 0.4 但要权衡复杂度。
> **等你定了 TTS 路线,A5 基本就有答案了。**

---

## 5. 你回来只需回答 4 个问题

1. **ASR**:large-v3(精度) + large-v3-turbo(lite 档)?——我建议是,除非你有别的偏好
2. **TTS 路线**:A(全 Piper,轻实用)还是 B(speech.full 用 XTTS,重好听可克隆声音)?**← 这是主要决策**
3. **VLM**:Qwen2.5-VL-3B(已测)?
4. **C4 语料**:你得录 30–50 句中/英/德混说 + 定 WER 口径(字错率/词错率、中文按字还是分词)——**只有你能做**

> 定完 1-3,我下载候选 + 测 A5/A8/C4(A8/C4 需要你的语料就绪)。
> **若你懒得逐个想,我的默认推荐**:ASR large-v3(+turbo)· TTS **路线 B**(XTTS 值得,你会天天听助手说话,而且能克隆你自己的声音)· VLM Qwen2.5-VL-3B。你只需说「按你推荐的」或改哪条。
