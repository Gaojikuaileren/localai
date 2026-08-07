# D103 · P5 语音 v1 骨干链路:speech 后端接上装载器 · 三条契约成对 · 权重许可台账

> 车道:V7 语音(worktree `../.localAI-v7`,分支 `v7/speech-v1`)
> 日期:2026-08-06 · 状态:**已并入 main 并取号 = D103**(2026-08-07,并入那一刻回填)
> ★ **§8(客户端按住说话)另行取号 = D104** —— 它写在同一份包里,但落地是另一次提交(`c7884f4`),
> 按「代码落地先后」应当各自成条。裁定正文见 [DECISIONS.md](../DECISIONS.md) 的 D103 / D104;
> 本包保留原样作为**裁定前的材料**。
> 前置:**D27**(语音栈定稿)· **D92**(垂直切片)· **D95**(契约成对元规则)· **D41/D46**

---

## §1 任务 0 勘察:**四条里有一条,协调层说错了**

### 1.1 (a) peak 有没有实测值 —— ★ **有,而且就是 P1 那三个数**

| 组件 | peak | note |
|---|---|---|
| `speech.lite` | **2.07** | `turbo ASR 2.07 GPU + Piper CPU TTS 0(原估 1.4 偏低)` |
| `speech.full` | **4.05** | `large-v3 ASR + Piper CPU TTS` |

**协调层把 `vram-budget.toml:67` 那句话读串了一层。** 原文的上一行是:

> `★ 2026-08-05(P4-S14)新增 model_rel / ctx / ngl —— 按需装载要用的启动参数。`
> `★★ 只给 kind = "llm" 的组件补了。其余(speech / vlm / comfyui)故意留空`

「故意留空」说的是 **`model_rel` / `ctx` / `ngl`(启动参数)**,**不是 `peak`**。
peak 早就补齐且与 P1 实测对得上。

⇒ **本车道没有改 `config/vram-budget.toml` 一个字节,也不需要改**
(纪律里那句"要动就写进决议包点名"因此不适用 —— 没有要动的)。
⇒ 真正空着的是**启动参数**,而那正是本车道要交付的东西。

★ 附带一条如实记账:P1 测了三档(cpu 0 · lite 2.07 · full 4.05),而 `vram-budget.toml` 里
**没有 `speech.cpu` 这个组件** —— 只有 lite / full 两条。v1 全走 CPU 时实际占用是 0,
但准入闸仍会按 lite 的 2.07 收费。要不要加一条 `speech.cpu`(peak = 0)归 config 车道裁定,
本车道**不擅自加** —— 那是准入闸的数据源。

### 1.2 (b) faster-whisper 权重在不在本地 —— **在,但住错了地方**

| 事实 | 实测 |
|---|---|
| 两个仓库都在磁盘上 | `models--mobiuslabsgmbh--faster-whisper-large-v3-turbo`(1.6 G)· `models--Systran--faster-whisper-large-v3`(2.9 G),快照文件齐全 |
| 能不能离线加载 | **能**。`HF_HUB_OFFLINE=1` + `local_files_only=True` 下两档**都**加载成功(turbo 1.9s / large-v3 3.6s)并跑通转写 |
| `local_files_only` 今天有没有被强制 | **没有**。全仓 grep:它只出现在 **文档**里(`DECISIONS.md:1493` · `STATE.md:643` · 可行性报告),**生产代码零命中** |

**⇒ 协调层这条说对了一半**:约束确实存在(D41/STATE:643),但它**从未被实作** ——
今天"没联网"只是因为权重恰好在缓存里,不是因为有什么东西拦着。

★★ **而且比"没实作"更值得记的一条**:权重住在 `D:\AI\cache\hf`,
而 `paths.toml` 自己写着 **cache 是「唯一可静默清理的根」**。
⇒ 一次正常的缓存清理就会把 3.5 G 权重删掉,而那**不是** D41 说的「构建期预置」。

**本车道做了什么**:`local_files_only` + `HF_HUB_OFFLINE` **双保险**写进 `launch.toml` 并被断言钉死。
双保险不是冗余 —— 前者是库层、后者是进程层,而少了后者时"权重不在本地"会表现成
**启动很慢**(慢慢超时)而不是**当场失败**,那是最难查的一种。

