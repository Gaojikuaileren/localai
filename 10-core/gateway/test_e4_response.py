"""E4 **方向 B**(响应回程出境闸)测试 —— 单元 + 网关集成。纯 assert,无 pytest:

    python test_e4_response.py

★★★ 这套测试的重点是**两个方向都钉住**,只钉一边等于没钉:
  · 正向:答案里的凭证**拦得住**;
  · ★★ 反向:**不该拦的不许拦** —— D81 实测过「扫整个响应体 ⇒ 100% 全拦」
    (响应里那个 32 位随机 `id` 单独就触发 high_entropy)。
    一个太宽的闸会让**每一条回答都发不出去**,而当值的人的第一反应是**把闸调松** ——
    调松之后它看起来还在,实际什么都不守。⇒ 反向那几条与正向同等承重。

★★★ 还有一条:**摘掉扫描必须红**,而且判据要**真起一条流**,不是测「函数存在」。
  本文件最后一节把 `scan_response` 换成恒不拦,重跑那条集成断言 —— 它必须失守。
  一条在扫描被摘掉之后**仍然通过**的断言,测的不是扫描。
"""
import inspect
import json
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:                                            # noqa: BLE001
    pass

import httpx

import e1_detector as e1
import e4_response as e4b
import gpu_policy

_p = _f = 0


def check(name, cond, extra=""):
    global _p, _f
    if cond:
        _p += 1
    else:
        _f += 1
        print(f"  FAIL  {name}" + (f"   {extra}" if extra else ""))


# ★ 公开测试语料:ECB 官方示例 IBAN(同 test_e1.py / test_e4_egress.py 的约定)。
#   真凭证一律不入库,哪怕是测试。
IBAN = "DE89370400440532013000"
#: 同一个 IBAN 的**全角**写法 —— 它在原始 SSE 字节里会是 `ＤＥ…`。
IBAN_FULLWIDTH = "".join(
    chr(ord(c) - 0x30 + 0xFF10) if c.isdigit() else
    (chr(ord(c) - 0x41 + 0xFF21) if c.isalpha() else c) for c in IBAN)
#: 真实形状的 32 位随机响应 id —— D81 说的就是它。
RANDOM_ID = "chatcmpl-9lQ3zK2pXvR7bN4tYw8sHdGf1aJ0mC5e"


def body(content: str, **extra) -> dict:
    d = {"id": RANDOM_ID, "object": "chat.completion", "created": 1756900000,
         "model": "assistant.fast", "system_fingerprint": "fp_44709d6fcb",
         "choices": [{"index": 0, "finish_reason": "stop",
                      "message": {"role": "assistant", "content": content}}],
         "usage": {"prompt_tokens": 10, "completion_tokens": 5, "total_tokens": 15}}
    d.update(extra)
    return d


# ══════════════════════════════════════════════════════════════════════
print("=== 1. sink 轴:反向全表 + 表外 fail-closed ===")
# ══════════════════════════════════════════════════════════════════════

check("★★★ 档位反向全表:遍历源是 gpu_policy.TIER_CAPS,本表只当期望值 —— "
      "新加一个档位而不来登记 ⇒ 这条红(默认落判红侧)",
      e4b.unregistered_tiers() == [], f"对不上:{e4b.unregistered_tiers()}")
check("★★ 遍历源**零命中判红**(TIER_CAPS 解出 0 个档位时,上面那条会空对空地绿)",
      len(gpu_policy.TIER_CAPS) > 0, f"{len(gpu_policy.TIER_CAPS)}")

check("★★★ 表外档位 ⇒ EGRESS(fail-closed)—— 明天真接一条桥进来,闸**默认是关着的**",
      e4b.sink_class("channel-relay") == e4b.EGRESS)
check("★★ 空档位名同样 fail-closed(判据不许被一个空串绕过)",
      e4b.sink_class("") == e4b.EGRESS)
