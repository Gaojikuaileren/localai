# moeru-ai/airi 融合可行性报告

> 编制日期:2026-07-30 · 首席工程师
> 范围:评估 moeru-ai/airi(下称 airi)能否整体引入 / 作外壳 / 作运行时依赖 / 局部提取,并映射到我方 D24/D25/D27/D40/D41/D47 与显存预算模型。
> 本报告已并入一轮对抗核验的全部修正:凡草稿被标 overstated / needs-source 的主张,已下调口径或标「待核实」;凡被标 understated(过度保守)的,已按证据解除保留。判据字段是本报告最重要的部分——半年后你需要的不是「当时怎么说」,而是「这个结论还成不成立、被什么约束着」。

---

## 1. 一句话结论 + 三行摘要

**结论:整体不可引入。** airi 是 TS/Vue + Electron + WebGL/WebGPU 栈(桌面另带 Rust/candle 原生推理),与我方三条主线正面冲突——D47(WPF 原生独立窗口,明确否决 HTML/Tauri/Electron 外壳)、D27(faster-whisper + Piper 原生语音栈)、原生 CUDA 显存预算模型(D24/D25)。**代码零复用**(TS/Vue/Rust 均无法搬进 .NET)。价值只在少数**零代码资产**:唇形算法思路、Live2D(阻断)vs VRM(MIT)的贩卖决策实证,以及一条**已被我方架构占位**的语音轮次接管范式。

- **值不值得:** 不作代码/外壳/运行时/显存依赖,只把 airi 当「架构参考 + 模型选型交叉验证源」。净提取价值比草稿原估**更低**——它在语音上真正独有的贡献近乎为零。
- **提取什么:** (a) 音频→音素→口型 唇形算法思路(uLipSync/wLipSync,许可已核实为干净 MIT,可用 .NET 自写 MFCC 驱动 DragonBones 嘴型槽);(b) Live2D Expandable 阻断 vs VRM=MIT 的对比,作为 D40/D41 贩卖决策的实证;(c) VAD 事件驱动的轮次接管范式,仅作我方 session-service(已在范围、尚未实装)的**参考蓝本**,而非「缺口填补」。
- **别搬什么:** 整个 Vue/Electron 外壳、Live2D 渲染栈(专有 Cubism Core)、浏览器内 ASR/TTS 引擎、three.js/VRM 渲染实现、candle 推理编排、wlipsync WASM 本体、unspeech 代理、浏览器内向量库。airi **不解决**我方显存预算——反证我方显存治理(预算 + 动态闸 + 勾选式选择器)是 airi 缺失的差异化核心资产,不是能从它提取的东西。

---

## 2. airi 是什么(实况 + 技术栈 + 项目许可)

airi(moeru-ai/airi)是一个 AI 虚拟角色 / 桌宠系统,主体代码 **MIT**(SPDX: MIT,版权 `Copyright (c) 2024-PRESENT Neko Ayaka`,已逐一核实,置信度高)。它是 **Vite / Vue3** 的 Web 应用,桌面端(`apps/stage-tamagotchi`,Stage Tamagotchi)**当前为 Electron**(`electron-builder.config.ts` / `electron.vite.config.ts`),整套 Chromium 承载。

**技术栈实况(已对草稿的时态/维度含糊做校正):**

- **外壳:** 当前桌面外壳是 **Electron**(整套 Chromium)。仓库内曾存在 Rust/Tauri 原生转写 crate(`crates/tauri-plugin-ipc-audio-transcription-ort`),但当前 main 分支已迁 Electron,该 crate 属旧 Tauri 架构残留(`src-tauri`/crates 目录 API 返回 404,**未逐文件核实到 Electron 主/渲染进程的具体转写调用点,置信度中**)。草稿把二者并列成不确定的「Electron/Tauri」略糊——**以 Electron 为准**。
- **推理:** 主干是「外部 OpenAI 兼容 provider」——经自研 xsAI / xsai-ext 接 40+ 云供应商,本地走 Ollama / vLLM / SGLang / LM Studio(仍是独立进程/端点)。此外有两条**本地原生**推理路径,草稿此前系统性漏记:①**桌面 Electron 版默认可经 🤗 candle(Rust ML 引擎)走原生 NVIDIA CUDA / Apple Metal 本地推理**;②浏览器内 transformers.js(ONNX,WebGPU 加速,Worker 线程)。浏览器内**纯本地 WebGPU LLM 对话**在 README 里标 **WIP**;较成熟的浏览器内路径是语音(VAD/STT/Whisper)、TTS、embedding、背景抠除。
- **渲染:** Live2D(2D)经 pixi-live2d-display ^0.4.0 + PixiJS v6 WebGL,**必须外挂专有 Cubism Core**;VRM(3D)经 three.js ^0.184 + @pixiv/three-vrm ^3.5.2 + TresJS WebGL。另有 Spine / MMD / 立绘路径(**成熟度未逐包取证,置信度中**)。
- **语音:** 浏览器/WebAudio 为核心,xsAI provider 抽象;ASR 三路(Web Speech API / 云 Whisper·阿里云 NLS / 浏览器内 transformers.js Whisper ONNX);VAD 用 @ricky0123/vad-web(底层 Silero VAD);本地 TTS 用 kokoro-js;唇形用 wlipsync。
- **记忆/存储:** pgvector / pglite / DuckDB WASM,记忆系统 Alaya 标 **WIP**。

