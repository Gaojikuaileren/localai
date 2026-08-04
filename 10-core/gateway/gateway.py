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
import asyncio          # P4-S13:同步面的 SSE 自己等事件(GPU 面那条是 Broker 在等)
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
import gpu_broker
import gpu_policy
import sync_policy
import sync_store
import system_prompt
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
# P4-S2:显存组件表 —— 词表桥要逐字校验别名引用的组件 id 真实存在。
VRAM_BUDGET_PATH = Path(__file__).resolve().parents[2] / "config" / "vram-budget.toml"


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
    _check_component_bridge(aliases)
    return aliases


def _check_component_bridge(aliases: dict) -> None:
    """P4-S2 · 词表桥的启动期强制 —— 五条,全部 fail-closed。

    ★★ 为什么要有这座桥:网关按**别名**路由(`assistant.fast`),显存闸按**组件 id**
      记账(`llm.assistant.8b@16k`)。2026-08-04 实测两套词表**零交集** ——
      网关源码里连 `component` 这个概念都没有。没有桥,Broker 就无法回答
      「要服务这个别名,得让哪个组件驻留」,而那是 P4 的全部前提。

    ★★ 必须是【声明】不是【推导】:`contract` 列今天混着四套后缀约定,
      按 "llm.assistant." + contract 拼组件 id,5 个聊天别名里**只有 2 个**拼得对,
      其余得到根本不存在的 id。推导会静默给出错的答案。

    ★★ 零显存别名必须**显式**写 `components_any_of = []` + `no_gpu_reason` ——
      省略即"默认不需要显存"是 fail-open,与 egress 那条论证逐字同源。
    """
    # ① 必填(缺字段拒绝启动并点名)
    missing = sorted(n for n, a in aliases.items() if "components_any_of" not in a)
    if missing:
        raise RegistryError(
            f"别名缺少必填的 components_any_of,拒绝启动:{missing}。\n"
            "  每个别名都必须显式声明「服务它需要哪个组件驻留」(P4-S2)。\n"
            "  不占显存的别名写 components_any_of = [] 并补 no_gpu_reason ——\n"
            "  省略即『默认不需要显存』是 fail-open:将来新增一个吃显存的别名忘了写,\n"
            "  它会被当成不占显存,而闸根本不知道要拦它。")

    # ② 类型
    bad = sorted(n for n, a in aliases.items() if not isinstance(a["components_any_of"], list))
    if bad:
        raise RegistryError(f"components_any_of 必须是数组,拒绝启动:{bad}")

    # ③ 空表必须给理由 —— 「不占显存」是一个需要被说出口的判断,不是默认值
    silent = sorted(n for n, a in aliases.items()
                    if not a["components_any_of"] and not str(a.get("no_gpu_reason", "")).strip())
    if silent:
        raise RegistryError(
            f"别名声明了不占显存却没写 no_gpu_reason,拒绝启动:{silent}。\n"
            "  空表是一个**判断**(这个别名真的不需要 GPU),必须留下依据供人复核。")

    # ④ 引用的组件必须真实存在于 config/vram-budget.toml —— 拼错一个字就是一条静默死路
    try:
        with open(VRAM_BUDGET_PATH, "rb") as f:
            known = set(tomllib.load(f).get("components", {}))
    except Exception as e:                                   # noqa: BLE001
        raise RegistryError(
            f"读不到显存组件表 {VRAM_BUDGET_PATH}({type(e).__name__}: {e}),拒绝启动。\n"
            "  ★ 读不到 ≠ 没有组件:那会让所有别名的组件引用都'碰巧'通过检查。")
    unknown = sorted({(n, c) for n, a in aliases.items()
                      for c in a["components_any_of"] if c not in known})
    if unknown:
        raise RegistryError(
            f"别名引用了不存在的组件 id,拒绝启动:{unknown}。\n"
            f"  已登记组件:{sorted(known)}\n"
            "  ★ 组件 id 必须逐字匹配 —— 拼错一个字,Broker 会以为这个别名不需要任何组件。")

    # ⑤ ★★ 反向全表:每个组件要么被某个别名覆盖,要么显式登记在 [unaliased] 里并写明理由。
    #   正向检查只管"别名引用的组件存不存在";而事故的形状是**新增的组件没人用却在参与算术**。
    try:
        with open(REGISTRY_PATH, "rb") as f:
            unaliased = tomllib.load(f).get("unaliased", {})
    except Exception:                                        # noqa: BLE001
        unaliased = {}
    covered = {c for a in aliases.values() for c in a["components_any_of"]}
    orphan = sorted(known - covered - set(unaliased))
    if orphan:
        raise RegistryError(
            f"这些组件没有任何别名驱动,也没登记进 [unaliased],拒绝启动:{orphan}。\n"
            "  ★ 一个『谁都不用』的组件要么是欠账、要么是该删 ——\n"
            "  不该无声地躺在预算表里参与算术。要保留就在 registry.toml 的\n"
            "  [unaliased] 里写明理由(那句理由就是将来复核它的依据)。")
    stale = sorted(set(unaliased) - known)
    if stale:
        raise RegistryError(
            f"[unaliased] 里登记了不存在的组件,拒绝启动:{stale}。\n"
            "  组件已从 vram-budget.toml 删掉,这条登记也该跟着删 —— 留着会掩盖下一次真的孤儿。")