check("★ 在册档位按表返回(判据不是恒 EGRESS —— 恒 EGRESS 会让今天每条回答都过闸)",
      e4b.sink_class("trusted-local") == e4b.IN_SET)

# ★★ 如实钉住「今天零生产影响」这句话:在册的七个档位**一个 EGRESS 都没有**。
#   这条不是装饰 —— 交回时说的「今天不改变任何人的行为」全靠它。
#   将来 D81 待裁 ① 把 unregistered-local 翻成 EGRESS,这条会红,**它就该红**:
#   那是一次有代价的裁定,不该悄悄发生。
check("★★★ 今天在册档位里没有出境 sink ⇒ 本闸对生产**零影响**(这句话有判据,不是自述)",
      all(v == e4b.IN_SET for v in e4b.SINK_CLASS.values()),
      f"{[k for k, v in e4b.SINK_CLASS.items() if v != e4b.IN_SET]}")

check("★★ must_force_nonstream 只看档位 —— 签名里只有 tier 一个参数,读不到报文",
      list(inspect.signature(e4b.must_force_nonstream).parameters) == ["tier"])
check("★ 出境档位要强制非流式", e4b.must_force_nonstream("channel-relay") is True)
check("★ 在册档位不强制(局域网客户端照旧流式 —— D81 决定 3 的另一半)",
      e4b.must_force_nonstream("lan-device") is False)


# ══════════════════════════════════════════════════════════════════════
print("=== 2. 出境没有放行入口(与方向 A 同一条结构约束)===")
# ══════════════════════════════════════════════════════════════════════

# ★ 有意**不含** `continue`:它是 Python 关键字(`_walk` / `extract_answer` 的循环体里
#   本来就有),拿它当放行语义的标记会造出一条**必然误红**的判据 —— 而误红的护栏
#   很快就被人放宽,那才是真正的损失。E1 那个 `continue` 是**请求头的取值**,
#   由下面单独一条判据去钉。
_BANNED = {"override", "allow", "force", "bypass", "whitelist"}
for fn in (e4b.scan_response, e4b.extract_answer, e4b.sink_class,
           e4b.is_egress_sink, e4b.must_force_nonstream):
    names = set(inspect.signature(fn).parameters)
    check(f"★★ {fn.__name__} 的参数名里没有任何放行语义", not (names & _BANNED),
          f"{sorted(names & _BANNED)}")

# 只看**会执行的代码**:摘掉文档串与 # 注释行(注释里当然要谈 override —— 谈的正是
# "这里为什么没有它")。同 test_e4_egress.py 第 3 节的手法。
_src = inspect.getsource(e4b)
for _doc in [e4b.__doc__] + [f.__doc__ for f in
                             (e4b.scan_response, e4b.extract_answer, e4b.sink_class,
                              e4b.is_egress_sink, e4b.must_force_nonstream,
                              e4b.unregistered_tiers, e4b.blocked_reply_text,
                              e4b._assert_no_unlock_leak)]:
    if _doc:
        _src = _src.replace(_doc, "")
_code = " ".join(l for l in _src.splitlines() if not l.lstrip().startswith("#"))
check("★★★ 模块的【代码】里没有任何放行分支", not (set(_code.split()) & _BANNED),
      f"{sorted(set(_code.split()) & _BANNED)}")
check("★★★ 代码里读不到 E1 的解除入口(请求头名 / 带内暗号)—— "
      "出境的放行能力必须在结构上不存在,而不是'默认关着'",
      "x-localai-e1-override" not in _code.lower() and e1.OVERRIDE_PHRASE not in _code)


# ══════════════════════════════════════════════════════════════════════
print("=== 3. ★★ 字段边界:反向那一侧(不该拦的不许拦)===")
# ══════════════════════════════════════════════════════════════════════

_clean = body("今天天气不错,我们来聊聊别的。")