**本车道没做什么**:**没有搬那 3.5 G 权重**。搬运涉及 `models` 根(有引用计数保护)与
一次大体积磁盘操作,不该由一条功能车道顺手做。⇒ 见 §5 交接。

### 1.3 (c) Piper 许可 —— ★ **本地根本没有 MODEL_CARD,许可无从查起**

`D:\AI\models\piper` 下**只有一个语音**:

```
en/en_US/lessac/medium/en_US-lessac-medium.onnx        (+ .onnx.json)
```

那份 `.onnx.json` 的键是:
`audio · espeak · inference · phoneme_type · phoneme_map · phoneme_id_map · num_symbols ·
 num_speakers · speaker_id_map · piper_version · language · dataset`

**没有任何许可字段,也没有 MODEL_CARD 文件。**

⇒ **协调层这条的判断是对的**(Piper 已改 GPL-3.0、逐语音许可各不相同 ⇒ 需白名单 + CI 校验),
但**今天连"查"的原料都不在本地** —— 只有一个 `dataset = "lessac"` 的字符串。

★★ 按本仓既有纪律(「**代码许可通过 ≠ 权重许可通过,两套台账分开记**」、
可行性报告里那句「按 local_files_only 权重台账**逐条核**,不默认沿用上游」),
**本车道拒绝从记忆里替它填一个许可**。台账见 §3,状态一栏如实写「**未核实**」。

★ 另一件顺带查出来的事:**一个中文语音都没有**。这套装置是中文界面、中文用户,
而 TTS 今天只有一个英文语音。P5 v1(按住说话 = ASR 为主)不阻塞,但"朗读"那一半
在中文上**今天是空的**。归属见 §5。

### 1.4 (d) `registry.toml:195-196` 的理由文案

原文:

```toml
"speech.lite" = "P5 语音链路未接:同传界面骨架已在 P3c 完成,但采集/ASR/合成/虚拟麦全未接,还没有别名驱动它"
"speech.full" = "同上。full 档在 P5 才会被 speech 别名选中"
```

**本车道没有改它** —— 因为改它的前提是「**已经有别名驱动它**」,而别名接入属于
`gateway.py` 的 speech 段 + `registry.toml`,本轮**没做到那一步**(见 §4)。
写下现在就把它改成"已接"会是一句**提前写好的谎**。⇒ 改法与时机见 §5.1。

---

## §2 交付了什么

### 2.1 ★ 验收判据:装载器对 speech **不再报「启动方式尚未验证」**

```
SUPPORTED_KINDS = ['llm', 'speech']
  speech.lite            -> OK  装载器认得怎么起
  speech.full            -> OK  装载器认得怎么起
  vlm.small              -> *** 仍报[启动方式尚未验证]
  comfyui.sdxl           -> *** 仍报[启动方式尚未验证]
  llm.assistant.8b@16k   -> OK
```

★★ 让它变绿的**不是**"我写了一份启动规格",而是这三件事:

1. `10-core/speech/verify_launch.py` **真的起过一次**:两个 ASR 档位在
   `HF_HUB_OFFLINE=1 + local_files_only=true` 下都加载成功并跑通转写;Piper 加载 0.99s、
   合成 0.07s、22050 Hz 出声。读数写进 `launch.toml` 的 `[verified]` 段。
2. 装载器的 `_speech_spec()` **只认带 `[verified]` 的规格** —— 缺它、或
   `asr_offline_load_ok` 不为真,就退回那句「启动方式尚未验证」。
   ⇒ 「改了启动参数但没重新验证」变成一件**会红**的事。
3. 真的起了一次服务并观测到状态机:

```
载入中 /health = 503        ← 与 llama-server 同形状(进程活着 ≠ 能服务)
就绪后 /health = 200        {"ok":true,"ready":true,"kind":"speech","tier":"lite",...}
TTS keys: audio_b64,format,frames,sample_rate,voice   rate=22050 frames=38656
```