def components_for_alias(alias: str) -> list:
    """别名 → 它可接受的组件集合(any_of)。★ 未知别名返回空表**并非**"不需要显存",
    调用方必须先确认别名存在;这里不替它决定。"""
    a = REGISTRY.get(alias)
    return list(a["components_any_of"]) if a else []


def aliases_for_component(component_id: str) -> list:
    """组件 → 哪些别名会用到它。★ **由代码导出**,不在 registry.toml 里再声明一遍 ——
    一个方向声明、另一个方向导出,两者就不可能打架。"""
    return sorted(n for n, a in REGISTRY.items()
                  if component_id in a.get("components_any_of", []))


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


# ══════════════════════════════════════════════════════════════════════
#  P4-S10 · GPU 面的主体解析 + 六元组判定(**唯一**入口)
#
#  ★★ 此前 GPU 面每个端点只有一行 `classify_caller(request) == "remote-unauthenticated"`,
#     于是 `denied-account`(§6.8 明文「绝不放行」)与 `trusted-local` 权限完全相同 ——
#     实跑确认过。根因是 `chat_completions` 里那套主体解析(denied-account 403 +
#     证书指纹封顶 lan-device)**只长在那一条路径上**,GPU 面各写各的。
#  ⇒ 抽成一个函数,GPU 面全部走它;并断言 GPU 面**不得再有散落的 classify_caller 比较**。
#     这就是方案书「权限按档位**挂载**而非运行时判断」在 HTTP 面上的等价形态。
# ══════════════════════════════════════════════════════════════════════

def gpu_principal(request: Request) -> str:
    """解出这次请求在 GPU 面上的**有效档位**。

    ★ 与 chat 那条路径同一套判据,顺序也一样:
      ① denied-account 直接封死(§6.8)· ② 带证书指纹 → 成员表反查 → **封顶** lan-device ·
      ③ 否则就是 classify_caller 的档位。
    ★ 指纹解析不出成员 → 返回 `remote-unauthenticated`(fail-closed),
      不退回 caller 档 —— 退回会让一个伪造指纹的本机进程拿到比 lan-device 更多的权限。
    """
    caller = classify_caller(request)
    if caller == "denied-account":
        return "denied-account"
    fp = request.headers.get("x-localai-cert-sha256", "")
    if fp:
        return "lan-device" if resolve_lan_principal(fp) is not None else "remote-unauthenticated"
    return caller


def gpu_guard(request: Request, action: str, *, components=None, lease_kind: str = "",
              ttl_s=None, holder: str = "", count_quota: bool = True):
    """判一次。通过返回 (tier, None);不通过返回 (tier, JSONResponse)。

    ★ 拒绝响应里带 `dimension` —— 六元组的哪一维拦的必须点名。
      合并成一句「权限不足」会让人去改错的东西:撞额度的人会去申请提权,
      而他其实只要等一分钟。(与 §8.1「两种撞墙必须分开说」同一条纪律。)
    """
    tier = gpu_principal(request)
    d = gpu_policy.check(tier, action, components=components, lease_kind=lease_kind,
                         ttl_s=ttl_s, holder=holder, count_quota=count_quota)
    if d.ok:
        return tier, None
    # 401 = 你是谁没确定;403 = 知道你是谁,但不给;429 = 太快了
    status = 401 if tier == "remote-unauthenticated" else (429 if d.code == "denied_quota" else 403)
    return tier, JSONResponse(
        status_code=status,
        content={"error": {"message": d.message, "type": d.code,
                           # ★ 点名是哪一维 —— 这决定用户下一步该做什么
                           "dimension": d.dimension, "tier": tier, "action": action},
                 "detail": d.detail},
    )


