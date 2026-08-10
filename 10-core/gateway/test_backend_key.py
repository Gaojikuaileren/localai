r"""D? · 后端鉴权的【运行期】两半 —— 正向,以及 ★★**反向才是重点**。

跑:  py -3 10-core\gateway\test_backend_key.py

════════════════════════════════════════════════════════════════════════════
 ★★★ 这个文件为什么必须有反向

 「一条只测了正向的断言,和没有断言是一回事 —— 它在 key 被摘掉那天照样绿。」

 只钉「带 key 能连」的话:某天有人把 `headers=up_hdrs` 删掉,而后端恰好
 还没上锁(或者上的是空 key —— 实测那会让 llama-server 完全不鉴权),
 转发照样 200,这条断言照样绿。
 ⇒ 所以上游桩的行为是**照实测复刻**的:不带 key / 错 key 一律 401。
   摘掉钥匙 = 当场 401 = 当场红。

════════════════════════════════════════════════════════════════════════════
 ★★ 上游桩的行为依据(2026-08-10 实测 · llama-server version 10107 · 本机真起过)
   · 不带 key 打 `/v1/chat/completions` → **401**
   · 错 key                              → **401**
   · 对的 key                            → **200**
   · `/health` 与 `/v1/models` **不受 key 约束**(不带 key 也 200)——
     所以"就绪"证明不了"钥匙对",那一格由 `_wait_ready` 的带鉴权探测负责,
     本文件第 ③ 组钉它。

 ★ 桩用 `httpx.MockTransport`,拿到的是**真的 httpx Request/Response** ——
   流式那条路径(build_request → send(stream=True))与非流式走的是同一套对象,
   不是我自己编的一个"长得像响应"的东西。
"""
from __future__ import annotations

import asyncio
import sys
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:                                            # noqa: BLE001
    pass

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import httpx                                                 # noqa: E402
from fastapi.testclient import TestClient                     # noqa: E402

import backend_key                                            # noqa: E402
import gateway                                                # noqa: E402
import model_loader as ml                                     # noqa: E402

# TestClient 的 request.client.host 是 'testclient',会被 D28 认证桩 401。
# 本文件测的是**出站鉴权**,不是入站认证,故旁路它(同 test_gateway_e1.py)。
gateway.classify_caller = lambda req: "trusted-local"

#: 测试用钥匙。★ **不读真实密钥文件** —— 一条会因为"这台机器上恰好有/没有那个文件"
#  而变色的断言,测的就不是它自称在测的东西(同 test_gateway_e1 文件头那段教训)。
#  真文件那一格归 `90-ops/verify-backend-auth.ps1`(它对着真后端打)。
FAKE_KEY = "f" * 64

_p = _f = 0


def check(name: str, cond: bool, extra: str = "") -> None:
    global _p, _f
    if cond:
        _p += 1
    else:
        _f += 1
        print(f"  X {name}" + (f"   {extra}" if extra else ""))


# ══════════════════════════════════════════════════════════════════════
#  上游桩 —— 行为照实测复刻
# ══════════════════════════════════════════════════════════════════════
class _Upstream:
    def __init__(self, key: str | None):
        self.key = key
        self.seen: list[str | None] = []            # 每次收到的 Authorization

    def handler(self, request: httpx.Request) -> httpx.Response:
        got = request.headers.get("authorization")
        self.seen.append(got)
        if self.key is None or got != f"Bearer {self.key}":
            return httpx.Response(401, json={"error": {"message": "Invalid API Key"}})
        # ★ 流式那条路径必须回一个**真的流** —— 把 json= 造的响应拿去 aiter_raw()
        #   会抛 StreamConsumed(写这个文件时当场踩到)。桩要跟被测代码走同一条路,
        #   不然"流式也带了钥匙"这条断言其实从来没走到过流式。
        if b'"stream": true' in (request.content or b'') or b'"stream":true' in (request.content or b''):
            _SEP = b"\n\n"

            async def _chunks():
                yield b'data: {"choices":[{"delta":{"content":"ok"}}]}' + _SEP
                yield b"data: [DONE]" + _SEP
            return httpx.Response(200, content=_chunks(),
                                  headers={"content-type": "text/event-stream"})
        return httpx.Response(200, json={"choices": [{"message": {"role": "assistant",
                                                                  "content": "ok"}}]})

    def install(self):
        gateway._client = httpx.AsyncClient(transport=httpx.MockTransport(self.handler),
                                            trust_env=False)
        return self