**关键定性:** airi 是「客户端 + 外部/浏览器内推理」的 Web 优先系统。它有与我方同类的 native-GPU 推理维度(仅引擎 candle ≠ 我方 CTranslate2/llama.cpp),但**全程无 GPU/显存资源治理**——无预算、无按需装卸载编排、无多客户端仲裁(DeepWiki 架构概览、xsai-transformers README、官方文档三处均无相关机制)。

---

## 3. 能力重合图

| 能力 | airi 做法 | 我方做法 | 重合度 |
|---|---|---|---|
| 桌宠渲染(2D) | Live2D 经 pixi-live2d-display + PixiJS v6 WebGL,**必须搭专有 Cubism Core** | DragonBones(MIT)骨骼/网格动画,WPF 原生 D3D,**计入 desktop_floor**(D40),非选择器一行 | low |
| 桌宠渲染(3D) | VRM 经 three.js + @pixiv/three-vrm(MIT)+ TresJS WebGL | 未评估;VRM 未上,3D 因吃 WebGL/显存在 D40 基本出局(WebView 路径否决) | low |
| 本地 ASR/STT | 浏览器内 transformers.js Whisper ONNX(WASM+onnxruntime-web,可 WebGPU)/ Web Speech API / 云;**桌面另有 candle 原生 CUDA/Metal 路径** | faster-whisper large-v3 / turbo(CTranslate2,原生 CUDA,local_files_only) | medium |
| 本地 TTS | kokoro-js(Kokoro-82M,Apache-2.0)浏览器内推理 | Piper(ONNX,CPU,0 显存,zh-en-de,现 GPL-3.0)——已定稿(D27) | medium |
| VAD / 轮次接管 | Silero VAD(@ricky0123/vad-web,MIT)onSpeechStart/onSpeechEnd 事件驱动,16kHz Float32Array 送转写 | **非缺口:** session-service「语音编排(VAD·打断·流式)」自 PLAN v1 起已占位,v3.0 半双工(本期)/全双工(B5);且 **faster-whisper 内置 Silero `vad_filter`** | low |
| 唇形同步 | wlipsync(MFCC+WASM+AudioWorklet)提取 AEIOUS 音素权重 → 5 元音口型/张嘴度 | 桌宠嘴型驱动未定;DragonBones 嘴型槽待设计 | low |
| LLM 推理接入 / provider 抽象 | xsAI 统一接口接 40+ 云/Ollama/vLLM/SGLang;浏览器内 WebGPU LLM 为 WIP;桌面 candle 原生 | 自写网关(D29,非 LiteLLM)+ llama.cpp 原生 worker | low |
| 显存/GPU 资源管理 | **无**——无预算、无按需装卸载编排、无多客户端仲裁(三处来源确认) | 完整显存预算 8.52 + 动态闸 Σpeak ≤ min(vram_budget, NVML free−0.8)+ 勾选式组件选择器(D24/D25) | low |
| 记忆/embedding 存储 | pgvector / pglite / DuckDB WASM,记忆系统 Alaya 标 WIP | bge-m3 1024 维 + 双 Qdrant(6333/6335)+ PG role,S2 结构性隔离 fail-closed | low |

---

## 4. 架构契合度

**阻断项(hard blockers,任一即否决「整体引入 / 作外壳 / 作依赖」):**

