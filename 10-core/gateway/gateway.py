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
import caller_identity

# §6.8 隔离服务账户 —— 绝不允许经网关触达记忆(D30 混淆代理防护)
LOCAL_DENY_ACCOUNTS = {"ai-asset", "ai-exec"}

REGISTRY_PATH = Path(__file__).with_name("registry.toml")
PATHS_TOML = Path(__file__).resolve().parents[2] / "config" / "paths.toml"


def _logs_dir() -> Path:
    """从 paths.toml 读 [state] logs(§11.1 不硬编码路径)。

    ★ 退路【绝不能】落在 10-core 代码树内(2026-07-28 审查):那是 git 跟踪目录,且按 D31
      对 ai-asset 可读 —— 审计日志(命中类别/被拒账户)写进去既进版本历史又对资产侧可见。
      读不到配置就退到系统临时目录,并在日志里留痕,不静默写进仓库。
    """
    try:
        with open(PATHS_TOML, "rb") as f:
            return Path(tomllib.load(f)["state"]["logs"])
    except Exception:
        import tempfile
        return Path(tempfile.gettempdir()) / "localai-hub-logs-FALLBACK"


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


def _current_user_text(messages) -> str:
    """只取【本轮最后一条 user 消息】的文本。

    ★ 专供「用户说放行」这类**授权信号**判定 —— 授权只能来自用户此刻的表态,
      不能来自会话历史、更不能来自 assistant 自己说过的话(否则系统能自我授权)。
      凭证扫描用的是 _scannable_text(整个载荷),两者【不可共用】,原因见调用处注释。
    """
    for m in reversed(messages or []):
        if isinstance(m, dict) and m.get("role") == "user":
            c = m.get("content")
            if isinstance(c, str):
                return c
            if isinstance(c, list):
                return "\n".join(p.get("text", "") for p in c
                                 if isinstance(p, dict) and isinstance(p.get("text"), str))
            return ""
    return ""


def _scannable_text(messages) -> str:
    """取本轮【将要发给后端】的全部人类可写文本,供 E1 扫描。

    ★ 早期版本只扫「最后一条 user 消息的 type=='text' 部分」,理由是「历史进来时已扫过」——
      这对一个【无状态网关 + 第三方前端】不成立(2026-07-28 审查发现,三种绕过均已确认):
        · 凭证放在 system 消息里 → 从不被扫
        · 凭证在上一轮 user 消息里(前端把历史整包重发)→ 从不被扫
        · content part 没有 type 字段 → 被过滤掉
      E1 的职责是「不让凭证进入发往模型的 prompt」,那就必须扫【整个将发出的载荷】。
      assistant 角色也扫:第三方前端可以伪造它,而我们不信任前端。
    """
    parts = []
    for m in messages or []:
        if not isinstance(m, dict):
            continue
        c = m.get("content")
        if isinstance(c, str):
            parts.append(c)
        elif isinstance(c, list):
            for p in c:
                if isinstance(p, dict):
                    t = p.get("text")
                    if isinstance(t, str):      # 不再要求 type=='text';有 text 就扫
                        parts.append(t)
                elif isinstance(p, str):
                    parts.append(p)
    return "\n".join(parts)


def load_registry() -> dict:
    with open(REGISTRY_PATH, "rb") as f:
        return tomllib.load(f)["aliases"]


REGISTRY = load_registry()
CHAT_KINDS = {"chat", "chat_multimodal"}

app = FastAPI(title="LocalAI Hub Gateway", version="0.1.0-p2")
_client = httpx.AsyncClient(timeout=httpx.Timeout(300.0, connect=5.0))


# ────────────────────────────────────────────────────────────
# 认证(D28)+ 调用方 OS 身份(D30 混淆代理修正)
# 本机(loopback)→ 解析调用方账户(port→PID→WMI GetOwner)→ 隔离服务账户(ai-asset/ai-exec)拒绝,
# 其余(人类 / ai-mem)trusted-local。远程 → WebAuthn(P2 后续,当前 401)。
#
# ★ fail 策略(重要):解析不到账户时,当前 fail-open 为 trusted-local —— 因为网关【现在只转发
#   chat,不代理记忆】,放行一个身份不明的本机调用方不构成记忆泄露。
#   ★★ 一旦网关开始代理记忆/Qdrant(注入 api_key / 连 PG),那条路径【必须改用 require_trusted_local
#   即 fail-closed】:必须 positively 解析到非隔离账户,否则拒。见下。
# ────────────────────────────────────────────────────────────
# ★ 只认 IPv4 回环。绝不能把 ::1 也当可信(2026-07-28 审查发现):
#   caller_identity 只查 AF_INET(IPv4)表,对 ::1 调用方【永远解析不到身份】→ 恒 fail-open
#   成 trusted-local,等于对 IPv6 回环整体关掉 D30 隔离账户拒绝,且不留任何日志痕迹。
#   网关按 README 绑 127.0.0.1,故 ::1 本不该出现;真出现就说明绑定被改过 —— 此时必须
#   fail-closed(拒),而不是无声放行。要支持 ::1 须先在 caller_identity 里补 AF_INET6 表
#   (结构体字段顺序与 IPv4 不同,不能复用同一 Structure)。
TRUSTED_LOOPBACK = {"127.0.0.1"}