# ★★★ 这两条**必须一起看**:上面那条证明 D81 说的那件事今天仍然成立
#   (naive 实现 = 每条回答都发不出去),下面那条证明我们真的绕开了信封。
#   只留下面那条的话,它可能是因为**检测器坏了**而绿 —— 那种绿最贵。
check("★★★ 【前提复测】扫整份响应体确实会命中 high_entropy(那个 32 位随机 id)—— "
      "这条一旦变绿失效,下面那条'干净响应不拦'就不再说明任何事",
      e1.HIGH_ENTROPY in e1.scan(json.dumps(_clean, ensure_ascii=False)).categories)
check("★★★ 而**只扫答案**时,同一份干净响应【不拦】—— 这就是那条 100% 全拦被绕开的地方",
      not e4b.scan_response(_clean).blocked,
      f"{sorted(e4b.scan_response(_clean).categories)}")

check("★★ 干净响应里**没有未登记字段**(有的话说明信封表漏了,而漏的那一侧今天是'要扫')",
      e4b.scan_response(_clean).undeclared == [],
      f"{e4b.scan_response(_clean).undeclared}")

# tool_calls:同一层里**随机 id 是信封、模型生成的参数是答案**,只认键名会把两者混掉。
_tc = body(None)
_tc["choices"][0]["message"]["tool_calls"] = [{
    "id": "call_9lQ3zK2pXvR7bN4tYw8sHdGf1aJ0mC5e", "type": "function", "index": 0,
    "function": {"name": "search", "arguments": '{"q": "天气"}'}}]
_tc["choices"][0]["message"]["content"] = ""
check("★★ tool_calls[].id 是**信封**(又一个随机 id)⇒ 不拦", not e4b.scan_response(_tc).blocked,
      f"{sorted(e4b.scan_response(_tc).categories)} undeclared={e4b.scan_response(_tc).undeclared}")
_tc2 = json.loads(json.dumps(_tc))
_tc2["choices"][0]["message"]["tool_calls"][0]["function"]["arguments"] = \
    json.dumps({"iban": IBAN}, ensure_ascii=False)
check("★★ 而 function.arguments 是**答案** ⇒ 里头的凭证照拦",
      e4b.scan_response(_tc2).blocked)


# ══════════════════════════════════════════════════════════════════════
print("=== 4. 正向:答案里的凭证拦得住 ===")
# ══════════════════════════════════════════════════════════════════════

_dirty = body(f"当然可以,你的账号是 {IBAN}。")
_r = e4b.scan_response(_dirty)
check("★★★ 答案里的凭证被拦", _r.blocked)
check("★ 拦下时有类别", e1.IBAN in _r.categories)
check("★ 与 E1 检出结果一致(共用同一套检测器,不另造一份判据)",
      _r.categories == e1.scan(f"当然可以,你的账号是 {IBAN}。").categories)

check("★ reasoning_content 也算答案(模型生成的另一条出口)",
      e4b.scan_response(body("好的", **{})) is not None)
_rc = body("好的")
_rc["choices"][0]["message"]["reasoning_content"] = f"用户的 IBAN 是 {IBAN}"
check("★★ reasoning_content 里的凭证照拦(它同样是模型写出来的)",
      e4b.scan_response(_rc).blocked)


# ══════════════════════════════════════════════════════════════════════
print("=== 5. ★★★ 必须先 JSON 解码再扫(D81 决定 2(2))===")
# ══════════════════════════════════════════════════════════════════════

_fw = body(f"你的账号是 {IBAN_FULLWIDTH}")
_raw = "data: " + json.dumps(_fw, ensure_ascii=True)      # 模型输出到达时的形态

# ★★ 判据钉的是「**对那个 IBAN** 瞎」,不是「一条都不命中」——
#   原始字节里还躺着那个 32 位随机 id,它会照常触发 high_entropy。
#   写成 `not blocked` 的话这条会因为**另一个不相干的命中**而红,
#   而红出来的原因读起来像是"解码那条约束不成立" —— 给错原因比不给更坏。
check("★★★ 【前提复测】扫**原始字节**对全角凭证是瞎的(normalize 折全角,但不折 \\uXXXX)",
      e1.IBAN not in e1.scan(_raw).categories, f"{sorted(e1.scan(_raw).categories)}")