- **[BLOCKER-1 客户端外壳]** airi 是 Web(Vue + Electron)。D47 定死 WPF 原生独立窗口(.NET 9 / net9.0-windows / UseWPF),并明确否决 Tauri/Electron/HTML 外壳(用户原话「不要 html,而是客户端程序独立窗口」)。**主壳绝不可用 airi。**
- **[BLOCKER-2 语言/运行时鸿沟]** airi 全部 UI/逻辑为 TS/Vue,桌面另有 Rust(candle)原生代码——**两者都无法搬进 .NET**。即使思路可取,也只能「参考后用 .NET 重写」,不构成代码复用。事实精度校正:airi 的非-.NET 原生代码比「全部 TS/Vue」更多(还有 Rust),但 Rust 同样不可移植,**「零 .NET 复用」反而更成立**。这是四项重点关切里草稿判断纪律最扎实的一处。
- **[BLOCKER-3 渲染层]** airi 桌宠渲染(Live2D 经 PixiJS/WebGL、VRM 经 three.js/WebGL)本就跑在 Electron/Chromium 里。为桌宠单开一个 WebView overlay = 局部重开 D47:又拖进一个 Chromium/WebGL GPU 进程,与「禁大面积半透明 + 不常驻浏览器内核 + 显存扣项」相悖,**通常不划算,否决**。精度校正:Windows 上若自建 WebView2 承载,是复用系统 Edge 运行时(非另装一份永久磁盘占用),故此处的显存代价应精确为「**活动期 VRAM/GPU 进程占用**」而非「永久磁盘占用」——结论不变,且现实(airi 桌宠本就在 Chromium+WebGL 里跑)使该否决**更成立、非更弱**。
- **[BLOCKER-4 推理/显存治理]**(措辞已按对抗核验下调)airi **有** candle 原生 CUDA/Metal 本地推理路径,与我方同为 native-GPU 维度,属「可交叉参考、不可直接迁移」;但 airi **无任何显存治理**——无预算、无按需装卸载编排、无多客户端仲裁。我方是原生 CUDA + 自写 gateway + 8.52 GiB 硬预算 + 动态闸。**承重结论保留:D24/D25 显存治理是 airi 缺失的东西,不是能从它提取的东西。**(草稿原措辞「推理在别处 / 不在同一维度 / 显存数字无参考价值」判 overstated,已改。)
- **[BLOCKER-5 完整性等级]** 我方客户端必须稳定运行在普通用户 / Medium 完整性(CNG/TPM 设备私钥,D46/D47),自启走 HKCU\...\Run 不提权。airi 无此概念;若作伴随进程接入配对/mTLS,**必须遵守我方模型而非引入其模型**。

**非阻断但需适配:** 六界面 IA(D42:聊天/资产生成/投资 MCP/翻译/PPT 课程/电脑操控 + 贯穿性记忆/组件/宠物/语音)不可被 airi 自己的界面壳替换。

**架构结论:** airi 只能作「架构参考 + 模型选型交叉验证源」,不作代码/外壳/运行时依赖。

---

## 5. 许可分析

按 D41 两台账纪律记账:**代码许可 ≠ 权重许可,分开核实。** 下表已并入对抗核验的逐条一手来源结果。

| 组件 | 许可 | 对「商用再分发/贩卖」的含义 | 严重度 |
|---|---|---|---|
| airi 本体(Neko Ayaka) | **MIT**(已核实) | 仅覆盖其自写 TS 代码;我方不搬代码,对我方几乎无许可意义 | 低 |
| Live2D **Cubism Core** | **专有**(Live2D Proprietary Software License;商用另须签 Cubism SDK Release License) | 与开放的 Cubism Framework(Open Software License)**双轨**;Core 单独专有、禁逆向、不可自由再分发 | 高 |
| Live2D **Expandable Application** 条款 | 特殊 Publication License(需事前审批) | **CRITICAL 阻断项(命中 airi)** ——见下 | 致命(若贩卖) |
| pixi-live2d-display(guansss) | MIT | 库自身 MIT,但运行时**必须外挂专有 Core**——是专有 Core 的传染入口 | 中 |
| @pixiv/three-vrm 系列 + VRM 格式 | **MIT**(库) + 格式开放 | 可自由商用再分发;**无 Expandable 陷阱**。但 MIT 只覆盖库代码,不覆盖用户加载的 VRM 权重(各自 VRM meta 许可字段) | 低 |
| Silero VAD(onnx-community/silero-vad) | **权重 MIT**(已核实) | 零代码模型,license_ok。但见 §7:已在 faster-whisper 内,非 airi 独有资产 | 低 |
| Kokoro-82M(hexgrad) | **Apache-2.0**(权重 + kokoro-js 代码) | 许可比 Piper 现行 GPL-3.0 干净;**但缺德语,见 §6/§7 致命短板** | 低(许可)/中(能力) |
| Whisper 上游(openai/whisper) | MIT | onnx-community ONNX 转换条目许可**待逐条核**(README 通常沿用上游 MIT,但按 local_files_only 台账实证,不默认) | 低(待核条目) |
| **wlipsync(mrxz)+ uLipSync(hecomi)** | **MIT(两者均已核实)** | **草稿的「待核/存疑」保留已解除**:hecomi/uLipSync = MIT(© 2021 hecomi);mrxz/wLipSync = MIT(© 2021 hecomi + 2024 Noeri Huisman)。「照思路用 .NET 自写 MFCC→音素→嘴型槽」**license_ok = true**。唯一残留:uLipSync 附带的**示例音素 profile/素材**是否另有许可——库代码本身无碍,且不影响自写 | 低 |