def classify_caller(request: Request) -> str:
    host = request.client.host if request.client else ""
    if host not in TRUSTED_LOOPBACK:
        return "remote-unauthenticated"       # 含 ::1:身份不可解析 → 按远程处理(fail-closed)
    ident = caller_identity.account_from_request(request)
    if ident and ident[1].lower() in {a.lower() for a in LOCAL_DENY_ACCOUNTS}:
        return "denied-account"               # ai-asset / ai-exec 绝不放行(§6.8)
    return "trusted-local"                    # 人类 / ai-mem / 解析不到 → 放行(见 fail 策略)


def require_trusted_local(request: Request):
    """记忆敏感路径专用 · fail-closed。必须 positively 解析到【非隔离】本机账户,否则返回 None(拒)。
    chat 路径用宽松的 classify_caller;此函数留给将来代理 Qdrant/PG 的记忆端点。"""
    host = request.client.host if request.client else ""
    if host not in TRUSTED_LOOPBACK:          # ::1 同样不认(身份不可解析,见上)
        return None
    ident = caller_identity.account_from_request(request)
    if not ident:                             # 解析不到 = 不能确认身份 → 拒(fail-closed)
        return None
    if ident[1].lower() in {a.lower() for a in LOCAL_DENY_ACCOUNTS}:
        return None
    return ident