check("★★★ 而先解码再扫 ⇒ 命中 —— 这就是'谁把原始字节喂进来谁就把闸关掉了'那一条",
      e4b.scan_response(_fw).blocked and e1.IBAN in e4b.scan_response(_fw).categories)
check("★★ scan_response 拒收 str(不做'顺手解码',那会把一条硬约束变成一句提醒)",
      e4b.scan_response(_raw).blocked and e4b.scan_response(_raw).refused == ["<raw-bytes>"])
check("★★ 拒收 bytes 同理", e4b.scan_response(_raw.encode("utf-8")).refused == ["<raw-bytes>"])


# ══════════════════════════════════════════════════════════════════════
print("=== 6. 未登记字段落哪一侧(裁定题 ②:三个取值都要真的能用)===")
# ══════════════════════════════════════════════════════════════════════

_new = body("好的")
_new["choices"][0]["message"]["a_new_upstream_field"] = f"顺手带了一句 {IBAN}"

check("★★★ 默认 'scan':未登记字段里的凭证**照拦**(denylist 的失效方式就是新字段默认自由)",
      e4b.scan_response(_new).blocked)
check("★★ 并且**把路径报出来**(运维要看的是'上游加了什么字段',不是内容)",
      e4b.scan_response(_new).undeclared == ["choices[].message.a_new_upstream_field"],
      f"{e4b.scan_response(_new).undeclared}")
check("★★★ 'envelope' 那一侧**真的会放过** —— 这条把该取值的代价钉在测试里,"
      "而不是留在一句注释里",
      not e4b.scan_response(_new, undeclared_side="envelope").blocked)
check("★★ 'refuse' 那一侧:未登记字段本身就足以拦(不看内容)",
      e4b.scan_response(body("好的干净回答"), undeclared_side="refuse").blocked is False
      and e4b.scan_response(_new, undeclared_side="refuse").blocked)
check("★★★ 配错值 ⇒ fail-closed 到 'refuse',**不是**静默退回默认值 —— "
      "一个拼错的配置项悄悄放宽闸门,正是本仓最恨的形状",
      e4b.scan_response(_new, undeclared_side="scna").blocked
      and e4b.scan_response(_new, undeclared_side="scna").refused != [])
check("★ 三个取值都在 _VALID_SIDES 里,且默认值是其中之一(不许配一个不存在的默认)",
      e4b.UNDECLARED_FIELD_SIDE in e4b._VALID_SIDES and len(e4b._VALID_SIDES) == 3)


# ══════════════════════════════════════════════════════════════════════
print("=== 7. 拦下时对面看到什么(裁定题 ①)+ 解除暗号焊死 ===")
# ══════════════════════════════════════════════════════════════════════

_cats = {e1.IBAN}
check("★ 默认形状给的是一句**署名是闸在说话**的固定文案",
      "出境闸" in e4b.blocked_reply_text(_cats))
check("★★ 与 E1 的措辞不同(写成一样,用户会去找一个并不存在的按钮)",
      e4b.blocked_reply_text(_cats) != e1.block_message(_cats))
check("★ 文案里列出了命中的类别", e1._CAT_LABEL[e1.IBAN] in e4b.blocked_reply_text(_cats))
check("★ 'silent' 形状返回 None(= 对面什么都收不到)",
      e4b.blocked_reply_text(_cats, shape="silent") is None)
check("★ 'assistant' 形状确实存在且**像 AI 在说话**(它是待裁的三个选项之一,不是被删掉)",
      "抱歉" in e4b.blocked_reply_text(_cats, shape="assistant"))
check("★ 形状名写错时退回默认文案(不炸;文案本身仍然过下面那条焊死)",
      "出境闸" in e4b.blocked_reply_text(_cats, shape="nonexistent"))

