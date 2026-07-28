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
import membership

# §6.8 隔离服务账户 —— 绝不允许经网关触达记忆(D30 混淆代理防护)
LOCAL_DENY_ACCOUNTS = {"ai-asset", "ai-exec"}

# P3b S3 · LAN Edge 服务账户名(低权、区别于机主)。provisioning 前为 None(该分支不激活)。
# 它只是纵深防御的一层:真正封顶 LAN_DEVICE 的是「带指纹头 → 查成员表」(见 resolve_lan_principal)。
LAN_EDGE_ACCOUNT = None

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


class RegistryError(RuntimeError):
    """注册表不合法 —— 拒绝启动。"""


def load_registry() -> dict:
    """加载别名表。★ 每个别名必须显式声明 `egress`,缺字段则**拒绝启动**。

    §4.6.3 要求「模型别名注册表给每个后端打 egress: true|false」。
    这里做成 fail-closed 而不是「缺字段视为 false」,理由与本项目其他几处同源:

      缺字段默认「不出境」是 **denylist 形状** —— 将来新增一个云端别名时忘了写,
      它会被**默认当成本地后端**,记忆正文就跟着上去了,而且不报错。
      同一族缺陷此前已出现三次:provenance denylist(新枚举逃逸全部约束)、
      E1 override(新档位默认有解除权)、unseal caller(新档位默认放行)。

    ★ 判据与 sensitivity 无关:一条 S0 记忆送进云端,同样违反 §5.6.2 的 L5。
    """
    with open(REGISTRY_PATH, "rb") as f:
        aliases = tomllib.load(f)["aliases"]

    missing = sorted(n for n, a in aliases.items() if "egress" not in a)
    if missing:
        raise RegistryError(
            f"别名缺少必填的 egress 字段,拒绝启动:{missing}。\n"
            "  每个后端都必须显式声明它在不在你的控制之内(§4.6.3)。\n"
            "  这里不设默认值 —— 缺字段默认『不出境』会让新增的云端别名\n"
            "  被当成本地后端,记忆正文跟着上去而且不报错。")
    bad = sorted(n for n, a in aliases.items() if not isinstance(a["egress"], bool))
    if bad:
        raise RegistryError(f"egress 必须是布尔值,拒绝启动:{bad}")
    return aliases


def backend_of(alias: str):
    """给 tainted.unseal_for_prompt 用的后端契约。★ 未知别名一律按【出境】处理。"""
    from dataclasses import dataclass

    @dataclass(frozen=True)
    class _B:
        name: str
        egress: bool

    entry = REGISTRY.get(alias)
    if entry is None:
        # ★ 未知别名 fail-closed:按最坏情况当成出境后端。
        #   「查不到就放行」会让一个拼错的别名变成一条静默的出境路径。
        return _B(alias, True)
    return _B(alias, bool(entry["egress"]))


REGISTRY = load_registry()
CHAT_KINDS = {"chat", "chat_multimodal"}

# ★ S3:关掉自动 API 文档(/docs · /redoc · /openapi.json)—— 安全网关不对外暴露接口清单;
#   同时使路由集合收敛为显式三条,ROUTE_TIERS 元测试可穷举。
app = FastAPI(title="LocalAI Hub Gateway", version="0.1.0-p3b",
              docs_url=None, redoc_url=None, openapi_url=None)
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

# ★★ 允许「解除 E1 拦截」的调用方档位 —— allowlist,不是 denylist。
#   E1 的解除是一个**人类声明**:「我,机主,现在,确认这不是凭证」。
#   因此只有能证明屏幕前是机主的档位才配拥有它。今天只有 trusted-local
#   (本机 loopback + 非隔离账户 + OS 会话信任,D28)满足。
#
#   ★ 将来新增任何档位(channel-relay / lan-device / …)**默认不在此集合内**,
#     必须显式加进来才有解除权 —— 而加之前请先回答:
#     「这个档位的另一端,能不能证明就是机主本人?」
#     对一条以电话号码/账号为身份保证的外联通道,答案永远是否。
E1_OVERRIDE_ALLOWED_TIERS = frozenset({"trusted-local"})


def classify_caller(request: Request) -> str:
    host = request.client.host if request.client else ""
    if host not in TRUSTED_LOOPBACK:
        return "remote-unauthenticated"       # 含 ::1:身份不可解析 → 按远程处理(fail-closed)
    ident = caller_identity.account_from_request(request)
    if ident and ident[1].lower() in {a.lower() for a in LOCAL_DENY_ACCOUNTS}:
        return "denied-account"               # ai-asset / ai-exec 绝不放行(§6.8)
    if ident and LAN_EDGE_ACCOUNT and ident[1].lower() == LAN_EDGE_ACCOUNT.lower():
        return "lan-edge"                     # ★ Edge 代理进程档:非业务档,永不落 trusted-local(纵深防御)
    return "trusted-local"                    # 人类 / ai-mem / 解析不到 → 放行(见 fail 策略)