**★ Expandable Application 为何命中 airi(证据链已数字级核对官方页,非夸大):** Live2D 定义 Expandable Application 为「具有显著可扩展性、允许用户通过添加/组合文件或数据生成任意不定数量模型(如 avatar)的作品」——**airi 允许用户导入任意 Live2D 模型正落此类**。该类应用:(1) 发布前须经 Live2D 事前审查批准;(2) 须签特殊 Publication License;(3) 须有有效收费模式(**「原则上完全免费不予批准」**);(4) 须提交销售报告并按营收分成付费(一般/小规模示例费率:**每次销售 US$1.88[¥300] 或销售额 20%,取高者**;大规模为销售额 5%);(5) 该要求**对包括通常被豁免的一般用户与小规模企业在内的所有发布者一律适用**(明文 `except Expandable Application`)。VTuber 直播追踪软件被官方点名归入此类。

**映射 D40/D41:** 本项目「卖给别人自建、用户换自己桌宠」几乎必然落入 Expandable 定义。在 D41 待决项 4 =「贩卖」下,这是 **CRITICAL 硬裁决**——直接实证我方 D40 补记「默认 DragonBones、产品不分发 Core」决策正确。注意 airi 用构建期插件 @proj-airi/unplugin-live2d-sdk 从 `cubism.live2d.com` 下载 Core、不入库来规避「再分发 Core 二进制」这一层(**该下载机制我未独立核实,标「待核实/needs-source」**);但即便属实,**Expandable 的「事前审批 + 营收分成」层对任何贩卖者仍致命**,CRITICAL 阻断的成立**与下载机制无关**。

**GPL/AGPL 传染排查:** 三项零代码提取(uLipSync/wLipSync = MIT、Silero = MIT、语音流水线思路源自 airi = MIT)与交叉验证的 Kokoro = Apache-2.0、Whisper = MIT **均无 GPL/AGPL**;airi 记忆栈(DuckDB = MIT、pglite ≈ Apache-2.0、pgvector = PostgreSQL License)**无 AGPL 网络条款(SaaS 传染)风险**,且本就不提取。

**唯一 GPL 项是我方既有的 Piper(补时间线与真正传染源):** rhasspy/piper 于 **2025-10 归档**,并将许可由 MIT 翻为 **GPL-3.0**;当前活跃分支为 **OHF-Voice/piper1-gpl(GPL-3.0)**;历史 rhasspy/piper 旧发行版仍是 MIT(若锁定旧版本可留在 MIT)。**更根本的 GPL 传染源其实是 Piper 音素化依赖的 espeak-ng(本身 GPL-3.0)**——草稿以「Piper=GPL」一句概括、未单列 espeak-ng。因不从 airi 提取 espeak,结论不受影响;「Kokoro Apache 比 Piper GPL 干净」的许可比较**成立**(但 Kokoro 的能力短板见 §6/§7)。

**捆绑权重(尽调残留,置信度低):** airi 内置默认模型若含 Live2D 官方 sample(如 Hiyori),受 Live2D Sample Data 免费素材条款约束(**禁再分发、须署名、部分角色禁改设计/禁商用**);默认 VRM 若为 VRoid AvatarSample,其 CC0 状态官方口径自相矛盾(FAQ 称 CC0、单模型页称非 CC0)——**贩卖前须逐模型核实**。我方不打算引入这些捆绑资产,列此仅为完整。

**许可维度总评:** 可放行。核心贩卖决策证据链扎实——Live2D Core 专有 + Expandable 三层(审批 + ¥300/20% 分成 + 全免费不批 + 小微不豁免)与官方页数字级吻合;VRM/Silero/Kokoro/Whisper/airi/pixi-live2d-display 逐一证实。唯一实质调整:**唇形算法从「待核」升级为「MIT 已证实」,对应风险项消除**。

---

## 6. 显存分析

映射我方预算模型(config/vram-budget.toml、D24/D25、A7-pet)。核心判断:**airi 基本不解决我方预算问题**,且草稿在两处高估了它的显存价值,已按对抗核验下调。

**(1) 维度可交叉参考、但绝对值不可迁移。** airi 桌面 candle 有原生 CUDA 推理(与我方同维度),但其浏览器/WebGL 侧的绝对显存数字(WASM/WebGL 特征)**不能迁移**到我方原生 CUDA + WPF D3D 模型;引擎也不同(candle ≠ CTranslate2/llama.cpp)。定性可参考,数字不可搬。