# ★ P3b S3:每条路由必须显式归类;新增未归类路由 → unclassified_routes() 非空 → 元测试失败(§S3)。
ROUTE_TIERS = {
    ("GET", "/health"): "public-minimal",
    ("GET", "/v1/models"): "authenticated",
    ("POST", "/v1/chat/completions"): "authenticated",
    # P4-S3:GPU Broker 的**只读**快照。
    ("GET", "/v1/gpu/snapshot"): "authenticated",
    # P4-S5:推送流(SSE)。D37 ②「推送非轮询」—— 客户端不再定时问,由中枢主动发。
    ("GET", "/v1/gpu/events"): "authenticated",
    # P4-S5:★ GPU 面的**第一个变更端点**。S3 曾断言"GPU 面只能有 GET",
    #   那条断言现在被**有意地**改成一张显式方法表(见 test_gpu_broker.py 第 1 组)——
    #   这是一次语义变更,应当在 diff 里看得见,而不是把断言删掉了事。
    ("POST", "/v1/gpu/lease"): "authenticated",
    # P4-S4b:客户端退出时通知结束会话 —— ★ 这条路由**此前不存在**,
    #   而客户端 HubClient.cs:230 每次退出都在调它、失败还被吞掉:
    #   一次伪装成成功的静默失败。现在它真的存在了。
    ("POST", "/v1/session/end"): "authenticated",
    # P4-S9:组件目录 —— 挑选面板的数据源。
    #   ★ 它必须存在,否则客户端只能自己编一份清单;而客户端**已经编过一份**
    #     (`Views/ModelCatalog.cs` 的 chat.8b / speech / image),那是**第三套词汇**:
    #     跟网关别名对不上,跟显存组件 id 也对不上,谁也映射不到谁。
    ("GET", "/v1/gpu/components"): "authenticated",
    # P4-S9:★ 「点确定」= 一次事务(S8 的 apply_intended 的对外落点)。
    #   与 lease 同款:if_generation **必填**,对不上 409 + 最新快照。
    ("POST", "/v1/gpu/intended"): "authenticated",
    # P4-S13(D86):内网同步 —— 家庭待办 + 共享会话。
    #   ★ 这是 D52「真正的上传/同步要等中枢接入(P4+)」那半边的落点。
    ("GET", "/v1/sync/snapshot"): "authenticated",
    ("POST", "/v1/sync/push"): "authenticated",
    ("GET", "/v1/sync/events"): "authenticated",
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


@app.on_event("startup")
async def _start_gpu_broker():
    """P4-S3:起 1 Hz 采样器。★ 起不来【不静默】——失败会写进快照的 sampler_error,
    而不是让端点看起来正常却永远返回同一个旧值。"""
    try:
        gpu_broker.BROKER.start()
    except Exception:                                        # noqa: BLE001
        pass   # 采样器起不来不该拖垮网关启动;快照会以 stale=True + sampler_error 如实呈现
    try:
        # P4-S9:结束 STARTING。★ 不加这一步的话 Broker **永远停在 STARTING**,
        #   而 apply_intended 只接受 READY ⇒ 整条事务路径从来走不到(实测 409 busy)。
        #   放行条件就是 I2 的后件(actual == committed),见 finish_startup 的说明。
        await gpu_broker.BROKER.finish_startup()
    except Exception:                                        # noqa: BLE001
        pass   # 同上:留在 STARTING 比谎称 READY 安全,快照里看得见


@app.get("/v1/gpu/snapshot")
async def gpu_snapshot(request: Request):
    """P4-S3 · GPU 状态的**只读副本**(D37「单一权威 + 副本」的副本那一半)。

    ★ 本片没有任何变更端点 —— 预留 / 装载 / 租约都属于 S4。
    ★ 快照带 generation:客户端将来提交变更时要回传它,对不上即 409 + 回带最新快照。
    ★ 快照带 stale / sampler_error:采样器死了必须**看得见**,
      而不是让调用方拿着一个永远不变的数字以为它是新的。
    """
    _tier, _deny = gpu_guard(request, "read")
    if _deny is not None:
        return _deny
    try:
        snap = gpu_broker.BROKER.snapshot()
    except Exception as e:                                   # noqa: BLE001
        # ★ 与显存闸 CLI 的三态同一条纪律:「读不出来」不能伪装成「一切正常」。
        return JSONResponse(
            status_code=503,
            content={"error": {"message": "GPU 快照不可用(Broker 未就绪或显存配置读不到)",
                               "type": "broker_unavailable",
                               "reason": type(e).__name__}},
        )
    return snap.to_json()


@app.get("/v1/gpu/events")
async def gpu_events(request: Request):
    """P4-S5 · 推送流(SSE)。D37 ②:**推送非轮询**。

    ★ 连上先给**全量**快照(重连即对齐,不必先问一次);之后每次世代号变化再发一帧。
    ★ 每帧都盖 `generation` —— 客户端据此判断自己手上那份是不是最新的。
    ★ 心跳:15 秒没变化就发一行注释帧。不发心跳的话,一条静默的长连接
      与一条**死掉**的长连接在客户端看来一模一样 —— 又是"失败与成功长得一样"。

    ★ 关于"增量":本快照只有几百字节,**每帧仍发全量**并如实标 `event: update`。
      不做字段级 diff 是有意的:diff/apply 两侧不一致是一整类难查的 bug,
      而这里省下的带宽可以忽略。不假装做了增量。
    """
    _tier, _deny = gpu_guard(request, "read")
    if _deny is not None:
        return _deny

    async def gen():
        try:
            snap = gpu_broker.BROKER.snapshot()
            yield ("event: snapshot\ndata: "
                   + json.dumps(snap.to_json(), ensure_ascii=False) + "\n\n")
            last = snap.generation
            while True:
                if await request.is_disconnected():
                    break
                changed = await gpu_broker.BROKER.wait_for_change(last, timeout=15.0)
                if await request.is_disconnected():
                    break
                if changed:
                    snap = gpu_broker.BROKER.snapshot()
                    last = snap.generation
                    yield ("event: update\ndata: "
                           + json.dumps(snap.to_json(), ensure_ascii=False) + "\n\n")
                else:
                    # ★ 心跳带上世代号:客户端可据此发现自己错过了一帧(不该发生,但能被发现)
                    yield f": heartbeat gen={last}\n\n"
        except Exception as e:                               # noqa: BLE001
            # ★ 推送流崩了要**说出来**,不能静默断开 —— 客户端会把静默断开当成"没有变化"。
            yield ("event: error\ndata: "
                   + json.dumps({"type": type(e).__name__, "message": str(e)[:200]},
                                ensure_ascii=False) + "\n\n")

    return StreamingResponse(gen(), media_type="text/event-stream",
                             headers={"Cache-Control": "no-cache",
                                      "X-Accel-Buffering": "no"})


@app.post("/v1/gpu/lease")
async def gpu_lease(request: Request):
    """P4-S5 · 申请租约。D37 ③:**世代号对不上即拒,并回带最新状态**。

    ★★ `if_generation` 是**必填**的。省略它不等于"我不在乎" ——
      那正是 fail-open:一个没读过快照的客户端会盲目申请,而世代号存在的全部理由
      就是让"你看到的"与"现在的"能被比对。缺字段 → 400,不是默认放行。

    ★ 对不上时回 **409 + 最新快照**(不是裸 409):D37 明写「对不上即拒**并回最新状态**」。
      只回 409 会让客户端必须再发一次请求才知道现在是什么样 —— 那就又变成轮询了。
    """
    # ★ 先判档位与动作(不计额度);参数维要等 kind / ttl 解析出来才判得了,
    #   所以在下面第二次调用里连参数一起判 —— count_quota 只在那一次为真,不重复扣。
    _tier, _deny = gpu_guard(request, "lease", count_quota=False)
    if _deny is not None:
        return _deny
    try:
        body = await request.json()
    except Exception:                                        # noqa: BLE001
        body = {}

    if "if_generation" not in body:
        return JSONResponse(
            status_code=400,
            content={"error": {"message": "缺少必填的 if_generation ——"
                                          "必须声明你是基于哪一版状态做的这次申请。",
                               "type": "missing_if_generation",
                               "hint": "先取 /v1/gpu/snapshot 或订阅 /v1/gpu/events"}},
        )
    try:
        want_gen = int(body["if_generation"])
    except Exception:                                        # noqa: BLE001
        return JSONResponse(status_code=400,
                            content={"error": {"message": "if_generation 必须是整数",
                                               "type": "bad_if_generation"}})

    kind = str(body.get("kind") or "")
    holder = _short_echo(body.get("holder"))
    components = [str(c) for c in (body.get("components") or [])][:16]
    ttl = float(body.get("ttl_s") or gpu_broker.DEFAULT_TTL_S)

    # ★ 参数维 + 额度维:kind / ttl / 组件数解析出来之后才判得了。
    #   ttl 不封顶 = 一份**永不过期**的租约,而租约的全部意义就是会过期。
    _tier2, _deny2 = gpu_guard(request, "lease", components=components,
                               lease_kind=kind, ttl_s=ttl, holder=holder)
    if _deny2 is not None:
        return _deny2

    try:
        snap = gpu_broker.BROKER.snapshot()
        if snap.generation != want_gen:
            # ★ 409 + 最新快照 —— 让客户端一次就拿到重试所需的一切
            return JSONResponse(
                status_code=409,
                content={"error": {"message": f"世代号对不上:你基于 {want_gen},当前 {snap.generation}",
                                   "type": "generation_conflict"},
                         "snapshot": snap.to_json()},
            )
        status, lease = await gpu_broker.BROKER.grant(kind, holder, components, ttl)
    except Exception as e:                                   # noqa: BLE001
        return JSONResponse(status_code=503,
                            content={"error": {"message": "Broker 不可用",
                                               "type": "broker_unavailable",
                                               "reason": type(e).__name__}})

    if status != gpu_broker.LEASE_OK:
        # ★ 被拒也回带最新快照:拒绝信息含【占用者】(P4-4)靠的就是它里面的 leases。
        return JSONResponse(
            status_code=409,
            content={"error": {"message": f"租约未发放:{status}", "type": status.lower()},
                     "snapshot": gpu_broker.BROKER.snapshot().to_json()},
        )
    return {"status": "ok", "lease": lease.to_json(),
            "fence_token": lease.fence_token,
            "generation": gpu_broker.BROKER.snapshot().generation}


@app.post("/v1/session/end")
async def session_end(request: Request):
    """P4-S4b:客户端退出时结束会话,释放它持有的租约。

    ★★ 这条路由**在 2026-08-04 之前根本不存在**,而客户端每次退出都在调它
      (`HubClient.cs:230`,注释写着「结束会话通知失败(不影响退出)」并吞掉异常)。
      于是每次关闭都在发一个永远不可能成功的请求 —— **一次伪装成成功的静默失败**,
      正是本项目纪律明令禁止的形状。它同时也是当天「关闭卡一段时间」的一部分成因。

    ★ 语义是**尽力而为但如实回话**:没有活跃租约不是错误(客户端可能从没申请过),
      但要在响应里说清"释放了几条",而不是一律回 200 空体让调用方无从分辨。
    """
    # ★ 只释放**自己持有**的租约,不改驻留集合 ⇒ 归 read 档,不占变更额度。
    #   否则一次正常退出会吃掉用户的变更配额,而退出是客户端每次都做的事。
    _tier, _deny = gpu_guard(request, "read")
    if _deny is not None:
        return _deny
    try:
        body = await request.json()
    except Exception:                                        # noqa: BLE001
        body = {}
    device = str(body.get("device") or "")[:64]
    reason = str(body.get("reason") or "")[:64]

    released = 0
    try:
        for l in await gpu_broker.BROKER.active_leases():
            if l.holder == device:
                if await gpu_broker.BROKER.release(l.lease_id, l.fence_token) == gpu_broker.LEASE_OK:
                    released += 1
    except Exception as e:                                   # noqa: BLE001
        return JSONResponse(
            status_code=503,
            content={"error": {"message": "会话结束时释放租约失败", "type": "broker_unavailable",
                               "reason": type(e).__name__}},
        )
    return {"status": "ok", "released_leases": released,
            "device": device, "reason": reason}


# ══════════════════════════════════════════════════════════════════════
#  P4-S9 · 组件挑选面板的服务端半边
#
#  ★★ 为什么必须有 /v1/gpu/components,而不是让客户端自己列:
#     客户端**已经自己列过**一份(`Views/ModelCatalog.cs`:chat.8b / chat.8b.long /
#     chat.30b / speech / vlm / image),那是【第三套词汇】——
#     跟网关别名(chat.default…)对不上,跟显存组件 id(llm.assistant.8b@16k…)也对不上。
#     它的注释自己写着「接入 GPU Broker(P4)后以中枢下发的清单为准替换这份占位」。
#     ⇒ 现在就是那个"接入后"。清单只能有一份权威,就是准入白名单本身。
# ══════════════════════════════════════════════════════════════════════



# ══════════════════════════════════════════════════════════════════════
#  P4-S13 · 内网同步(D86):家庭待办 + 共享会话
#
#  ★ D52 写着「★ 真正的上传/同步要等中枢接入(P4+)」—— 这里就是那半边。
#  ★ 触发它的是两条实测反馈:「副机提升为共享,主机看不见」「共享家庭待办对方看不到」。
#    查清后都不是 bug,是**从来没做过**。
#
#  ★★ 三条裁定(D86)在这一层的落点:
#    ① 只收「家庭/共享」的 —— 判据在 sync_store.in_scope,**服务端**说了算;
#    ② 实时:变更即推,订阅方走 SSE(与 GPU 面同一手法,D37 ②);
#    ③ 冲突后到的赢,**但被覆盖的那一版存起来** —— 响应里如实回报 superseded。
# ══════════════════════════════════════════════════════════════════════

# 同步面的变更通知。★ 与 Broker 的世代号同款:订阅者等事件,不轮询。
_sync_waiters: list = []


def _sync_notify() -> None:
    """★ 非阻塞地叫醒所有订阅者。写完就叫,不攒批 ——
    D86 裁定②要求"内容更新也要实时",攒批就不实时了。"""
    global _sync_waiters
    for w in _sync_waiters:
        w.set()
    _sync_waiters = []


def sync_guard(request: Request, action: str, *, batch: int = 0, holder: str = "",
               count_quota: bool = True):
    """同步面的档位判定。★ 复用 gpu_principal 解主体 —— 主体解析只有一套,
    否则又会出现「两条路径各写各的档位判断」(S10 那个洞的根因)。"""
    tier = gpu_principal(request)
    d = sync_policy.check(tier, action, batch=batch, holder=holder, count_quota=count_quota)
    if d.ok:
        return tier, None
    status = 401 if tier == "remote-unauthenticated" else (429 if d.code == "denied_quota" else 403)
    return tier, JSONResponse(
        status_code=status,
        content={"error": {"message": d.message, "type": d.code,
                           "dimension": d.dimension, "tier": tier, "action": action},
                 "detail": d.detail or {}},
    )


@app.get("/v1/sync/snapshot")
async def sync_snapshot(request: Request):
    """共享数据的全量/增量快照。`since_rev` 省略即全量(重连后拿它对齐)。"""
    _tier, _deny = sync_guard(request, "sync_read")
    if _deny is not None:
        return _deny
    try:
        since = int(request.query_params.get("since_rev", "0"))
    except Exception:                                        # noqa: BLE001
        since = 0
    try:
        return sync_store.store().snapshot(since_rev=since)
    except Exception as e:                                   # noqa: BLE001
        # ★ 存储坏了要**说出来**(sync_store 对坏档是抛而不是当空表 —— 当空会让
        #   下一次推送把全部共享数据整个覆盖掉)。
        return JSONResponse(
            status_code=503,
            content={"error": {"message": "共享数据不可用", "type": "sync_store_unavailable",
                               "reason": type(e).__name__, "detail": str(e)[:200]}},
        )


@app.post("/v1/sync/push")
async def sync_push(request: Request):
    """推一批变更。逐条判范围、逐条回结果。

    ★★ **逐条**回结果,不是一个总的 ok —— 一批里有的收了有的被拒(个人待办),
      合成一个布尔值会让客户端不知道哪条没上去,于是它要么全部重推、要么静默丢。
    """
    try:
        body = await request.json()
    except Exception:                                        # noqa: BLE001
        body = {}
    items = body.get("items")
    if not isinstance(items, list):
        # ★ 与 /v1/gpu/intended 同一条:缺字段不当成空 —— 那会把一次手滑变成一次空推。
        return JSONResponse(
            status_code=400,
            content={"error": {"message": "缺少 items 数组", "type": "missing_items"}})
    device = _short_echo(body.get("device")) or "unknown"
    _tier, _deny = sync_guard(request, "sync_write", batch=len(items), holder=device)
    if _deny is not None:
        return _deny

    results = []
    accepted = 0
    try:
        st = sync_store.store()
        # ★ 先收 sessions 再收 messages:messages 的范围判据要查它所属会话在不在共享里。
        #   顺序反了的话,同一批里"新共享的会话 + 它的消息"会因为会话还没进表而被拒。
        order = {"sessions": 0, "todos": 1, "messages": 2}
        for it in sorted(items, key=lambda x: order.get(str(x.get("kind")), 9)):
            kind = str(it.get("kind") or "")
            rec = it.get("record") or {}
            r = st.put(kind, rec, device)
            if r.get("ok"):
                accepted += 1
            results.append({"kind": kind, "id": rec.get("id"), **r})
    except Exception as e:                                   # noqa: BLE001
        return JSONResponse(
            status_code=503,
            content={"error": {"message": "写共享数据失败", "type": "sync_store_unavailable",
                               "reason": type(e).__name__, "detail": str(e)[:200]}})
    if accepted:
        _sync_notify()
    return {"accepted": accepted, "total": len(items), "results": results,
            "generation": sync_store.store().snapshot()["generation"]}


@app.get("/v1/sync/events")
async def sync_events(request: Request):
    """共享数据的推送流。★ D86 裁定②:内容更新要**实时**同步。

    ★ 与 GPU 面同款三条:连上先给全量(重连即对齐)· 每帧盖 generation ·
      15 秒心跳(**没有心跳的话,一条死掉的长连接与"一直没变化"长得一模一样**)。
    """
    _tier, _deny = sync_guard(request, "sync_read")
    if _deny is not None:
        return _deny

    async def gen():
        try:
            snap = sync_store.store().snapshot()
            yield "event: snapshot\ndata: " + json.dumps(snap, ensure_ascii=False) + "\n\n"
            last = snap["generation"]
            while True:
                if await request.is_disconnected():
                    break
                ev = asyncio.Event()
                cur = sync_store.store().snapshot(since_rev=last)
                if cur["generation"] != last:
                    last = cur["generation"]
                    yield "event: update\ndata: " + json.dumps(cur, ensure_ascii=False) + "\n\n"
                    continue
                _sync_waiters.append(ev)
                try:
                    await asyncio.wait_for(ev.wait(), timeout=15.0)
                except asyncio.TimeoutError:
                    if ev in _sync_waiters:
                        _sync_waiters.remove(ev)   # ★ 超时摘掉自己,否则无界增长
                    yield f": heartbeat gen={last}\n\n"
        except Exception as e:                               # noqa: BLE001
            # ★ 推送流崩了要说出来 —— 静默断开会被客户端当成"没有变化"。
            yield ("event: error\ndata: "
                   + json.dumps({"type": type(e).__name__, "detail": str(e)[:200]},
                                ensure_ascii=False) + "\n\n")

    return StreamingResponse(gen(), media_type="text/event-stream",
                             headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"})


@app.get("/v1/gpu/components")
async def gpu_components(request: Request):
    """组件目录 = **准入白名单本身**,不是它的一份摘抄。

    ★★ 反向全表:目录**必须逐条列出** `cfg.components` 的全部成员,一个不漏。
      漏掉一个的后果不是"界面少一项",是**用户看不见但闸仍然会算它** ——
      于是"我明明没勾它"和"闸说装不下"同时成立,而用户无从对上账。
      故这里不做任何过滤(不按 kind 筛、不按 peak 筛、不藏"装不下的")。
    ★ `display` 缺失时**回落到 id 本身**,不是跳过、也不是空字符串:
      一个没起名字的组件仍然占显存,它必须出现在面板上。
    """
    _tier, _deny = gpu_guard(request, "read")
    if _deny is not None:
        return _deny
    try:
        snap = gpu_broker.BROKER.snapshot()
        cfg = gpu_broker.BROKER.cfg
    except Exception as e:                                   # noqa: BLE001
        return JSONResponse(
            status_code=503,
            content={"error": {"message": "组件目录不可用(显存配置读不到)",
                               "type": "broker_unavailable", "reason": type(e).__name__}},
        )

    intended = set(snap.intended)
    committed = set(snap.committed)
    permitted = set(snap.permitted_on_demand)
    items = []
    for cid in sorted(cfg.components):
        meta = cfg.components[cid]
        items.append({
            "id": cid,
            # ★ 回落到 id:没起名字的组件也必须出现在面板上,不能因缺字段被吞掉
            "display": str(meta.get("display") or cid),
            "kind": str(meta.get("kind") or ""),
            "peak_gib": cfg.peak(cid),
            # ★ 这条是**测量出处**,不是宣传语。界面照抄,不改写、不省略。
            "note": str(meta.get("note") or ""),
            "intended": cid in intended,
            "committed": cid in committed,
            "permitted_on_demand": cid in permitted,
        })
    # ★ 别名映射一并回带:让面板能说清"勾掉它,哪些功能会停"。
    #   这层桥是 S2 建的;客户端**不得**自己再猜一遍 id 与功能的对应。
    try:
        aliases = {cid: sorted(aliases_for_component(cid)) for cid in cfg.components}
    except Exception:                                        # noqa: BLE001
        aliases = {}
    return {
        "generation": snap.generation,
        "components": items,
        "aliases_by_component": aliases,
        "budget": {
            "vram_budget": snap.vram_budget,
            "total_gib": snap.total_gib,
            "desktop_floor": snap.desktop_floor,
            "free_gib": snap.free_gib,
            # ★ 面板要能区分两种撞墙(§8.1:合并成「显存不足」是有害的):
            #   撞 vram_budget ⇒ 改桌面预留**有用**;撞实时 free ⇒ 改预留**没用**,得关程序。
            #   ★ 取自 cfg 而不是 getattr(snap, ..., None):快照上没有这个字段,
            #     getattr 的默认值会让它**静默变成 null**,而面板拿 null 算不出第二种撞墙。
            "safety_margin": cfg.budget.safety_margin,
        },
        "state": snap.state,
        "stale": snap.stale,
        "sampler_error": snap.sampler_error,
    }


@app.post("/v1/gpu/intended")
async def gpu_intended(request: Request):
    """「点确定」= 一次事务(S8 `apply_intended` 的对外落点)。

    ★★ `if_generation` **必填**,与 lease 同款理由:省略它不等于"我不在乎",
      那正是 fail-open。挑组件要花几十秒,期间桌面会变 ——
      「预览过、确定时不过」是**必然**会发生的,而世代号是唯一能让两边对上账的东西。

    ★★ 事务的每一种失败都有**自己的** code,不合并成一个"失败了":
      · gate_*            预检不过 —— **一个组件都没卸**,回编辑态
      · needs_user_choice 有任务在跑 —— 给了 5 秒排空窗口仍未空,交还用户裁定
      · loader_absent     装载器尚未实现(P5)—— 事务失败关闭,**不是**"装好了"
      · load_failed_rolled_back / rollback_failed
      合并成一个失败码会让客户端只能弹一句"失败",而这四种的下一步动作**完全不同**。
    """
    # ★ 同 lease:动作要看 components 才定得了(空数组 = 卸掉全部,是**另一个动作**),
    #   所以这里先只判档位,解析完再连动作与参数一起判。
    _tier, _deny = gpu_guard(request, "read", count_quota=False)
    if _deny is not None:
        return _deny
    try:
        body = await request.json()
    except Exception:                                        # noqa: BLE001
        body = {}

    if "if_generation" not in body:
        return JSONResponse(
            status_code=400,
            content={"error": {"message": "缺少必填的 if_generation ——"
                                          "必须声明你是基于哪一版状态做的这次变更。",
                               "type": "missing_if_generation",
                               "hint": "先取 /v1/gpu/snapshot 或订阅 /v1/gpu/events"}},
        )
    try:
        want_gen = int(body["if_generation"])
    except Exception:                                        # noqa: BLE001
        return JSONResponse(status_code=400,
                            content={"error": {"message": "if_generation 必须是整数",
                                               "type": "bad_if_generation"}})
    if not isinstance(body.get("components"), list):
        # ★ 缺字段不当成"空集合" —— 那会把一次手滑变成"把所有模型都卸掉"。
        return JSONResponse(
            status_code=400,
            content={"error": {"message": "缺少 components 数组。"
                                          "★ 省略不等于空集合 —— 空集合意味着卸掉全部,"
                                          "那必须是明确写出来的意图。",
                               "type": "missing_components"}})
    components = [str(c) for c in body["components"]][:32]
    interrupt = bool(body.get("interrupt_running") or False)

    # ★★★ 六元组的参数维在这里落地:`components == []` 与 `components == [x]`
    #   走同一个端点、同一段代码,HTTP 上**长得一模一样** —— 但前者的意思是**卸掉全部**。
    #   所以它被映射成【另一个动作】(unload_all),而不是同一个动作的一个取值。
    #   §6.2 原话:「同一个『写文件』工具,路径参数决定它是安全还是灾难」。
    _act = gpu_policy.resolve_action(components, is_change=True)
    _tier2, _deny2 = gpu_guard(request, _act, components=components,
                               holder=_short_echo(body.get("holder")))
    if _deny2 is not None:
        return _deny2

    try:
        snap = gpu_broker.BROKER.snapshot()
        if snap.generation != want_gen:
            return JSONResponse(
                status_code=409,
                content={"error": {"message": f"世代号对不上:你基于 {want_gen},当前 {snap.generation}",
                                   "type": "generation_conflict"},
                         "snapshot": snap.to_json()},
            )
        res = await gpu_broker.BROKER.apply_intended(components, interrupt_running=interrupt)
    except Exception as e:                                   # noqa: BLE001
        return JSONResponse(
            status_code=503,
            content={"error": {"message": "变更驻留集合时 Broker 出错",
                               "type": "broker_unavailable", "reason": type(e).__name__}},
        )

    after = gpu_broker.BROKER.snapshot()
    payload = {"result": res.to_json(), "snapshot": after.to_json()}
    if res.ok:
        return payload
    # ★ 事务没成 ⇒ **不得回 200**。回 200 再让客户端读 body 里的 ok 字段,
    #   等于把"失败"藏进一个看起来成功的响应里 —— 失败必须长得和成功不一样。
    #   409 = 状态冲突(有人在跑 / 忙),422 = 这次请求本身过不去(闸拒 / 装载器缺席)。
    code = 409 if res.code in ("busy", "needs_user_choice") else 422
    payload["error"] = {"message": res.message, "type": res.code}
    return JSONResponse(status_code=code, content=payload)


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
    # ══════════════════════════════════════════════════════════════════
    #  P4-S12:★★★ 注入**行为底线**提示词。
    #
    #  实机实测(2026-08-05,模型接入当晚第一轮):用户问「你记得我是谁吗」,
    #  模型答「当然记得啦!我们之前聊过天气、日常趣事…」—— **那段"之前"根本不存在**,
    #  会话就是从「你好」开始的。它在第二轮就凭空捏造了共同回忆,而且语气极笃定。
    #
    #  根因:全链路一句提示词都没有,模型落回自带聊天人设(那种人设的默认行为
    #  就是营造熟稔感)。⇒ 这个项目从 P0 起的全部纪律是「绝不伪造」,
    #  而产品面上第一个能开口的东西第二轮就在编。
    #
    #  ★ 放在中枢而不是客户端:客户端可以漏发、可以被改,将来还有桌宠/Agent/同传
    #    好几个调用方。一条"靠每个调用方自觉带上"的底线**不是底线**。
    #    与「权限按档位挂载而非运行时判断」同一条纪律:放在唯一权威那一侧。
    # ══════════════════════════════════════════════════════════════════
    if isinstance(fwd.get("messages"), list):
        fwd["messages"] = system_prompt.ensure(fwd["messages"])
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