★ `vlm` / `comfyui` **仍然 fail-closed** —— 别看见 speech 进来了就顺手把它们也加上,
那正是那条规矩要防的动作。

### 2.2 服务:`10-core/speech/`(全 CPU,零新依赖)

| 文件 | 作用 |
|---|---|
| `server.py` | ASR + TTS + `/health`。**标准库 `http.server`** —— speech venv 里没有 fastapi/uvicorn,装它们要么联网要么改 `D:\AI`,而底线是全本地 ⇒ 新依赖 **0** |
| `launch.toml` | 启动规格 + `[verified]` 实测段 |
| `verify_launch.py` | 真加载真转写真合成(**手动跑**,不进门禁 —— 它验的是"这台机器",进门禁会因换机器而红) |
| `contracts.json` | 三条契约的顶层键集合,**服务端与消费者读同一份** |
| `selftest.py` | 服务端半边(桩引擎驱动**真 Handler**) |

★ **peak 不在 `launch.toml` 里** —— 显存数只由 `vram-budget.toml` 说了算。
两处都写一个数迟早对不上,而准入闸会照着错的那个放行。已用反向断言钉死。

### 2.3 三条跨进程契约,DEBT 仍是 **1**

| 契约号 | 端点 | 服务端半边 | 消费者半边 |
|---|---|---|---|
| `CONTRACT:speech.health` | `GET /health` | `10-core/speech/selftest.py` | `10-core/gateway/test_speech_contract.py` |
| `CONTRACT:speech.asr` | `POST /v1/speech/asr` | 同上 | 同上 |
| `CONTRACT:speech.tts` | `POST /v1/speech/tts` | 同上 | 同上 |

```
前:TOTAL=27 PAIRED=26 DEBT=1   (225 PASS)
后:TOTAL=30 PAIRED=29 DEBT=1   (252 PASS · 0 FAIL)
```

★ 两半**读同一份 `contracts.json`** —— 两边各写一份常量的话,分家那天两边都不会红(A1 的形状)。
★ 消费者是**网关**(`speech_proxy.py`):speech 是独立进程、自己的 venv,
「同语言不等于同进程」这条在这里第二次成立。

### 2.4 ★★ `provenance` 做成了**安全判据**,不是配置字段

任务原话:「只有本机 / 已认证 LAN 设备的麦克风才可用 `user_voice_asr`;
来源档位由**通道**决定,不由调用方自报。」

落实:`provenance_for(client_host, headers)` —— **函数签名里根本没有请求体**。

| 通道 | 档位 |
|---|---|
| 回环 127.0.0.1 | `user_voice_asr` |
| 非回环 + lan-edge 注入的**已验证**指纹头 | `user_voice_asr` |
| 其余一切 | `untrusted_audio` |

★ 那个指纹头是 lan-edge 在 mTLS 通过**之后**写的,而它会先把客户端自带的同名头**剥掉** ——
所以"头在"等价于"这条连接被一张 active 成员证书验过了"。
★ 网关侧 `may_write_memory()` **不补救、不放宽**:拿不到可信档位就是不能写,
而不是"退一步记成低可信度" —— 记忆库里一条来源可疑的记录会被当成事实用下去。
★ 已用逐条反向断言钉死(含"调用方自报也没用")。

### 2.5 「麦克风不可失败」这一版做到哪一步

**服务端那一半是结构性的,并且被钉住了**:

- 采集**根本不在这个进程里**。本服务只接收「一段已经录好的音频」,或返回一段音频;
  它**不持有、也不代理任何实时音频通路**。
- ⇒ 本服务全挂 / 端口被占 / 权重被删,用户对着麦克风说话这件事
  **在代码路径上就到不了这里**,因此不受影响。这不是 try-catch,是**够不着**。
- 已用断言钉死:`server.py` 里不得出现任何实时采集/转发的痕迹
  (针拼出来写,避免撞上 ASSERTION-PITFALLS 第 1 条),并配了反向断言证明针表不是空的。

**⇒ 但客户端那一半本轮没做** —— 见 §4.1,如实记账。

---

## §3 权重许可台账(★ 两套账分开记)

### 3.1 代码许可