# ★ P3b S3:证书指纹 → LAN_DEVICE 主体(经 S2 成员表反查)。
#   主体只来自成员表;客户端自报的 device_id / tier 一律忽略。未知/吊销/未激活/无 store → None(fail-closed)。
def resolve_lan_principal(cert_sha256: str):
    dev = membership.active_device(cert_sha256)
    if dev is None:
        return None
    return {"tier": "lan-device", "device_id": dev["device_id"],
            "cert_sha256": cert_sha256, "generation": dev["generation"]}


# ★ P3b S3:每条路由必须显式归类;新增未归类路由 → unclassified_routes() 非空 → 元测试失败(§S3)。
ROUTE_TIERS = {
    ("GET", "/health"): "public-minimal",
    ("GET", "/v1/models"): "authenticated",
    ("POST", "/v1/chat/completions"): "authenticated",
}


def unclassified_routes():
    out = []
    for r in app.routes:
        path = getattr(r, "path", None)
        methods = getattr(r, "methods", None)
        if path is None or not methods:
            continue
        for m in methods:
            if m in ("HEAD", "OPTIONS"):
                continue
            if (m, path) not in ROUTE_TIERS:
                out.append((m, path))
    return out


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
    return {"status": "ok"}   # ★ S3 收窄:不再泄露别名清单(别名走已认证的 /v1/models)


@app.get("/v1/models")
async def list_models(request: Request):
    """OpenAI 兼容:把 chat 别名列成 models。★ S3:纳入认证(远程/未认证拒)。"""
    if classify_caller(request) == "remote-unauthenticated":
        return JSONResponse(
            status_code=401,
            content={"error": {"message": "远程访问需认证;本机请走 loopback",
                               "type": "unauthenticated"}},
        )
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

    # ---- P3b S3:LAN 设备封顶(带证书指纹头 = LAN Edge 代理的 LAN 客户端)----
    #   一律按成员表反查、封顶 LAN_DEVICE。即使 caller 因 fail-open 成了 trusted-local,
    #   带指纹的请求也【拿不到】trusted-local 的能力(尤其解除 E1)。本机进程若伪设此头,
    #   只会把自己【降】为 LAN_DEVICE —— 拿到的更少,不越权。主体来自成员表,不认自报 device_id。
    fp = request.headers.get("x-localai-cert-sha256", "")
    if fp:
        principal = resolve_lan_principal(fp)
        if principal is None:
            return JSONResponse(
                status_code=401,
                content={"error": {"message": "未知 / 已吊销 / 未激活的设备指纹",
                                   "type": "lan_device_unknown"}},
            )
        effective_tier = "lan-device"
    else:
        effective_tier = caller

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
    #
    # ★★★ 2026-07-28 审查发现的更深一层:上面那个修法只解决了「E1 自己关掉自己」,
    #   没解决「**解除信号本身来自不可信输入**」。
    #   放行判据的两个来源 —— 请求头与本轮用户消息正文 —— 在【本机人类打字】这个
    #   场景下都等于「用户本人」,所以原来成立。但只要将来接上一条外联通道
    #   (WhatsApp/Signal/Discord 的桥),桥会把**外来消息原文**填进 messages,于是:
    #       任何知道你号码的人发一句 `我的 IBAN 是 DE89... #E1放行`
    #       → E1 命中 iban,但 override 为真 → 载荷照常转发给模型。
    #   桥同样能自己带上那个请求头。也就是说 E1 对外联通道**从接通的第一天起就是关的**,
    #   而它看起来完全正常(审计里还记着一条 'continued',像是用户主动放行的)。
    #
    #   ⇒ 解除能力必须由【调用方档位】决定,不能由【报文内容】决定。
    #   只有能证明「屏幕前的人就是机主」的档位才配拥有这个按钮:
    #   今天是 trusted-local(本机 OS 会话信任,D28)。将来新增的 channel-relay 之类
    #   档位**默认不在此集合内** —— 这是 allowlist,新档位默认没有解除权,
    #   与本轮 provenance 那处改动是同一条规矩:**约束要写成拒绝优先**。
    if effective_tier in E1_OVERRIDE_ALLOWED_TIERS:
        override = (request.headers.get("x-localai-e1-override", "").lower() == "continue"
                    or e1.OVERRIDE_PHRASE in _current_user_text(body.get("messages")))
    else:
        override = False          # ★ 该档位连请求头都不读 —— 不给伪造留任何入口(LAN 设备走这条)
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
