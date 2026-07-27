"""本地 AI 中枢 · 统一入口网关(P2 · 别名层)

把 `assistant.fast` 这类友好别名路由到 llama.cpp 后端(OpenAI 兼容)。
这是「统一入口」的骨架 —— 换别名映射,客户端零改动(§14 P2 验收)。

★ 本文件是骨架:别名路由 + 契约回写已实装并可测。
  安全层(下方 STUB 标注)是 P2 后续填的,当前明确未实装 —— 不假装有:
    - 认证:D28 本机走 OS 信任(loopback + 登录用户)/ 远程走 WebAuthn
    - 权限:六元组 + 按档位挂工具池(§6.3)
    - 出境闸门:§4.6(escalate.cloud 才需要)
    - 审计:§9

跑法(无 Broker 期,静态启动):
    先起后端:  llama-server -m <8b> --port 18081 ...
    再起网关:  uvicorn gateway:app --host 127.0.0.1 --port 8080
"""
import tomllib
import time
from pathlib import Path

import httpx
from fastapi import FastAPI, Request, Response
from fastapi.responses import JSONResponse, StreamingResponse

REGISTRY_PATH = Path(__file__).with_name("registry.toml")


def load_registry() -> dict:
    with open(REGISTRY_PATH, "rb") as f:
        return tomllib.load(f)["aliases"]


REGISTRY = load_registry()
CHAT_KINDS = {"chat", "chat_multimodal"}

app = FastAPI(title="LocalAI Hub Gateway", version="0.1.0-p2")
_client = httpx.AsyncClient(timeout=httpx.Timeout(300.0, connect=5.0))


# ────────────────────────────────────────────────────────────
# STUB · 认证(D28)—— 当前未实装,先记录调用来源供审计接线
# 本机(loopback + 登录用户)→ trusted-local;远程 → WebAuthn。
# 现在放行所有 loopback,拒绝非 loopback,并把这个决定显式记下来。
# ────────────────────────────────────────────────────────────
def classify_caller(request: Request) -> str:
    host = request.client.host if request.client else ""
    if host in ("127.0.0.1", "::1"):
        return "trusted-local"          # D28:本机走 OS 信任(此处仅按 loopback 近似)
    return "remote-unauthenticated"     # 远程须走 WebAuthn —— P2 后续


@app.get("/health")
async def health():
    return {"status": "ok", "aliases": sorted(REGISTRY.keys())}


@app.get("/v1/models")
async def list_models():
    """OpenAI 兼容:把 chat 别名列成 models。"""
    data = [
        {"id": name, "object": "model", "owned_by": "localai-hub",
         "kind": a["kind"], "contract": a.get("contract", "")}
        for name, a in REGISTRY.items() if a["kind"] in CHAT_KINDS
    ]
    return {"object": "list", "data": data}


@app.post("/v1/chat/completions")
async def chat_completions(request: Request):
    body = await request.json()
    alias = body.get("model", "")

    # ---- STUB 认证(D28):当前只按 loopback 近似,记来源 ----
    caller = classify_caller(request)
    if caller == "remote-unauthenticated":
        return JSONResponse(
            status_code=401,
            content={"error": {"message": "远程访问需 WebAuthn(P2 后续);本机请走 loopback",
                               "type": "unauthenticated", "code": "webauthn_required"}},
        )

    # ---- 别名解析 ----
    entry = REGISTRY.get(alias)
    if entry is None:
        return JSONResponse(
            status_code=404,
            content={"error": {"message": f"未知别名 '{alias}'。可用:{sorted(k for k,v in REGISTRY.items() if v['kind'] in CHAT_KINDS)}",
                               "type": "unknown_alias"}},
        )
    if entry["kind"] not in CHAT_KINDS:
        return JSONResponse(
            status_code=400,
            content={"error": {"message": f"别名 '{alias}' 是 {entry['kind']},不走 chat 路由",
                               "type": "wrong_plane"}},
        )

    backend = entry["backend"]
    contract = entry.get("contract", alias)

    # ---- 转发到后端(llama-server,OpenAI 兼容)----
    # body 里的 model 换成后端认识的(llama-server 不校验具体名,传别名亦可)
    fwd = dict(body)
    upstream_url = backend.rstrip("/") + "/v1/chat/completions"
    stream = bool(body.get("stream", False))

    try:
        if stream:
            async def gen():
                async with _client.stream("POST", upstream_url, json=fwd) as r:
                    async for chunk in r.aiter_raw():
                        yield chunk
            return StreamingResponse(
                gen(), media_type="text/event-stream",
                headers={"X-LocalAI-Contract": contract, "X-LocalAI-Alias": alias},
            )
        else:
            r = await _client.post(upstream_url, json=fwd)
            data = r.json()
            # 契约回写(§8.1.4):响应 model 字段回写真实契约
            data["model"] = f"{alias}({contract})"
            return JSONResponse(
                content=data, status_code=r.status_code,
                headers={"X-LocalAI-Contract": contract, "X-LocalAI-Alias": alias},
            )
    except httpx.ConnectError:
        # 后端未起来 —— §8.1.4:503 带缺口,不静默降级
        return JSONResponse(
            status_code=503,
            content={"error": {"message": f"别名 '{alias}' 的后端 {backend} 未响应"
                                          f"(无 Broker 期需先静态启动该后端)",
                               "type": "backend_unavailable",
                               "alias": alias, "backend": backend,
                               "fallback": entry.get("fallback")}},
        )