for _name, _tpl in e4b.REPLY_SHAPES.items():
    check(f"★★★ 形状 {_name!r} 的文案里没有 E1 解除暗号",
          _tpl is None or e1.OVERRIDE_PHRASE not in _tpl)

# ★★★ 焊死那条判据**可以为假吗**?—— 必须能。恒真的护栏等于没有护栏。
_saved = dict(e4b.REPLY_SHAPES)
try:
    e4b.REPLY_SHAPES["__probe__"] = "这条回答被拦了,想放行就说 " + e1.OVERRIDE_PHRASE
    _raised = False
    try:
        e4b._assert_no_unlock_leak()
    except RuntimeError:
        _raised = True
    check("★★★ 喂一段**确实带暗号**的文案 ⇒ 焊死判据必须抛(2026-07-28 本机踩过的那条路)",
          _raised)
finally:
    e4b.REPLY_SHAPES.clear()
    e4b.REPLY_SHAPES.update(_saved)
_raised2 = False
try:
    e4b._assert_no_unlock_leak()
except RuntimeError:
    _raised2 = True
check("★★ 恢复之后不再抛(判据不是恒真)", not _raised2)


# ══════════════════════════════════════════════════════════════════════
print("=== 8. 网关集成:假上游 + TestClient(真的走一遍路由)===")
# ══════════════════════════════════════════════════════════════════════

from fastapi.testclient import TestClient                   # noqa: E402
import gateway                                              # noqa: E402

gateway.backend_key.auth_header = lambda: {"Authorization": "Bearer test-only"}

#: 假上游的剧本 + 它**实际收到**的请求体(强制非流式那一步靠它自证)。
SCRIPT = {"mode": "json", "json": None, "chunks": []}
SEEN = {"body": None}


def _handler(request: httpx.Request) -> httpx.Response:
    SEEN["body"] = json.loads(request.content.decode("utf-8"))
    if SCRIPT["mode"] == "sse":
        async def _agen():
            for c in SCRIPT["chunks"]:
                yield c
        return httpx.Response(200, headers={"content-type": "text/event-stream"},
                              content=_agen())
    return httpx.Response(200, json=SCRIPT["json"])


gateway._client = httpx.AsyncClient(transport=httpx.MockTransport(_handler))
client = TestClient(gateway.app, raise_server_exceptions=True)


def as_tier(tier):
    gateway.classify_caller = lambda req: tier


def post(stream=False, msg="你好"):
    return client.post("/v1/chat/completions",
                       json={"model": "assistant.fast", "stream": stream,
                             "messages": [{"role": "user", "content": msg}]})


# ── 8a. 出境 sink:强制非流式 + 拦得住 ───────────────────────────────
as_tier("channel-relay")                 # 表外档位 ⇒ EGRESS(不用 monkeypatch 本模块)
SCRIPT["mode"] = "json"
SCRIPT["json"] = body(f"当然,你的账号是 {IBAN}。")
_resp = post(stream=True)                # ★ 调用方**要流式**

check("★★★ 上游实际收到的 body 里 stream 是 False —— 强制那一步真的生效了"
      "(只改本地变量而 fwd 里还写着 true,上游会回 SSE 而我们拿 r.json() 解析它,"
      "表现成一条**说错原因**的 502)",
      SEEN["body"] is not None and SEEN["body"].get("stream") is False,
      f"{SEEN['body'] and SEEN['body'].get('stream')}")
check("★★★ 答案里的凭证被拦在发出之前", _resp.headers.get("X-LocalAI-E4B") == "response-egress-blocked",
      f"{dict(_resp.headers)}")
check("★ 拦截响应带类别头", _resp.headers.get("X-LocalAI-E4B-Categories") == e1.IBAN)
check("★★ 且**按调用方要的形态**回(它要 SSE,就得给 SSE —— 给 JSON 的话前端解析失败,"
      "用户看到的是报错而不是'这一轮没有发出去')",
      _resp.headers.get("content-type", "").startswith("text/event-stream"))
