"""本地 AI 中枢 · 统一入口网关(P2 · 别名层)

把 `assistant.fast` 这类友好别名路由到 llama.cpp 后端(OpenAI 兼容)。
这是「统一入口」的骨架 —— 换别名映射,客户端零改动(§14 P2 验收)。

★ 本文件是骨架:别名路由 + 契约回写已实装并可测。
  安全层(下方 STUB 标注)是 P2 后续填的,当前明确未实装 —— 不假装有:
    - 认证:D28 本机走 OS 信任(loopback + 登录用户,判据是 allowlist 见下)/ 远程经 LAN Edge mTLS(D34 已作废 WebAuthn)
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
import e4_egress as e4
import caller_identity
import membership

# §6.8 隔离服务账户 —— 绝不允许经网关触达记忆(D30 混淆代理防护)
#
# ★ ai-vigil(D69):常驻清醒助手的后端账户。拒它的理由与 ai-asset/ai-exec 略有不同 ——
#   前两个是防「资产/执行器借网关摸记忆」,这一个是**兑现「Vigil 始终本地」**:
#   Vigil 走专用命名管道到只加载 assistant.resident 的后端,把网关这条路一并堵死之后,
#   它连「想换个别名试试」都做不到。Agent 身份由端点证明,不由报文声明。
#
# ★ ai-ctl(D72):层二外部控制面 ctld 的账户。拒它是防**反向旁路** ——
#   若 ctld 能回头调 chat 网关,层二就成了层一的一条无闸支路,
#   一个能改设置的进程顺带获得了一条模型调用通道。
#
# ★★ ai-op(形态 A):**外部 AI 宿主账户**(Claude Code / Codex 跑在这个身份下)。
#   这条必须和账户一起加,不能等账户建好再补 —— 理由是 classify_caller 的最后一行:
#   「人类 / ai-mem / 解析不到 → trusted-local」。**新账户默认落在放行侧**,
#   而 trusted-local 恰好是唯一含 S2 读与 E1 解除权的档位。
#   也就是说:建一个"受限"账户却不登记在这里,等于给它发了全系统最高档。
#   这正是项目反复吃过亏的那族缺陷(provenance denylist / E1 override / unseal caller):
#   判据写成放行优先,新增的东西默认自由。
#   ⇒ 外部 AI 有它自己的模型,**没有任何正当理由调我方 chat 网关**。
LOCAL_DENY_ACCOUNTS = {"ai-asset", "ai-exec", "ai-vigil", "ai-ctl", "ai-op"}

# P3b S3 · LAN Edge 服务账户名(低权、区别于机主)。provisioning 前为 None(该分支不激活)。
# 它只是纵深防御的一层:真正封顶 LAN_DEVICE 的是「带指纹头 → 查成员表」(见 resolve_lan_principal)。
LAN_EDGE_ACCOUNT = None

REGISTRY_PATH = Path(__file__).with_name("registry.toml")
PATHS_TOML = Path(__file__).resolve().parents[2] / "config" / "paths.toml"
CALLER_ACCOUNTS_PATH = Path(__file__).resolve().parents[2] / "config" / "caller-accounts.toml"


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


def _short_echo(s, limit: int = 64) -> str:
    """把调用方自报的值回显给它自己之前,先截断 + 剔控制字符。

    ★ 与配对规格对 `displayName` 的处置同一条纪律(服务端截断 64 字 + 剔控制字符):
      自报值只作显示,且**不得成为一条无界回显通道** —— 无界回显会让错误响应变成
      「把任意内容塞进一个将要发出的载荷」的入口。今天回给的是它自己,
      但**将来接上出境 sink 时,这条路径会跟着一起出境**。
    """
    s = "" if s is None else str(s)
    s = "".join(ch for ch in s if ch.isprintable())
    return s[:limit]


def log_upstream_problem(alias: str, backend: str, kind: str, detail: str) -> None:
    """上游异常的诊断落**服务端日志**,不回给调用方。

    ★★ 2026-08-04 加。此前 502 分支把 `r.text[:500]`(**上游原始字节**)、
      503 分支把**后端 URL** 直接放进回给调用方的 JSON 里。两处都不是"给用户看的错误",
      而是**内部拓扑与上游响应内容的披露**:
        · 别名全表(404 分支曾把 REGISTRY 里所有 chat 别名列出来)
        · 后端地址(`http://127.0.0.1:18081` 之类)
        · 上游返回的原文(可能含堆栈、配置片段、甚至半截生成内容)
      而 D30「降档不断连」有意让 `unregistered-local` 账户仍能走 chat ——
      2026-08-03 实测机器上就有两个未登记的外部 AI 沙箱账户。**对它们,这些是侦察材料。**

    ⇒ 判据:**错误响应不得携带调用方本来无权获得的内容。** 诊断信息不是删掉,是**换地方**:
      落到 `{state}/logs`(强 ACL,已加固)。排查照样查得到,调用方拿不到。
    """
    rec = {
        "ts": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "alias": alias,
        "backend": backend,
        "kind": kind,          # bad_upstream_response | upstream_error | backend_unavailable
        "detail": (detail or "")[:2000],
    }
    try:
        d = _logs_dir()
        d.mkdir(parents=True, exist_ok=True)
        with open(d / "upstream_problem.jsonl", "a", encoding="utf-8") as f:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")
    except Exception:
        pass  # 诊断落盘失败不能反过来把请求搞挂


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


# ★ Agent 身份的封闭表(D69)。agent_allow 只能从这里取值。
#   新增一个 Agent 必须先加进这里 —— 否则 registry 里写它就拒绝启动。
KNOWN_AGENTS = frozenset({
    "assistant.main",    # 用户在客户端里对话的日常助手
    "asset-director",    # 资产导演(§6.6 独立身份)
    "memory-service",    # 记忆平面自身的检索/嵌入调用
    "vigil",             # 常驻清醒助手(§17)
    "pet",               # 桌面宠物(P8 · Vigil 的身体)
})

# ★ 「始终本地」的两个 Agent(D69)。它们能用的别名集合被反向断言钉死。
RESIDENT_AGENTS = frozenset({"vigil", "pet"})

# ★ 它们唯一被允许使用的别名。改这个常量 = 放开「Vigil 始终本地」,必须走决议。
RESIDENT_ALIAS = "assistant.resident"


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

    _check_local_only(aliases)
    return aliases


def _check_local_only(aliases: dict) -> None:
    """D69「Vigil / 宠物始终本地」的启动期强制 —— 六条,全部 fail-closed。

    ★ 先把这条说清楚:**真正的强制点不在这里,在传输。** Vigil 与宠物不经本网关,
      连专用命名管道到一个只加载 assistant.resident、不链接任何 HTTP 客户端的后端;
      LOCAL_DENY_ACCOUNTS 含 ai-vigil,使它结构上连不上网关。Agent 身份由端点证明。

      本函数是**纵深防御 + 人类可审的声明**,不是唯一防线。之所以还要写:
      registry 是「Vigil 用哪个模型」这件事的唯一声明位置,而这个位置此前
      **没有任何约束** —— 今天把 assistant.fast 的 backend 改成一个云端地址,
      或者给 Vigil 新配一个指向 escalate.cloud 的别名,不会有任何东西报错。

    ★★ 第 6 条(反向全表断言)是本函数的重点,也是唯一防住「将来忘了写」的那条:
       正向断言只管 assistant.resident 一条,而事故的形状永远是**新增的那条忘了管**。
       这与 provenance denylist / E1 override / unseal caller 是同一族缺陷。
    """
    # ① 两个新字段必填(缺字段拒绝启动并点名)
    for field, typ, tname in (("local_only", bool, "布尔值"), ("agent_allow", list, "数组")):
        missing = sorted(n for n, a in aliases.items() if field not in a)
        if missing:
            raise RegistryError(
                f"别名缺少必填的 {field} 字段,拒绝启动:{missing}。\n"
                f"  见 D69 与 registry.toml 头部说明。这里不设默认值 —— \n"
                f"  缺 {field} 默认为宽松值正是 denylist 形状,新增的别名会默认自由。")
        wrong = sorted(n for n, a in aliases.items() if not isinstance(a[field], typ))
        if wrong:
            raise RegistryError(f"{field} 必须是{tname},拒绝启动:{wrong}")

    # ② agent_allow 的取值必须来自封闭表,且不许通配、不许空
    for name in sorted(aliases):
        allow = aliases[name]["agent_allow"]
        if not allow:
            raise RegistryError(
                f"agent_allow 不得为空数组,拒绝启动:{name}。\n"
                "  空数组是「谁也不能用」,那就该删掉这个别名而不是留一条死条目。")
        if "*" in allow:
            raise RegistryError(
                f"agent_allow 不得使用通配 \"*\",拒绝启动:{name}。\n"
                "  通配是 denylist 形状 —— 将来新增一个 Agent 时它默认落在【放行】一侧。")
        unknown = sorted(set(allow) - KNOWN_AGENTS)
        if unknown:
            raise RegistryError(
                f"agent_allow 含未登记的 Agent,拒绝启动:{name} → {unknown}。\n"
                f"  已登记的是 {sorted(KNOWN_AGENTS)};新增 Agent 必须先进 KNOWN_AGENTS。")

    # ③ local_only 与 egress 互斥
    both = sorted(n for n, a in aliases.items() if a["local_only"] and a["egress"])
    if both:
        raise RegistryError(
            f"local_only=true 与 egress=true 互斥,拒绝启动:{both}。\n"
            "  「永远本地」和「在你控制之外」不可能同时为真。")

    # ④ 常驻别名必须存在
    if RESIDENT_ALIAS not in aliases:
        raise RegistryError(
            f"缺少常驻别名 {RESIDENT_ALIAS},拒绝启动。\n"
            "  Vigil 与宠物必须有一个被钉死的本地别名(D69);没有它,\n"
            "  「始终本地」就没有可断言的标的。")

    # ⑤ 常驻别名的三个性质
    res = aliases[RESIDENT_ALIAS]
    if res["egress"] or not res["local_only"]:
        raise RegistryError(
            f"{RESIDENT_ALIAS} 必须 egress=false 且 local_only=true,拒绝启动"
            f"(实测 egress={res['egress']} local_only={res['local_only']})。")
    if "provider" in res:
        raise RegistryError(
            f"{RESIDENT_ALIAS} 不得有 provider 字段,拒绝启动。\n"
            "  provider 意味着「由外部服务商承接」,与「始终本地」直接冲突。")

    # ⑥ ★ 反向全表断言:凡允许 vigil / pet 的别名,有且只有 RESIDENT_ALIAS 一条。
    #    ——「将来给 Vigil 加了新别名忘了写」是这一族事故的真实形状,正向断言管不到。
    resident_capable = {n for n, a in aliases.items()
                        if RESIDENT_AGENTS & set(a["agent_allow"])}
    if resident_capable != {RESIDENT_ALIAS}:
        extra = sorted(resident_capable - {RESIDENT_ALIAS})
        raise RegistryError(
            f"反向全表断言失败,拒绝启动:允许 {sorted(RESIDENT_AGENTS)} 的别名"
            f"必须【有且只有】{RESIDENT_ALIAS},实测为 {sorted(resident_capable)}。\n"
            f"  多出来的:{extra}\n"
            "  「Vigil / 宠物始终本地」是对用户的承诺(D69)。要放开它,\n"
            "  必须显式删掉本断言并留下决议 —— 不能靠悄悄加一条别名绕过去。")


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


def load_caller_accounts() -> frozenset:
    """加载「谁配得上 trusted-local」的 allowlist。★ 缺文件/缺字段/空表一律**拒绝启动**。

    ★ 为什么必须是 allowlist,以及为什么必须 fail-closed:

      `trusted-local` 是 `E1_OVERRIDE_ALLOWED_TIERS` 的**唯一**成员,也是
      `tainted._ALLOWED_CALLERS["S2"]` 的**唯一**成员 —— 它就是本系统的最高档。
      原判据是「不在 LOCAL_DENY_ACCOUNTS 就给 trusted-local」(denylist + fail-open),
      等于**本机上任何一个没被点名拒绝的账户,连上来就是最高档**。

      2026-08-03 实测这条判据在本机上已经漏了:`CodexSandboxOffline` /
      `CodexSandboxOnline` 两个外部 AI 沙箱账户 Enabled、在 Users 组、不在拒绝表里。
      denylist 的失效方式永远是这个形状:**新增的东西默认自由**,而且不报错。

      fail-closed 到「拒绝启动」而不是「降级为空表」:空表意味着**谁都拿不到 E1 解除权**,
      那会表现为「按钮点了没反应」这种最难查的静默故障。宁可起不来,也不要半哑。
    """
    if not CALLER_ACCOUNTS_PATH.exists():
        raise RegistryError(
            f"缺少 {CALLER_ACCOUNTS_PATH.name},拒绝启动。\n"
            "  它是「谁配得上 trusted-local」的 allowlist(D30 修正)。\n"
            "  没有它就只能退回 denylist 判据 —— 而那条判据已被实测证明会漏。")
    with open(CALLER_ACCOUNTS_PATH, "rb") as f:
        data = tomllib.load(f)
    section = data.get("caller")
    if not isinstance(section, dict) or "trusted_local" not in section:
        raise RegistryError(
            f"{CALLER_ACCOUNTS_PATH.name} 缺少必填的 [caller].trusted_local,拒绝启动。")
    accounts = section["trusted_local"]
    if not isinstance(accounts, list) or not all(isinstance(a, str) for a in accounts):
        raise RegistryError("[caller].trusted_local 必须是字符串数组,拒绝启动。")
    if not accounts:
        raise RegistryError(
            "[caller].trusted_local 为空,拒绝启动。\n"
            "  空表 = 谁都拿不到 E1 解除权,表现为「按钮点了没反应」的静默故障。\n"
            "  要真的谁都不给,请显式写一条注释说明并让本条断言变红,不要留空表。")
    if "*" in accounts:
        raise RegistryError(
            "[caller].trusted_local 不得使用通配 \"*\",拒绝启动 —— 那就退回 denylist 了。")
    return frozenset(accounts)


REGISTRY = load_registry()
TRUSTED_LOCAL_ACCOUNTS = load_caller_accounts()
CHAT_KINDS = {"chat", "chat_multimodal"}

# ★ S3:关掉自动 API 文档(/docs · /redoc · /openapi.json)—— 安全网关不对外暴露接口清单;
#   同时使路由集合收敛为显式三条,ROUTE_TIERS 元测试可穷举。
app = FastAPI(title="LocalAI Hub Gateway", version="0.1.0-p3b",
              docs_url=None, redoc_url=None, openapi_url=None)
# ★ trust_env=False(2026-07-31 审计,高危):不让 HTTP_PROXY / HTTPS_PROXY / 系统代理
#   把本应走回环的转发改道到外网 —— 那会把整包 system + 全部历史(含解封后的记忆正文)
#   明文送到一个你不控制的端点。回环不需要代理,关掉它零损失。
_client = httpx.AsyncClient(timeout=httpx.Timeout(300.0, connect=5.0), trust_env=False)


# ────────────────────────────────────────────────────────────
# 认证(D28)+ 调用方 OS 身份(D30 混淆代理修正)
# 本机(loopback)→ 解析调用方账户(port→PID→WMI GetOwner)→ 隔离服务账户拒绝(见 LOCAL_DENY_ACCOUNTS);
# 远程 → 经 LAN Edge 的 mTLS 通道(D34 已作废 WebAuthn,D43 改走 mTLS),直连本口一律 401。
#
# ★★ 判据是 **allowlist**(D30 修正,2026-08-03):只有登记在 `config/caller-accounts.toml` 里的账户
#   才是 trusted-local;解析不到身份的、以及表外的一切账户,统统落 `unregistered-local`(降档不断连)。
#   ——— 原来的 fail-open 判据(「解析不到就当 trusted-local」)已被实测推翻:
#   两个未登记的外部 AI 沙箱账户(CodexSandboxOffline / Online)按它就能白拿最高档。
#   记录见 worklog 2026-08.md「回头补一」。**这段注释以前描述的正是那条被推翻的判据。**
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
    # ★★ D30 修正(2026-08-03):allowlist,不是 denylist。
    #   只有【显式登记在 config/caller-accounts.toml】的账户才配 trusted-local。
    #   解析不到身份的、以及登记表外的一切账户 → unregistered-local。
    if ident and ident[1].lower() in {a.lower() for a in TRUSTED_LOCAL_ACCOUNTS}:
        return "trusted-local"
    return "unregistered-local"               # ★ 降档不断连:chat 仍可用,但无 E1 解除权、无 S2 正文


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
    # ★★ D30 修正(2026-08-03):这里同样从 denylist 改为 allowlist。
    #   本函数是**记忆敏感路径**专用,却比 chat 路径更松是说不通的 ——
    #   原实现「非隔离账户即放行」会把 CodexSandbox* 这类未登记账户放进记忆路径。
    #   它今天还没有调用点,正因如此现在改代价最小:等有人接线再改就晚了。
    if ident[1].lower() not in {a.lower() for a in TRUSTED_LOCAL_ACCOUNTS}:
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
            content={"error": {"message": "远程访问须经 LAN Edge 的 mTLS 通道(D34/D43);本机请走 loopback",
                               "type": "unauthenticated", "code": "mtls_required"}},
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
        # ★ 不再枚举别名全表:那等于把 REGISTRY 的 chat 面摊给任何能走到这里的调用方
        #   (含 D30 有意保留连接的 unregistered-local)。要列表就走 /v1/models —— 它是认证过的。
        return JSONResponse(
            status_code=404,
            content={"error": {"message": f"未知别名 '{_short_echo(alias)}'。可用别名见 /v1/models。",
                               "type": "unknown_alias"}},
        )
    # ---- E4 出境载荷强制点(§5.6.2 · 五强制点 E4 · 在 kind 路由分类【之前】)----
    #   出境后端(egress=true)= 内容将离开受控设备集,不可逆。故:
    #     · 放在 kind 检查【之前】—— 安全强制点不被路由分类短路。
    #     · 【不认 E1 override】—— override 只让内容进本地(可逆);出境没有放行按钮。
    #     · 【与来源无关】—— 扫整个将发出的载荷(system/历史/assistant 都算,同 _scannable_text)。
    #   当前 chat 路由里唯一的 egress=true 别名(escalate.cloud=chat_cloud)会在其后的 kind
    #   检查处 400;E4 在此先行,是为它/未来云端别名接入时【闸已在位】(P3d 硬前置)。
    if entry.get("egress"):
        e4r = e4.scan(_scannable_text(body.get("messages")))
        if e4r.blocked:
            log_gate_rejection(session_id, e4r.categories, "egress_blocked")
            msg = e4.block_message(e4r.categories)
            hdrs = {"X-LocalAI-E4": "egress-blocked",
                    "X-LocalAI-E4-Categories": ",".join(sorted(e4r.categories))}
            # 同 E1:按客户端要的形态回(Open WebUI 默认 stream:true)
            if bool(body.get("stream", False)):
                def sse_e4():
                    base = {"id": "e4-block", "object": "chat.completion.chunk",
                            "created": int(time.time()), "model": f"{alias}(e4-egress-blocked)"}
                    first = dict(base, choices=[{"index": 0, "finish_reason": None,
                                                 "delta": {"role": "assistant", "content": msg}}])
                    last = dict(base, choices=[{"index": 0, "finish_reason": "content_filter",
                                                "delta": {}}])
                    yield f"data: {json.dumps(first, ensure_ascii=False)}\n\n"
                    yield f"data: {json.dumps(last, ensure_ascii=False)}\n\n"
                    yield "data: [DONE]\n\n"
                return StreamingResponse(sse_e4(), media_type="text/event-stream", headers=hdrs)
            return JSONResponse(
                status_code=200,
                headers=hdrs,
                content={
                    "id": "e4-block", "object": "chat.completion",
                    "created": int(time.time()), "model": f"{alias}(e4-egress-blocked)",
                    "choices": [{
                        "index": 0, "finish_reason": "content_filter",
                        "message": {"role": "assistant", "content": msg},
                    }],
                    "usage": {"prompt_tokens": 0, "completion_tokens": 0, "total_tokens": 0},
                    "x_localai_e4": {"egress_blocked": True, "categories": sorted(e4r.categories)},
                },
            )

    if entry["kind"] not in CHAT_KINDS:
        return JSONResponse(
            status_code=400,
            content={"error": {"message": f"别名 '{_short_echo(alias)}' 是 {entry['kind']},不走 chat 路由",
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
                # ★ 上游原文与后端地址【落服务端日志】,不回给调用方(见 log_upstream_problem)。
                log_upstream_problem(alias, backend, "upstream_error",
                                     raw.decode("utf-8", "replace"))
                return JSONResponse(
                    status_code=r.status_code, headers=hdrs,
                    content={"error": {"message": f"后端返回 {r.status_code}",
                                       "type": "backend_error",
                                       "alias": alias,
                                       "hint": "详细诊断已记入主机日志 upstream_problem.jsonl"}},
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
                # ★ 原先这里回 `detail: r.text[:500]` —— 上游【原始字节】直接转给调用方。
                #   换成非内容型诊断(状态码/类型/长度),原文落服务端日志。
                log_upstream_problem(alias, backend, "bad_upstream_response", r.text)
                return JSONResponse(
                    status_code=502, headers=hdrs,
                    content={"error": {"message": "后端返回的不是合法 JSON",
                                       "type": "bad_upstream_response",
                                       "alias": alias,
                                       "upstream_status": r.status_code,
                                       "upstream_content_type": r.headers.get("content-type", ""),
                                       "upstream_bytes": len(r.content),
                                       "hint": "原文已记入主机日志 upstream_problem.jsonl"}},
                )
            # 契约回写(§8.1.4):响应 model 字段回写真实契约
            if isinstance(data, dict):
                data["model"] = f"{alias}({contract})"
            return JSONResponse(content=data, status_code=r.status_code, headers=hdrs)
    except httpx.RequestError as e:
        # ★ 用 RequestError 而非 ConnectError:ConnectTimeout / ReadTimeout /
        #   RemoteProtocolError 等都【不是】ConnectError 的子类,原来会裸奔成 500。
        #   §8.1.4:503 带缺口,不静默降级。
        # ★ 后端 URL 与 fallback 是内部拓扑,落服务端日志;回给调用方的只有别名与原因类型。
        #   §8.1.4「503 带缺口,不静默降级」仍然成立 —— 缺的是【哪个别名】,不是【哪个地址】。
        log_upstream_problem(alias, backend, "backend_unavailable",
                             f"{type(e).__name__}: {e}; fallback={entry.get('fallback')}")
        return JSONResponse(
            status_code=503, headers=hdrs,
            content={"error": {"message": f"别名 '{_short_echo(alias)}' 的后端未响应"
                                          f"(无 Broker 期需先静态启动该后端)",
                               "type": "backend_unavailable",
                               "reason": type(e).__name__,
                               "alias": alias,
                               "hint": "后端地址与 fallback 已记入主机日志 upstream_problem.jsonl"}},
        )