def log_denied_access(account: str, session_id: str) -> None:
    """§6.8:非授权本机账户触达网关 → 写审计(现落文件,待接 §9.3 告警)。账户名非凭证,可记。"""
    rec = {"ts": datetime.now(timezone.utc).isoformat(timespec="seconds"),
           "account": account, "session_id": session_id or "unknown",
           "reason": "isolated-service-account-denied"}
    try:
        d = _logs_dir()
        d.mkdir(parents=True, exist_ok=True)
        with open(d / "denied_access.jsonl", "a", encoding="utf-8") as f:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")
    except Exception:
        pass


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

    # ---- 认证(D28)+ 调用方身份(D30)----
    caller = classify_caller(request)
    if caller == "remote-unauthenticated":
        return JSONResponse(
            status_code=401,
            content={"error": {"message": "远程访问需 WebAuthn(P2 后续);本机请走 loopback",
                               "type": "unauthenticated", "code": "webauthn_required"}},
        )
    if caller == "denied-account":
        ident = caller_identity.account_from_request(request)
        acct = ident[0] if ident else "unknown"
        log_denied_access(acct, request.headers.get("x-session-id", ""))
        return JSONResponse(
            status_code=403,
            content={"error": {"message": "隔离服务账户不得经网关访问(§6.8)",
                               "type": "denied_account"}},
        )

    # ---- E1 入口凭证检测(§6.9.0 · 在组装/转发之前 · 不信任前端)----
    # 命中即拦下本轮:不转发后端、不落 L0、不记正文;只记类别(§6.9.8)。
    session_id = request.headers.get("x-session-id", "")
    scan_text = _scannable_text(body.get("messages"))
    # ★★ 扫凭证看【整个载荷】,但「用户说放行」这个信号只认【本轮用户消息】。
    #
    #   2026-07-28 实测过的严重 bug:两者曾共用 scan_text —— 而拦截文案里带着解除暗号,
    #   且拦截响应是以 role:assistant 返回的正常消息。于是:
    #     第1轮被拦 → 前端把拦截文案存进历史 → 第2轮整包重发 → 暗号出现在载荷里
    #     → override 自动为真 → **该会话此后每一轮 E1 全部自动解除,用户零操作**。
    #   即 E1 在第一次拦截后就把自己永久关掉了 —— 比没有 E1 更糟,因为你以为它在保护你。
    #
    #   语义上也只能这样:放行是「我,用户,现在,声明这不是凭证」,不是历史里出现过这串字。
    override = (request.headers.get("x-localai-e1-override", "").lower() == "continue"
                or e1.OVERRIDE_PHRASE in _current_user_text(body.get("messages")))
    e1r = e1.scan(scan_text)
    if e1r.blocked:
        if override:
            # 用户显式「这不是凭证,继续」—— 记类别(不记值),放行本轮
            log_gate_rejection(session_id, e1r.categories, "continued")
        else:
            log_gate_rejection(session_id, e1r.categories, "blocked")
            msg = e1.block_message(e1r.categories)
            hdrs = {"X-LocalAI-E1": "blocked",
                    "X-LocalAI-E1-Categories": ",".join(sorted(e1r.categories))}
            # ★ 必须按客户端要的形态回:Open WebUI 等主力客户端默认 stream:true,
            #   给它一个非流式 JSON 会解析失败 —— 用户看到的是报错,而不是「这一轮没有发送」的说明。
            if bool(body.get("stream", False)):
                def sse():
                    base = {"id": "e1-block", "object": "chat.completion.chunk",
                            "created": int(time.time()), "model": f"{alias}(e1-blocked)"}
                    first = dict(base, choices=[{"index": 0, "finish_reason": None,
                                                 "delta": {"role": "assistant", "content": msg}}])
                    last = dict(base, choices=[{"index": 0, "finish_reason": "content_filter",
                                                "delta": {}}])
                    yield f"data: {json.dumps(first, ensure_ascii=False)}\n\n"
                    yield f"data: {json.dumps(last, ensure_ascii=False)}\n\n"
                    yield "data: [DONE]\n\n"
                return StreamingResponse(sse(), media_type="text/event-stream", headers=hdrs)
            return JSONResponse(
                status_code=200,   # 对前端是一条正常回复(assistant 说明),不是错误
                headers=hdrs,
                content={
                    "id": "e1-block", "object": "chat.completion",
                    "created": int(time.time()), "model": f"{alias}(e1-blocked)",
                    "choices": [{
                        "index": 0, "finish_reason": "content_filter",
                        "message": {"role": "assistant", "content": msg},
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

    hdrs = {"X-LocalAI-Contract": contract, "X-LocalAI-Alias": alias}
    try:
        if stream:
            # ★★ 必须【先建立连接、拿到状态码】再返回 StreamingResponse。
            #    原写法 `return StreamingResponse(gen(), ...)` 会立即返回 —— gen() 尚未执行,
            #    后端连不上时异常发生在 return 之后、响应头(200)已发出 → 客户端收到
            #    「200 + 空 body」,正是 §8.1.4 明令禁止的静默降级(实测复现)。
            req = _client.build_request("POST", upstream_url, json=fwd)
            r = await _client.send(req, stream=True)
            if r.status_code >= 400:                     # 上游错误:读完转发真实状态码,不吞
                raw = await r.aread()
                await r.aclose()
                return JSONResponse(
                    status_code=r.status_code, headers=hdrs,
                    content={"error": {"message": f"后端返回 {r.status_code}",
                                       "type": "backend_error",
                                       "alias": alias, "backend": backend,
                                       "detail": raw.decode("utf-8", "replace")[:500]}},
                )

            async def gen():
                try:
                    async for chunk in r.aiter_raw():
                        yield chunk
                finally:
                    await r.aclose()
            return StreamingResponse(gen(), media_type="text/event-stream",
                                     status_code=r.status_code, headers=hdrs)
        else:
            r = await _client.post(upstream_url, json=fwd)
            try:
                data = r.json()
            except Exception:                            # 上游返回非 JSON/空体:不静默变成 200
                return JSONResponse(
                    status_code=502, headers=hdrs,
                    content={"error": {"message": "后端返回的不是合法 JSON",
                                       "type": "bad_upstream_response",
                                       "alias": alias, "backend": backend,
                                       "detail": r.text[:500]}},
                )
            # 契约回写(§8.1.4):响应 model 字段回写真实契约
            if isinstance(data, dict):
                data["model"] = f"{alias}({contract})"
            return JSONResponse(content=data, status_code=r.status_code, headers=hdrs)
    except httpx.RequestError as e:
        # ★ 用 RequestError 而非 ConnectError:ConnectTimeout / ReadTimeout /
        #   RemoteProtocolError 等都【不是】ConnectError 的子类,原来会裸奔成 500。
        #   §8.1.4:503 带缺口,不静默降级。
        return JSONResponse(
            status_code=503, headers=hdrs,
            content={"error": {"message": f"别名 '{alias}' 的后端 {backend} 未响应"
                                          f"(无 Broker 期需先静态启动该后端)",
                               "type": "backend_unavailable",
                               "reason": type(e).__name__,
                               "alias": alias, "backend": backend,
                               "fallback": entry.get("fallback")}},
        )