**(2) airi 无显存治理——反证我方治理是差异化核心资产。** config/vram-budget.toml 确认:total_vram = 15.92、desktop_floor = 6.6、safety_margin = 0.8、**vram_budget = 8.52(导出值,不单独设,D24 起「cap」废止)**;确定按钮判据 Σpeak ≤ min(vram_budget, NVML free−0.8);D24/D25 勾选式选择器均为原生 CUDA 模型。airi 无预算、无原生 CUDA 装卸载编排、无多客户端仲裁。**措辞校正:** 草稿「无按需装卸载(三来源确认)」过绝——airi 浏览器侧模型**是会按需 load/unload 的**;准确表述为「**无原生 CUDA 预算/卸载/多客户端仲裁**」。结论不受影响:D24/D25 是 airi 缺失的东西,不是能提取的东西。

**(3) Live2D vs VRM 的显存取舍——降级为「至多弱方向性提示」(草稿原判 overstated):**
- airi **未提供任何显存实测**,称其「实证支撑」我方选 DragonBones **过强**——那只是通用 2D/3D 直觉,不是 airi 的实测数据。
- 该直觉针对的是 **GPU 算力/占用**,**不必然是显存足迹**:Live2D/2D 的显存由**纹理图集主导**(常多张 2K/4K),一个精细 Live2D 模型的纹理显存可与紧凑 VRM 相当甚至更高。草稿「2D 骨骼显存**显著低于** 3D VRM」缺乏依据,**「显著」二字删除**。
- 更关键:这是一次**双重跨语境迁移**(Live2D→DragonBones 换引擎;WebGL→原生 D3D 换栈),恰与本节 (1) 「WebGL 数字不能迁移到原生」**自相矛盾**。
- **修正后口径:** 方向上「2D 骨骼倾向比 3D VRM 省」只能作弱提示,唯一承重输入是 **A7-pet 对 DragonBones 的原生实测**。

**(4) A7-pet 是硬前置,不得估算(处理得当,保留)。** A7-pet(桌宠渲染稳态显存 + 空闲 GPU 占用)**仍待实测、不得估算**(D40),渲染选型会让它差好几倍——「渲染选型不是美术问题,是显存问题」。桌宠**计入 desktop_floor**(非选择器一行),按最坏情况「宠物在主机」计入。绝对值一律以原生实测为准。

**(5) 预算很紧,越发支持选 2D 骨骼。** 日常(8b@16k + speech.lite 2.07)= 7.99 ≤ 8.52,仅余 **0.53**;长上下文(8b@32k)7.19 加 speech.lite 就 9.26 超;深度(30b-a3b@32k)11.9、视觉(8b@16k + vlm.small)10.27 均在 6.6 地板下装不下。桌宠任何显存增量都在这条紧线内竞争——**方向上支持 DragonBones 2D 骨骼**(与 airi 的弱提示同向,但不靠 airi 背书)。

**(6) 若真提取 Silero 常驻:显存顾虑基本不成立。** Silero VAD ~1–2MB、CPU、近 0 显存(如 Piper),不必新增独立档位 peak。但见 §7——**根本不需要从 airi 提取它**,faster-whisper 已内置。

---

## 7. 可提取清单 与 明确不可提取清单

**重点:草稿把语音项(a)列为「价值最高/许可最干净/工作量最低」,对抗核验判 overstated 并降级。** 原因:VAD/turn-taking **不是 airi 揭示的缺口**——我方 session-service「语音编排(VAD·打断·流式)」自 PLAN v1/v2/v2.1 起已占位,v3.0(行 162/1880/1962)仍保留,SESSION = 语音会话「半双工(本期)/全双工(B5)」,是**已划入范围、尚未实装**的一层;且 Silero VAD 是独立 MIT 项目(snakers4/silero-vad),airi 只是经 @ricky0123/vad-web 封装它,而**我方已锁定的 faster-whisper 自带 Silero `vad_filter=True`**,模型已在锁定栈内可达。故 **airi 在语音上真正独有的可提取贡献近乎为零**。