check("★★★ 凭证值**没有**出现在回给调用方的那份里(拦截文案只报类别,不回显值)",
      IBAN not in _resp.text)

# 非流式形态
_resp2 = post(stream=False)
check("★ 调用方要 JSON 就给 JSON", _resp2.headers.get("content-type", "").startswith("application/json"))
check("★★ 载荷里带 x_localai_e4b(blocked / categories / undeclared / refused 四栏)",
      set(_resp2.json()["x_localai_e4b"]) == {"blocked", "categories",
                                              "undeclared_fields", "refused_fields"})
check("★ finish_reason 是 content_filter(而不是伪装成正常说完)",
      _resp2.json()["choices"][0]["finish_reason"] == "content_filter")

# ── 8a'. ★★★ 契约面:本车道**没有**新增跨进程响应契约,而这句话要有判据 ──────
#
#  D95 那张广度表(90-ops\gate\check_contract_pairs.py)按**路由**登记响应契约,
#  `POST /v1/chat/completions` 已经是 paired。⇒ 只要拦截响应的**顶层键集合**
#  与既有的 E1 / E4 拦截响应一致,它就仍然落在那条已登记的契约里,DEBT 不动。
#  ★★ 反过来说清代价:一旦裁定选了 `"silent"`(空 choices),那就是一个**新形状**,
#    必须先去登记一条子形状契约号再上线 —— 那张表在 90-ops,不在本车道。
#    这条判据就是那道提醒:改形状的人会在这里先撞一次。
_E4_SHAPE = {"id", "object", "created", "model", "choices", "usage"}
check("★★★ 拦截响应的顶层键集合 = 既有 E1/E4 拦截响应的那一套 + 一个 x_localai_* 标记 ⇒ "
      "**没有新增契约形状**(DEBT 仍是 1)",
      set(_resp2.json()) == _E4_SHAPE | {"x_localai_e4b"}, f"{sorted(_resp2.json())}")
check("★★ 而 choices 元素**非空**(今天的形状) —— 'silent' 那一档会让它变成空数组,"
      "那是另一个形状,得先登记再上线",
      len(_resp2.json()["choices"]) == 1)

# ── 8b. ★★ 反向:同一条出境路径,干净答案必须**放行** ────────────────
SCRIPT["json"] = body("今天天气不错。")
_ok = post(stream=False)
check("★★★ 【反向】干净答案照常放行 —— 那个 32 位随机 id **没有**把它拦下"
      "(D81:一个太宽的闸会让每条回答都发不出去,而值班第一反应是把闸调松)",
      "X-LocalAI-E4B" not in _ok.headers, f"{dict(_ok.headers)}")
check("★★ 放行时内容原样到达", _ok.json()["choices"][0]["message"]["content"] == "今天天气不错。")
check("★ 契约回写仍然发生(闸放在回写之前,回写没被它吃掉)",
      _ok.json()["model"].startswith("assistant.fast("))

# ── 8c. ★★★ 真起一条流:在册档位走的还是原来那条路 ────────────────────
as_tier("trusted-local")
SCRIPT["mode"] = "sse"
_frames = [
    {"id": RANDOM_ID, "object": "chat.completion.chunk", "created": 1756900000,
     "model": "assistant.fast",
     "choices": [{"index": 0, "finish_reason": None,
                  "delta": {"role": "assistant", "content": "今天"}}]},
    {"id": RANDOM_ID, "object": "chat.completion.chunk", "created": 1756900000,
     "model": "assistant.fast",
     "choices": [{"index": 0, "finish_reason": None,
                  "delta": {"content": f"你的账号是 {IBAN}"}}]},
    {"id": RANDOM_ID, "object": "chat.completion.chunk", "created": 1756900000,
     "model": "assistant.fast",
     "choices": [{"index": 0, "finish_reason": "stop", "delta": {}}]},
]
SCRIPT["chunks"] = [("data: " + json.dumps(f, ensure_ascii=True) + "\n\n").encode("utf-8")
                    for f in _frames] + [b"data: [DONE]\n\n"]