def post(stream: bool = False):
    return client.post("/v1/chat/completions",
                       json={"model": "assistant.fast", "stream": stream,
                             "messages": [{"role": "user", "content": "你好"}]})


client = TestClient(gateway.app, raise_server_exceptions=True)

print("=== D? 后端鉴权 · 运行期两半 ===")

# ══════════════════════════════════════════════════════════════════════
#  ① 正向:网关出站**带着钥匙**
# ══════════════════════════════════════════════════════════════════════
print("\n=== 1. 正向:出站带钥匙 ===")
backend_key.load_key = lambda: FAKE_KEY                       # 注入,不读真实文件
up = _Upstream(FAKE_KEY).install()
r = post()
check("★ 带对钥匙时,转发成功(200)", r.status_code == 200, str(r.status_code))
check("★★ 出站请求上**真的有** Authorization: Bearer <key> —— "
      "这一条要是靠「看代码里写了」来相信,那 `headers=` 拼错一个字母也照样绿",
      up.seen and up.seen[-1] == f"Bearer {FAKE_KEY}", str(up.seen[-1:]))

up_s = _Upstream(FAKE_KEY).install()
rs = post(stream=True)
check("★ 流式那条路径**也**带钥匙(它走的是 build_request+send,与非流式不是同一行代码)",
      rs.status_code == 200 and up_s.seen and up_s.seen[-1] == f"Bearer {FAKE_KEY}",
      str(up_s.seen[-1:]))

# ══════════════════════════════════════════════════════════════════════
#  ② ★★ 反向:钥匙不对 / 没带,就**连不上**
# ══════════════════════════════════════════════════════════════════════
print("\n=== 2. ★★ 反向:没带钥匙就连不上 ===")
backend_key.load_key = lambda: "w" * 64                       # 错钥匙
up_wrong = _Upstream(FAKE_KEY).install()
r_wrong = post()
check("★★★ **错钥匙 → 401**(后端拒绝)。这一条是本车道的全部价值所在:"
      "它在 key 被摘掉那天会红,而只测正向的断言不会",
      r_wrong.status_code == 401, str(r_wrong.status_code))

# 把出站头整个摘掉,模拟"有人把 headers=up_hdrs 删了"
backend_key.load_key = lambda: FAKE_KEY
up_naked = _Upstream(FAKE_KEY).install()


class _StripAuth(httpx.AsyncClient):
    """模拟「转发时忘了带头」。★ 它存在的意义是让**下面那条断言真的能为假**。"""

    def build_request(self, *a, **k):
        k.pop("headers", None)
        return super().build_request(*a, **k)

    async def post(self, *a, **k):
        k.pop("headers", None)
        return await super().post(*a, **k)


gateway._client = _StripAuth(transport=httpx.MockTransport(up_naked.handler), trust_env=False)
r_naked = post()
check("★★★ **不带钥匙 → 401**:哪天有人把 `headers=up_hdrs` 删掉,这一条立刻红",
      r_naked.status_code == 401, str(r_naked.status_code))
check("★ 而且后端确实收到了一个**没有** Authorization 的请求(证明上面那条红对了地方)",
      up_naked.seen and up_naked.seen[-1] is None, str(up_naked.seen[-1:]))

# ══════════════════════════════════════════════════════════════════════
#  ③ fail-closed:拿不到钥匙时**根本不发**,不是"不带头继续发"
# ══════════════════════════════════════════════════════════════════════
print("\n=== 3. fail-closed:拿不到钥匙就不发 ===")


def _boom():
    raise backend_key.BackendKeyError("注入:拿不到钥匙")


backend_key.load_key = _boom
up_never = _Upstream(FAKE_KEY).install()
r_503 = post()
check("★★ 拿不到钥匙 → 503(§8.1.4 带缺口,不静默降级)", r_503.status_code == 503,
      str(r_503.status_code))
check("★★★ 而且**一个字节都没往后端发** —— 这条比状态码更重要:"
      "「取不到就不带头继续发」会在后端尚未上锁的机器上碰巧还能用,"
      "于是这条债静默重开而所有测试全绿",
      up_never.seen == [], str(up_never.seen))
check("★ 错误类型可辨(不是笼统的 backend_unavailable)—— 修法完全不同",
      r_503.json().get("error", {}).get("type") == "backend_key_unavailable",
      str(r_503.json()))

backend_key.load_key = lambda: FAKE_KEY                       # 复位,供后面用

