# 统一入口网关(P2 · 别名层)

把 `assistant.fast` 这类友好别名路由到 llama.cpp 后端(OpenAI 兼容)。
**换别名映射,客户端零改动** —— 这是 §14 P2 的验收标准之一。

## 为什么是自定义网关而不是 LiteLLM(2026-07-27,见 DECISIONS D29)

原计划写「LiteLLM 别名层」。实测 litellm 1.93.0 的构建后端要 Rust(py3.12/Win 无预编译 wheel),
装不动。更重要的是:**这个网关本质上必须是定制的** —— 它要干 LiteLLM 根本不管的一堆事:

- **认证**:D28 本机走 OS 信任(loopback + 登录用户)/ 远程走 WebAuthn
- **权限**:六元组 + 按档位挂工具池(§6.3)
- **契约头**:`X-LocalAI-Contract` 回写真实装载的组件(§8.1.4)
- **出境闸门**:§4.6(`escalate.cloud` 才需要)
- **平面隔离**:MEMORY / MODEL 平面不混(§3.1)
- **审计**:§9

LiteLLM 是通用 LLM 代理,这些一样不知道 —— 用它也得包在自定义网关里。
而别名→后端转发本身就是「一个字典 + httpx 透传」。所以自己写更对、更轻、更可控。
LiteLLM 将来可作**内部库**用于 `escalate.cloud` 归一化云端 provider(Gemini 等)。

## 文件

| 文件 | 作用 |
|---|---|
| `registry.toml` | 别名 → 后端契约。后端 URL 由无 Broker 期静态脚本 / P4 Broker 填 |
| `gateway.py` | FastAPI 网关:别名路由 + 契约回写 + 错误码 |

## 已实装(可测)

- `GET /health` · `GET /v1/models`(别名列成 models)
- `POST /v1/chat/completions`:别名解析 → 转发后端 → 契约回写(`model` 字段 + `X-LocalAI-Contract` 头)
- 错误码:未知别名 404 · 非 chat 平面 400 · 后端未起 **503 带缺口 + fallback**(§8.1.4 不静默降级)· 远程未认证 401
- 流式(`stream:true`)透传

**2026-07-27 端到端实测通过**:`assistant.fast` 路由到 8B 后端,契约头 `8b`,`model` 回写
`assistant.fast(8b)`;`assistant.deep` 后端未起返回 503 带 fallback;未知别名 404。

## E1 入口凭证检测器(§6.9.0 · 已实装 · 2026-07-27)

`e1_detector.py` —— 在入口(转发/组装 prompt 之前)扫描本轮 user 输入,命中即拦下:
不转发、不记正文,只记类别(§6.9.8)。**在网关侧做,不信任前端**(Open WebUI 是第三方)。

- 类别(= `mem.cred_pattern_class`):`iban`(mod-97)· `card_pan`(Luhn)· `tax_id_de`(ISO 7064)·
  `id_doc` · `secret_phrase`(密码/助记词/私钥触发词)· `high_entropy`(仅 E1/E4,误报高)
- **归一化前置**:全角→半角 · 中文数字→阿拉伯 · 结构化匹配去分隔符(语音/全角通道)
- **带校验和的类别用校验和** —— 噪声检测器会训练用户「一律点继续」,反而废掉 E1
- 命中返回 200 + `finish_reason=content_filter` + `X-LocalAI-E1: blocked` 头 + 说明文案(不回显值)
- **继续**:请求带 `X-LocalAI-E1-Override: continue` → 放行本轮,记 `outcome=continued`(仍只记类别)
- 审计现落 `{state}/logs/gate_rejection.jsonl`(category·ts·session·outcome),★ 待 memory-service
  上线后改写 `mem.gate_rejection` 表。**已实测:审计日志不含任何凭证串。**
- 测试:`test_e1.py`(41 例:校验和/正例/归一化/误报控制/E3 剔除)· `test_gateway_e1.py`(12 例:
  拦下/放行/override/多类别/中文,进程内 UTF-8)

★ **诚实边界(§4.6.2)**:E1 拦得住【意外】,拦不住【坚持】。它把「手滑贴凭证」变成
  「必须显式点继续」,**不是**「记忆零外发」的证明,也拦不住手动复制粘贴。

## ★ 明确未实装(P2 后续 · 代码里以 STUB 标注,不假装有)

| 层 | 现状 | 依据 |
|---|---|---|
| **认证** | 只按 loopback 近似(本机放行 / 远程 401)。**OS 信任的真实校验(登录用户身份)未做** | D28 |
| 权限六元组 + 工具池 | 未做 | §6.3 |
| 出境闸门 | 未做(`escalate.cloud` 才需要) | §4.6 |
| 审计 | 未做 | §9 |

## 跑法(无 Broker 期,静态启动)

```
# 1. 起后端(assistant.fast 用 8B)
D:\AI\tools\llama.cpp\llama-server.exe -m <8b.gguf> -ngl 99 -c 16384 \
    --port 18081 --host 127.0.0.1 -fa on -ctk q8_0 -ctv q8_0

# 2. 起网关
cd 10-core\gateway
D:\AI\venvs\gateway\Scripts\python.exe -m uvicorn gateway:app --host 127.0.0.1 --port 8080
```