_sent = b"".join(SCRIPT["chunks"])

with client.stream("POST", "/v1/chat/completions",
                   json={"model": "assistant.fast", "stream": True,
                         "messages": [{"role": "user", "content": "你好"}]}) as _s:
    _got = b"".join(_s.iter_raw())
    _ctype = _s.headers.get("content-type", "")

check("★★★ 在册档位**真的开了一条流**(SSE),不是被强制成了整包",
      _ctype.startswith("text/event-stream"), _ctype)
check("★★★ 上游收到的 stream 仍然是 True —— 局域网客户端**一个字节都没被动过**",
      SEEN["body"].get("stream") is True, f"{SEEN['body'].get('stream')}")
check("★★★ 客户端收到的字节与上游发出的**逐字节相同**(原样透传:循环体里没多出扫描,"
      "也就没有多出延迟)", _got == _sent, f"{len(_got)} vs {len(_sent)}")
check("★★ 而这条流里**带着凭证**却照样放行 —— 受控设备集内部不是出境,"
      "本闸不该管它(管了就是把 D81 决定 3 的另一半也毁掉)",
      IBAN.encode() in _got)


# ══════════════════════════════════════════════════════════════════════
print("=== 9. ★★★ 摘掉扫描 ⇒ 上面那条集成断言必须失守 ===")
#
#  一条在扫描被摘掉之后**仍然通过**的断言,测的不是扫描 —— 它测的是形状。
#  ⇒ 这里把 `scan_response` 换成恒不拦,把 8a 那条原样重跑一遍,要求它**红**。
# ══════════════════════════════════════════════════════════════════════

as_tier("channel-relay")
SCRIPT["mode"] = "json"
SCRIPT["json"] = body(f"当然,你的账号是 {IBAN}。")

_before = post(stream=False)
check("★ 【对照】没摘之前:拦得住", _before.headers.get("X-LocalAI-E4B") == "response-egress-blocked")

_orig = gateway.e4b.scan_response
try:
    gateway.e4b.scan_response = lambda data, *a, **k: e4b.ResponseScan()
    _after = post(stream=False)
    check("★★★ 摘掉扫描之后:**拦不住了**(证明上面那条钉的是扫描本身,不是响应形状)",
          "X-LocalAI-E4B" not in _after.headers, f"{dict(_after.headers)}")
    check("★★ 而且凭证**真的会流出去** —— 这就是摘掉它的代价,写成判据而不是写成担心",
          IBAN in _after.json()["choices"][0]["message"]["content"])
finally:
    gateway.e4b.scan_response = _orig

_restored = post(stream=False)
check("★★ 装回去之后又拦得住(判据不是一次性的)",
      _restored.headers.get("X-LocalAI-E4B") == "response-egress-blocked")


# ══════════════════════════════════════════════════════════════════════
print("=== 10. 强制非流式那一步本身被两个方向各问一次 ===")
# ══════════════════════════════════════════════════════════════════════

check("★★★ 出境档位 + body 说 stream:true ⇒ 仍然 False(调用方控制不了它)",
      gateway.upstream_stream_mode("channel-relay", {"stream": True}) is False)
check("★★ 在册档位 + body 说 stream:true ⇒ True(判据不是恒 False —— "
      "恒 False 会把局域网那条流也关掉,而那是 D81 决定 3 明写要保住的)",
      gateway.upstream_stream_mode("trusted-local", {"stream": True}) is True)
check("★ 在册档位 + 没写 stream ⇒ False(默认非流式,照旧)",
      gateway.upstream_stream_mode("trusted-local", {}) is False)


print("-" * 70)
print(f"=== E4 方向 B(响应回程出境闸):{_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