| 组件 | 版本 | 许可 | 状态 |
|---|---|---|---|
| faster-whisper | 1.2.1 | MIT(上游) | 需按发行版逐条核 —— **本车道未核** |
| CTranslate2 | 4.8.1 | MIT(上游) | 同上 |
| piper-tts | 1.6.0 | **GPL-3.0**(Piper 已改) | ★ 与本仓分发方式的相容性**需要一次裁定**,见 §5.3 |
| onnxruntime | 1.28.0 | MIT(上游) | 同上 |

### 3.2 ★ 权重许可(与代码许可**分开**)

| 权重 | 位置 | 许可 | 状态 |
|---|---|---|---|
| `en_US-lessac-medium`(Piper) | `D:\AI\models\piper\en\en_US\lessac\medium` | **未知** | ★★ **未核实** —— 本地无 MODEL_CARD,`.onnx.json` 里没有任何许可字段,只有 `dataset="lessac"`。**本车道拒绝凭记忆填一个** |
| `faster-whisper-large-v3-turbo` | `D:\AI\cache\hf`(★ 可静默清理的根) | 未核 | 未核实 |
| `faster-whisper-large-v3` | 同上 | 未核 | 未核实 |
| 中文 TTS 语音 | **不存在** | — | ★ 今天一个中文语音都没有 |

**⇒ 白名单 + CI 校验(协调层点名要的那一条)本轮没做**,理由是:
一份**内容全是"未核实"**的白名单,和没有白名单是一回事,而它看起来像有防护。
先把台账如实立起来(就是这张表),核实与校验见 §5.3。

---

## §4 没做的,和为什么

### 4.1 ~~★ 客户端「按住说话」(任务 2)**整块没做**~~ → **同日晚已补上,见 §8**

> ★ 本节保留原样作为**当时的实况**,不改写 —— 但它已经不成立了,以 §8 为准。
> (本仓的习惯:记账只追加、不回头把话抹掉。)

以下是当时的记录:

`AudioDevices.cs` / `InterpretState.cs` / `InterpretPanel.cs` 一个字节没动。

**为什么**:本轮把预算花在了"验收判据"那条链上(勘察 → 服务 → 真起一次 → 装载器 → 契约成对),
而那条链是**可验证**的;客户端那半边需要 WPF 音频采集 + 一个新的 Services 文件
(`localai-client.csproj` 要加 Compile 项,而 csproj 归证书那条车道 ⇒ 得先走决议包)。
**半做**会留下一个"看起来接了"的界面,而那正是本项目最恨的形状。

⇒ 客户端那一半仍然是**零调用点**。请照 A5 的教训读这句:**服务端写好 ≠ 接上了**。

### 4.2 网关别名接入没做(所以 `registry.toml:195-196` 没改)

`speech.lite` / `speech.full` 今天**仍然没有别名驱动**。`speech_proxy.py` 提供了消费者侧的
解析与准入判据,但把它挂进 `gateway.py` 的 speech 段与 `registry.toml` 的别名表**没做**。

### 4.3 `10-core/speech/selftest.py` **不在门禁里**

它不叫 `test_*.py`,而门禁的 Python 扫描只收 `test_*.py`(同款先例:`90-ops/debug/selfcheck.py`
是被**显式**接进 `run-tests.ps1` 的)。接它要改 `run-tests.ps1` ⇒ `90-ops` 是本车道禁区。
⇒ 那 26 条服务端断言**今天在门禁上是零覆盖**。消费者那半边(`test_speech_contract.py`,28 条)
**在门禁里**,因为它的文件名对得上。见 §5.2。

### 4.4 其它

- **GPU 路径没做**:v1 全 CPU(且 onnxruntime 实测**没有 CUDA provider**),按任务要求不抢租约;
- **虚拟麦注入没做**:那是同传,不是 P5 v1(按住说话)。归属见 VB-CABLE 那份包;
- **SSE 没做**:v1 半双工按住说话,一次一段,不流式。将来要流式**按帧重登记契约**;
- **没搬 3.5 G 权重**(见 §1.2);**没加 `speech.cpu` 组件**(见 §1.1)。

---

