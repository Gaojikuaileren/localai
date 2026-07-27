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
import json
import tomllib
import time
from datetime import datetime, timezone
from pathlib import Path

import httpx
from fastapi import FastAPI, Request, Response
from fastapi.responses import JSONResponse, StreamingResponse

import e1_detector as e1

REGISTRY_PATH = Path(__file__).with_name("registry.toml")
PATHS_TOML = Path(__file__).resolve().parents[2] / "config" / "paths.toml"


def _logs_dir() -> Path:
    """从 paths.toml 读 [state] logs(§11.1 不硬编码路径)。读不到则退回本目录。"""
    try:
        with open(PATHS_TOML, "rb") as f:
            return Path(tomllib.load(f)["state"]["logs"])
    except Exception:
        return Path(__file__).with_name("_logs")


def log_gate_rejection(session_id: str, categories, outcome: str) -> None:
    """E1 命中记账。§6.9.8:【只】记 类别 · 时间 · 会话id · 结果,
    绝不记 body / 片段 / 哈希(定长凭证的哈希可爆破)。
    ★ 现落 JSONL(state/logs,强 ACL);待 memory-service 上线后改写 mem.gate_rejection 表。"""
    rec = {
        "ts": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "session_id": session_id or "unknown",
        "categories": sorted(categories),
        "outcome": outcome,   # blocked | continued
    }
    try:
        d = _logs_dir()
        d.mkdir(parents=True, exist_ok=True)
        with open(d / "gate_rejection.jsonl", "a", encoding="utf-8") as f:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")
    except Exception:
        pass  # 审计落盘失败不阻断拦截本身


def _last_user_text(messages) -> str:
    """取本轮最后一条 user 消息文本(E1 只扫新输入,历史进来时已扫过)。"""
    for m in reversed(messages or []):
        if isinstance(m, dict) and m.get("role") == "user":
            c = m.get("content")
            if isinstance(c, str):
                return c
            if isinstance(c, list):   # 多模态 content parts
                return " ".join(
                    p.get("text", "") for p in c
                    if isinstance(p, dict) and p.get("type") == "text"
                )
    return ""


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

    # ---- E1 入口凭证检测(§6.9.0 · 在组装/转发之前 · 不信任前端)----
    # 命中即拦下本轮:不转发后端、不落 L0、不记正文;只记类别(§6.9.8)。
    session_id = request.headers.get("x-session-id", "")
    override = request.headers.get("x-localai-e1-override", "").lower() == "continue"
    e1r = e1.scan(_last_user_text(body.get("messages")))
    if e1r.blocked:
        if override:
            # 用户显式「这不是凭证,继续」—— 记类别(不记值),放行本轮
            log_gate_rejection(session_id, e1r.categories, "continued")
        else:
            log_gate_rejection(session_id, e1r.categories, "blocked")
            return JSONResponse(
                status_code=200,   # 对前端是一条正常回复(assistant 说明),不是错误
                headers={"X-LocalAI-E1": "blocked",
                         "X-LocalAI-E1-Categories": ",".join(sorted(e1r.categories))},
                content={
                    "id": "e1-block", "object": "chat.completion",
                    "created": int(time.time()), "model": f"{alias}(e1-blocked)",
                    "choices": [{
                        "index": 0, "finish_reason": "content_filter",
                        "message": {"role": "assistant",
                                    "content": e1.block_message(e1r.categories)},
                    }],
                    "usage": {"prompt_tokens": 0, "completion_tokens": 0, "total_tokens": 0},
                    "x_localai_e1": {"blocked": True, "categories": sorted(e1r.categories)},
                },
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