| 项 | 类型 | 价值 | 工作量 | 许可安全 | 说明 |
|---|---|---|---|---|---|
| 音频→音素权重→口型 唇形算法思路(uLipSync/wLipSync:MFCC→AEIOUS 6 路→5 元音映射,S 静音映射到 I 避免嘴突然闭合;输出 getVowelWeights / getMouthOpen) | 算法 | **medium** | medium | **是(MIT 已证实)** | 桌宠嘴型我方未定;可移植为 .NET 驱动 DragonBones 嘴型槽。wlipsync 本体是 WASM+AudioWorklet 不可用,MFCC+profile 需 **.NET 自写**。**许可已核实,不再「待核」**。经修正,这是 airi 现存**最干净、确有用**的思路型资产 |
| Live2D(Expandable 阻断)vs VRM(MIT/格式开放)对比,作为 D40/D41 贩卖决策实证 | 选型/洞察 | **medium** | high | 是 | Live2D 是**反面教材**、已验证 DragonBones 决策正确;VRM 为 D41=「贩卖 3D 桌宠」保留一条 MIT 门路。但 airi 的 three.js 实现不可用,上 3D 需 .NET 原生 glTF/VRM 加载器,effort 高;VRM 权重许可另计 |
| VAD→STT→LLM→TTS 事件驱动轮次接管**范式**(onSpeechStart/onSpeechEnd) | 思路 | **low(降级:冗余确认)** | low | 是 | **非缺口填补**:session-service 已占位该层(半双工本期/全双工 B5)。可作我方 session-service 未实装部分的**参考蓝本**,借范式不搬 JS。**不是「价值最高」项** |
| Silero VAD 模型作为 VAD 引擎 | 选型 | **low(降级:冗余)** | low | 是(MIT) | **不需从 airi 提取**:faster-whisper 已内置 `vad_filter`;若将来需独立 VAD 层,ONNX 权重可用 Microsoft.ML.OnnxRuntime 在 .NET 直接跑(近 0 显存)。列此仅为账目完整 |
| Kokoro-82M(Apache-2.0)作为 P9 TTS 备选 | 选型 | low | medium | 是 | 许可比 Piper GPL-3.0 干净;**但致命短板:我方语音是硬性 zh/en/de 混说,Kokoro「德语基本没有」(speech-stack-candidates.md 行 43),难满足三语硬约束**——仅凭许可优势推荐具误导性。Piper 已定稿(D27,CPU/0 显存/首字节 83ms),Kokoro 更重且可能占 GPU。仅列权重台账备选,不进 P1 |
| provider 抽象分层接口思路(xsAI:统一 generate-text/embed/generate-speech/generate-transcription) | 思路 | low | low | 是 | 我方已自写 gateway(D29)覆盖此需求,仅作 API 分层设计参考,**无代码复用价值** |
| onnxruntime 多版本共存踩坑记录(vad-web 硬编码 onnxruntime-web@1.14.0,airi 用 onnxWASMBasePath 强拉最新) | 无 | low | low | 是 | web 专属坑;提醒我方原生侧若同时用 CTranslate2/llama.cpp + 新增 ONNX runtime 时核实版本不冲突 |

### 明确不可提取