## §5 交接(★ 本车道不改中央四文档,也不越界改别人的文件)

### 5.1 给**语音车道下一轮**(或本车道续做)
1. 客户端「按住说话」+ 语音直通记忆写入(§4.1);
2. 网关别名接入,**之后**才改 `registry.toml:195-196`。建议改成:
   > `"speech.lite" = "已接:ASR(faster-whisper turbo,CPU)+ TTS(Piper,CPU)由 speech 别名驱动;虚拟麦注入属同传,不在 P5 v1"`
   ★ 在别名真的能驱动它**之前**不要改这两行。

### 5.2 给 **V3**(拥有 `90-ops`)
1. 把 `10-core/speech/selftest.py` 接进 `run-tests.ps1`(同 `90-ops/debug/selfcheck.py` 的形状),
   否则那 26 条断言没人跑;
2. **复核本车道对 `check_contract_pairs.py` 的改动**:除了登记三条契约,还**加了一段 speech 端点枚举**。
   ★ 为什么非加不可:枚举不到的服务,它的契约会被第 ② 组判成「过期登记」⇒ **登记它反而判红**,
   而唯一能变绿的做法是不登记 —— 那正好把新服务放在账外。枚举源取 `contracts.json` 的 `what`,
   并**配了元断言**逐条核对它写的路径在服务端源码里真的存在(枚举源不许自说自话)。
   ★ 顺带记一次自伤:第一版那段用了 `_p` 做循环变量,**覆盖掉本文件的全局计数器**,脚本当场崩。
   已改名并在注释里写明原因。

### 5.3 给**许可/合规**这条线
1. Piper 逐语音许可**逐条核实**(今天本地无 MODEL_CARD),再建白名单 + CI 校验;
2. **piper-tts 1.6.0 是 GPL-3.0** —— 与本仓的分发方式相容性需要一次**明确裁定**
   (它是被作为独立进程调用还是被链接,结论不同)。这条**超出本车道**,但不该没人认领;
3. faster-whisper / CTranslate2 / onnxruntime 的**发行版**许可逐条核。

### 5.4 给 **config 车道**
1. 要不要加 `speech.cpu`(peak = 0)—— v1 全 CPU 时实占 0,而闸按 lite 的 2.07 收费(§1.1);
2. 权重是否迁出 `cache` 根(可静默清理)到 `models` 根 —— 这是 D41「构建期预置」的实际要求(§1.2)。

### 5.5 给**第 0 条车道**(中央文档草稿)
- `STATE.md`:「同传」那段旁边补一行 —— P5 v1 的**服务端**骨干已通(装载器不再报「启动方式尚未验证」),
  **客户端仍未接**;权重许可台账「未核实」三条;
- `DECISIONS.md`:本包取号,裁定要点 = ①装载器 speech 分派以 `[verified]` 为准入 ②三条契约成对
  ③provenance 由通道判定 ④v1 全 CPU 不抢租约;
- 契约数:27 → **30**,DEBT 仍为 **1**。

---

## §8 追加(同日晚)· 客户端「按住说话」接上了

§4.1 记的那块**整块没做**,本轮补上。

### 8.1 链路

```
按住按钮 → AudioCapture(WinMM waveIn,16 kHz 单声道 16-bit)
        → 松开:StopAndTakeWav() 拿到一段 WAV
        → SpeechClient.TranscribeAsync → 127.0.0.1:18085 /v1/speech/asr
        → AsrResult{text, language, duration_s, tier, provenance}
        → 界面显示;provenance 可信才允许直通记忆写入
```

零新依赖:采集用 `winmm.dll` 的几个 P/Invoke(与本文件上半的 WASAPI 枚举同一条口径,不引 NuGet)。

### 8.2 ★★★ 「麦克风不可失败」在客户端是怎么做成**结构性**的

**判据不是"有没有 try-catch",是两个类在结构上够不着对方:**