# ══════════════════════════════════════════════════════════════════════
#  ④ 起后端的那一侧:args 带钥匙 + /health 绿但钥匙不对要报出来
# ══════════════════════════════════════════════════════════════════════
print("\n=== 4. 起后端那一侧 ===")

#: ★ 用**相对**假路径:带盘符的字面量会被 pre-commit 的绝对路径闸拦下(§11.1),
#  而这里根本不需要一个真路径 —— 只要一个「backend_key 给了什么、args 里就该是什么」的靶。
FAKE_KF = Path("_fake_backend_") / "llama-api.key"
ml.backend_key.ensure_key_file = lambda: FAKE_KF               # 不去动真实系统状态


class _ArgsLoader(ml.ModelLoader):
    """只为看**起法拼出来的 args**,不真起进程、不碰真实路径。"""

    def __init__(self, cfg):
        super().__init__(cfg=cfg)
        self.captured: list = []

    def _llama_exe(self):
        return Path("_fake_backend_") / "llama-server.exe"

    def _model_path(self, rel):
        return Path("_fake_backend_") / rel

    #: ★ 第一次问是 `load()` 的**认领判据**(端口上已经有人就不再起一个),
    #  必须答 False 才会走到起法;之后才是就绪轮询。
    #  —— 这一格是写下来当场踩到的:两处用的是同一个方法,答案却必须不同。
    async def _health_ok(self, port, timeout=2.0):
        self._asked += 1
        return self._asked > 1

    async def _auth_ok(self, port, timeout=5.0):
        return self.auth_answer

    auth_answer = True
    _asked = 0


class _FakePopen:
    def __init__(self, args, **kw):
        _captured.append(list(args))
        self.returncode = None

    def poll(self):
        return None


_captured: list = []
_real_popen = ml.subprocess.Popen
ml.subprocess.Popen = _FakePopen

cfg = gateway.gpu_broker.BROKER.cfg
llm_cid = next(c for c, v in cfg.components.items() if str(v.get("kind")) == "llm")

ld = _ArgsLoader(cfg)
asyncio.run(ld.load([llm_cid]))
args = _captured[-1] if _captured else []
check("★★★ 起 llama 后端的 args 里**有** --api-key-file —— 没有它,18081 对同机所有进程敞开",
      "--api-key-file" in args, str(args))
check("★ 而且指向 backend_key 给的那个文件(不是另抄一个路径)",
      "--api-key-file" in args and args[args.index("--api-key-file") + 1] == str(FAKE_KF),
      str(args))
check("★★ 命令行上出现的是**路径**,不是钥匙本身 —— 命令行同机任何进程都读得到",
      not any(len(str(a)) == 64 and str(a).isalnum() for a in args), str(args))

# /health 绿、但钥匙不对 ⇒ 必须报出来(这一格就是 /health 不受 key 约束的后果)
ld2 = _ArgsLoader(cfg)
ld2.auth_answer = False
_captured.clear()
raised = ""
try:
    asyncio.run(ld2.load([llm_cid]))
except ml.LoaderError as e:
    raised = str(e)
check("★★★ /health 是绿的、但**钥匙不对** ⇒ 装载必须失败并说清楚 —— "
      "实测 /health 不受 key 约束,所以不带鉴权探一次的话,"
      "栈会「启动成功」然后每一次对话都 401(失败发生在启动,却等到用户开口才显形)",
      "钥匙" in raised, raised[:120])

ml.subprocess.Popen = _real_popen

# ══════════════════════════════════════════════════════════════════════
#  ⑤ 元断言:出站转发**一处都不许漏**
# ══════════════════════════════════════════════════════════════════════
print("\n=== 5. 元断言:出站一处都不许漏 ===")
gw_src = (HERE / "gateway.py").read_text(encoding="utf-8")
fwd = [ln for ln in gw_src.splitlines()
       if "upstream_url" in ln and "_client." in ln]
check("★ 至少扫到一处出站调用(零命中 = 提取器坏了,不是「没有出站」)", len(fwd) > 0, str(len(fwd)))
check("★★★ 每一处出站转发都带 headers —— 将来多一条转发路径(比如 embeddings)"
      "而忘了带钥匙,这一条会红。★ 广度那一半在 90-ops/gate/check_backend_auth.py",
      all("headers=" in ln for ln in fwd),
      str([ln.strip()[:70] for ln in fwd if "headers=" not in ln]))

print("-" * 70)
print(f"  === D? 后端鉴权 · 运行期:{_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