| 项 | 原因 |
|---|---|
| 整个客户端外壳/UI(Vue + Electron) | Web 技术;D47 定死 WPF 原生并否决 HTML/Tauri/Electron 外壳,语言不通无法搬进 .NET |
| Live2D 渲染栈(pixi-live2d-display + Cubism Core + unplugin-live2d-sdk) | Cubism Core 专有 + Expandable 条款阻断贩卖(D40 补记),产品不分发其 Core;PixiJS/WebGL 非原生 |
| ASR/TTS 引擎实现(transformers.js / kokoro-js / @xsai-transformers / @xsai/*) | 浏览器内 WASM/WebGPU 运行时;D27 已定 faster-whisper(CTranslate2 原生)+ Piper,引擎实现不可迁移 |
| VRM/3D 渲染实现(stage-ui-three:three.js + @pixiv/three-vrm + TresJS) | three.js/TresJS 是 Web/WebGL,WPF 无 three.js;即便将来采纳 VRM 格式也需 .NET 原生加载器重写 |
| 推理/显存编排(含 candle 桌面路径) | airi 无显存治理;candle 是 Rust,不可移植到 .NET;与我方原生 CUDA 预算模型(D24/D25)无接口可对 |
| 唇形同步库本体 wlipsync | 单文件内联 WASM+AudioWorklet,需 secure context(浏览器);.NET 不可运行,仅**算法思路**可参考 |
| unspeech 统一 ASR/TTS 代理服务 | 独立服务端(LiteLLM 式);我方已自写 gateway,引入徒增攻击面与运维面 |
| 浏览器内 embedding/向量库(pglite / DuckDB WASM / @proj-airi/memory-pgvector) | 我方 embedding = bge-m3 + 双 Qdrant + PG role,S2 结构性隔离 fail-closed 严格,Web 向量库不满足隔离与出境要求 |

---

## 8. 建议与下一步(可执行,按优先级排序)

**总建议:不引入 airi 任何代码,不作外壳、不作运行时依赖。** 定位为「架构参考 + 模型选型交叉验证源」。全部结论受 D41 待决项 4(是否贩卖)这个最上层开关约束:**未裁决前只记账不定案**。

1. **[账目更正,零成本]** 把 §7 的语音提取项(a)VAD/Silero **从「价值最高」下调为「冗余确认」**,并在 P1 语音回合设计里记明:VAD/turn-taking 层归 **session-service**(已在范围、半双工本期/全双工 B5),Silero 已在 faster-whisper `vad_filter` 内——**无需为此引入 airi**。
2. **[唇形,现在可动手]** 唇形算法许可已证实为干净 MIT——可直接在 session-service / 桌宠嘴型设计中把 **MFCC→音素权重→张嘴度** 写成 .NET 自写规范(参考 uLipSync/wLipSync 思路,不搬 WASM),驱动 DragonBones 嘴型槽。核实 uLipSync 附带**示例 profile 素材**的许可后再决定是否复用其标定数据(库代码本身无碍)。
3. **[session-service 借范式]** 在 P1/session-service 未实装部分,把 onSpeechStart/onSpeechEnd 事件驱动轮次接管写成 .NET 编排规范(借范式不搬 JS),明确 VAD→faster-whisper→gateway→Piper 的回合边界。**这是借蓝本,不是填缺口。**
4. **[A7-pet 排期]** A7-pet 实测时,用「2D 骨骼倾向省 vs 3D VRM 倾向吃」作**弱方向性预判**(不引 airi 的 WebGL 数字、不用「显著」),优先实测 DragonBones 2D 骨骼稳态 + 空闲 GPU 占用;绝对值以原生实测为准(D40 不得估算)。
5. **[Kokoro 台账]** 把 Kokoro-82M(Apache-2.0)登记为 P9 TTS 备选并**同时记明「德语基本没有,难满足 zh/en/de 三语硬约束」**——许可优势不掩盖能力短板。Piper 仍为基线。
6. **[Whisper ONNX 条目]** 若将来评估 onnx-community 的 Whisper/Silero ONNX 条目,按 local_files_only 权重台账**逐条核许可字段**,不默认沿用上游 MIT(D41 补记③)。
7. **[仅当裁为贩卖 3D 桌宠时]** 才单独立项评估 VRM 原生加载(.NET glTF/VRM 解析 + 换装素材解析器攻击面核实),而非引入 airi 的 three.js 实现。**未裁决前不投入。**

---

## 9. 风险

- **贩卖决策未定(D41 待决项 4)是最上层开关:** 在它裁决前,VRM vs DragonBones、Kokoro vs Piper 只能记账。Live2D Expandable 条款对我方是「记账」还是「致命裁决」完全取决于此。过早为 airi 某条路径投入 = 押注未定的产品化方向。
- **「借思路」边界:** 从 airi(MIT)读源码后用 .NET 重写,须停在思路/接口层。MIT 下逐行翻译也合规,但会把 airi 的 Web/外部推理假设污染进原生设计——避免。
- **素材解析器攻击面(D40 补记):** 任何采纳外部模型/换装素材(.model3.json/physics3.json/skeleton/VRM)都扩大解析攻击面;VAD ONNX 风险低,但 3D/换装路径须先做攻击面核实。
- **记忆出境闸(与 airi 提取无直接关系,但撞硬前置):** 桌宠是第一个默认持续显示记忆内容的组件,撞「响应侧回程出境闸(方向 B)仍是硬前置未做」。唇形/语音输出任何记忆内容前需先补该闸。
- **onnxruntime 运行时冲突:** 同时用 CTranslate2(faster-whisper)、llama.cpp,+ 若新增 Silero ONNX runtime,需核实原生运行时版本/依赖不冲突(airi 在 Web 侧就踩过 onnxruntime-web 版本冲突)。
- **Kokoro 能力短板(已并入 §7/§8):** 缺德语,不满足三语硬约束——不可只凭许可推荐。
- ~~唇形算法许可未核~~ **(已解除:uLipSync/wLipSync 均 MIT 证实)**。

---

## 10. 待核实问题(unknown / needs-source 汇总)

- **[needs-source]** airi 构建期从 cubism.live2d.com 下载 Core、不入库的机制(unplugin-live2d-sdk)未独立核实。**注:** 即便属实也只解「再分发」一层,不改变 Expandable 阻断这一致命结论。
- **[置信度中]** 桌面 Electron 主/渲染进程的具体转写调用点未逐文件核实到;旧 Tauri crate(`tauri-plugin-ipc-audio-transcription-ort`)是否已完全退场未逐文件确认。
- **[待逐条核]** onnx-community 的 Whisper/Silero ONNX 转换条目的确切许可字段是否与上游 MIT 一致(权重台账逐条核,D41 补记 local_files_only)。
- **[置信度低]** airi 默认捆绑的 Live2D / VRM 模型确切身份、文件路径与各自内嵌许可元数据未逐一取证(devlog 提约 2 Live2D + 2 VRM,名称/许可未证实)。若确含官方 sample(如 Hiyori)/VRoid AvatarSample,须核实再分发与署名限制、CC0 口径冲突。
- **[待核]** uLipSync 附带示例音素 profile/素材是否另有许可(库代码 MIT 无碍,自写不受影响)。
- **[未核]** Live2D 收入豁免门槛口径差异(检索到 1000 万 / 2000 万日元两种表述,分别对应不同合同/币种口径)——但「个人/小企业通常豁免、Expandable 一律不豁免」当作确定结论即可,门槛数字不影响 Expandable 阻断。
- **[未做]** 完整 SBOM 许可扫描(pnpm-lock 逐项)未跑;所读源中未浮现 GPL/AGPL 依赖,但结论前应对完整依赖树跑 license checker。
- **[未取证]** 唇形在 VRM/three 与 MMD/Spine 下的音素→blendshape 具体绑定;Spine 路径若引入 spine-runtimes 其许可有专有条款(未在 airi 语境核实,且我方不引入)。
- **[已由文档答定]** ~~我方语音栈是否已隐含 VAD/turn-taking 层?~~ **已定:session-service 占位(PLAN v1 起,v3.0 半双工本期/全双工 B5);Silero 已在 faster-whisper vad_filter 内。**
- **[待排期]** A7-pet 实测何时排期——它是桌宠一切显存结论的硬前置(D40),在它出结果前 2D/3D 取舍只能定性。

---

## 11. 附:主要来源 URL

**airi 本体 / 许可**
- `github.com/moeru-ai/airi/blob/main/LICENSE`(MIT,Neko Ayaka)
- `raw.githubusercontent.com/moeru-ai/airi/refs/heads/main/README.md`
- `raw.githubusercontent.com/moeru-ai/airi/refs/heads/main/packages/stage-ui/package.json`
- `raw.githubusercontent.com/moeru-ai/airi/refs/heads/main/packages/stage-ui-live2d/package.json`
- `raw.githubusercontent.com/moeru-ai/airi/refs/heads/main/packages/stage-ui-three/package.json`
- `raw.githubusercontent.com/moeru-ai/airi/refs/heads/main/apps/stage-tamagotchi/package.json`
- `raw.githubusercontent.com/proj-airi/unplugin-live2d-sdk/main/src/vite/index.ts`(Core 构建期下载,needs-source)
- `github.com/moeru-ai/xsai-transformers` · `github.com/proj-airi/webai-examples` · `deepwiki.com/moeru-ai/airi`

**Live2D 许可(贩卖决策证据链)**
- `github.com/Live2D/CubismWebFramework/blob/develop/LICENSE.md`(Core 专有 / Framework 开放双轨)
- `live2d.com/en/sdk/license/expandable/`(Expandable:审批 + ¥300/20% 分成 + 全免费不批 + 小微不豁免)
- `live2d.com/en/sdk/license/` · `help.live2d.com/en/sdk/sdk_001/`
- `live2d.com/eula/live2d-proprietary-software-license-agreement_en.html`
- `live2d.com/eula/live2d-sample-model-terms_en.html`

**VRM / 唇形 / 语音权重**
- `github.com/pixiv/three-vrm/blob/dev/LICENSE`(MIT)
- `github.com/hecomi/uLipSync/blob/main/LICENSE.md`(MIT) · `github.com/mrxz/wLipSync/blob/main/LICENSE`(MIT)
- `huggingface.co/onnx-community/silero-vad`(MIT) · `huggingface.co/hexgrad/Kokoro-82M`(Apache-2.0)
- `github.com/guansss/pixi-live2d-display`(MIT,依赖专有 Core)
- `npmjs.com/package/@ricky0123/vad-web`

**我方约束(一手)**
- `E:\.meine\.Proj_Soft\.Proj\.localAI\00-docs\DECISIONS.md`(D24/D25/D27/D40/D40 补记/D41/D42/D47;Piper GPL-3.0)
- `E:\.meine\.Proj_Soft\.Proj\.localAI\config\vram-budget.toml`(total 15.92 / floor 6.6 / margin 0.8 / budget 8.52)
- `E:\.meine\.Proj_Soft\.Proj\.localAI\00-docs\PROJECT_PLAN_v3.0.md`(§14 P3c/P8/§17;session-service 行 162/1880/1962)
- `E:\.meine\.Proj_Soft\.Proj\.localAI\00-docs\STATE.md`(显存分配模型 / 语音栈定稿 / 硬前置)
- `E:\.meine\.Proj_Soft\.Proj\.localAI\00-docs\speech-stack-candidates.md`(行 43:Kokoro 德语基本没有)