| 方向 | 约束 | 断言 |
|---|---|---|
| 采集 → 网络 | `AudioCapture` 里**没有** `HttpClient` / `SpeechClient` / `Transport` / `TheApp` | 4 条(针拼出来写,且**只切 AudioCapture 那一段**再判 —— 拿整个文件判会撞上文件头那段解释这条底线的注释,ASSERTION-PITFALLS 第 1 条) |
| 网络 → 采集 | `SpeechClient` 里**没有** `AudioCapture` / `waveIn` / `IMMDevice` | 3 条 |
| 顺序 | `PttReleaseAsync` 里 `StopAndTakeWav` 必须排在 `TranscribeAsync` **之前** | 1 条(两个下标先各自确认存在再比大小) |
| 证据 | 转写失败也保留 `PttLastWav` | 1 条 |

⇒ 语音服务挂掉 / 权重被删 / 端口被占,**在代码路径上都够不着采集**。
录音照录、字节照给;失败的只是"这段话转成了什么字",而界面**明说**「你的话已经录下来了,没有丢」。

★ 顺序那一条是承重的:反过来写(在转写的 try 里才停录音)的话,转写抛出去时**麦克风还开着** ——
一个"用户以为已经松开、其实还在录"的麦克风。那比转写失败严重得多。

★ 界面上还有一条同款保险:**鼠标移出按钮也算松开**,否则按住拖走会留下一个一直在录的麦克风。

### 8.3 契约:**没有新增端点,DEBT 仍是 1**

客户端复用已登记的三条(`speech.health` / `.asr` / `.tts`),所以账面不动:
```
TOTAL=30 PAIRED=29 DEBT=1   (252 PASS · 0 FAIL)
```

★★ 但多了一个**第三个消费者**(C# 客户端),而欠债表每条只支持**一个** `client_file`
(今天指向网关那个 Python 消费者)。⇒ C# 这一半**不受欠债表保护**。
补法:C# 侧的键集合常量与 `10-core/speech/contracts.json` **逐条对拍**(3 条断言)+
一条元断言「登记表里每条契约在 C# 侧都有键集合」。
⇒ 抄本跟原本分家会当场红,而不是等到运行时才发现。
**这是一处如实记账的机制缺口,请 V3 考虑让 `client_file` 支持多个。**

### 8.4 v1 的范围,如实写

- **只连本机回环**(`127.0.0.1:18085`)⇒ **主机上可用**;
- **副机(局域网)今天用不了** —— 要网关把 `/v1/speech/*` 代理过去(lan-edge 会注入已验证指纹头),
  那一段仍然没做。界面上如实写着这句话,**不摆一个按下去没反应的按钮**;
- **选麦克风这一项不生效**:`waveIn` 只认系统默认输入设备。
  已做成 `AudioCapture.DeviceSelectionSupported = false` 并被断言钉住 ——
  一个"看起来能选、其实不生效"的设置,比没有这个设置更坏;
- **同传仍然没接**:`PipelineReady` 依旧恒为 false。按住说话与同传是两件事,
  界面上分开说,不让后者把前者盖掉。

### 8.5 数字

| | 前 | 后 |
|---|---|---|
| 客户端自检(Debug 量) | **2036** | **2069**(**+33**) |
| 契约元规则 | 252 PASS · DEBT=1 | 不变 |

**红测**:给 `AudioCapture` 塞一条 `HttpClient` 捷径 ⇒ **恰好 1 条红**,
正是「采集类里没有 HttpClient」那条。已用文件备份还原并核对 SHA-256 一致。

### 8.6 本轮仍然没做

1. **网关 `/v1/speech/*` 代理**(⇒ 副机不可用,`registry.toml:195-196` 仍未改);
2. **TTS 回放没接**:`SpeechClient` 只实作了 ASR 那一半(`KeysTts` 已登记并对拍,但没有播放路径);
3. **记忆写入没有真的落库**:`PttMayWriteMemory` 给出了**准入判据**并被断言钉死,
   但把文字写进记忆库要调记忆那条线 —— 不在本车道归属里;
4. **真麦克风没有端到端跑过**:采集要真设备与人按住,门禁里跑不了。
   门禁能验的是纯函数那部分(WAV 封装 / 形状解析 / 结构独立性),已全部钉住;
   **`AudioCapture.Start()` 这条路径在本轮没有被任何自动断言执行过** —— 如实记账。
