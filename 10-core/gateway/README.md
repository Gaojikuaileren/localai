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
