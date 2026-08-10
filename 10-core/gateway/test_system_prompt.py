"""行为底线 + 人设层的判据。纯 assert,无 pytest:python test_system_prompt.py

★★★ 这套断言为什么在 2026-08-09 才有(而 FLOOR 从 2026-08-05 就在跑):

  在此之前 `system_prompt.py` **一条断言都没有**。那 5 条底线是实机血案换来的
  (模型第二轮就凭空捏造了一段共同回忆),而它们**从来没有判据守着** ——
  谁手一滑删掉一条,全仓门禁不会响一声。
  ⇒ 本轮要往同一个文件里加人设层,而 `system_prompt.py:30` 逐字预言过这次风险:
    「两者混在一句里的后果是:**调人设时会顺手把底线一起改掉**」。
    所以先把这 5 条钉住,再动隔壁。

★★ 本文件的判据分两端(D95 那张表的手法):
    ① **常量端**:`FLOOR` 里那 5 条逐条在不在;
    ② **上线端**:`ensure()` **真正注入出去**的消息里,那 5 条在不在。
  只钉①的话,"常量还在但没注入"会全绿 —— 而模型看到的是②。

★ 本文件里抄了 FLOOR 的原话(每条一句)。那是**故意的**:
  它就是"另一份期望值"。有人删掉 FLOOR 里的某一条,这里的副本会让断言当场红。
  ★★★ 红了之后**唯一正确的动作是把 FLOOR 改回去**,不是把下面这张表删掉一行 ——
    删表 = 把这次要防的事亲手做掉。(ASSERTION-PITFALLS 第 1 条同一类陷阱。)
"""

import re
import sys

import assert_helpers

import gateway
import persona
import system_prompt

# ★★ 编码双保险:干净的 cp936 控制台编不出 ★ / · ,print 一抛异常会把整套掀翻,
#   于是【一条断言变红】表现成【整套崩溃】——运行器只看到"没有汇总行"。见 test_imports.py 同段。
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

_pass = _fail = 0


def check(name, cond, extra=""):
    global _pass, _fail
    if cond:
        _pass += 1
    else:
        _fail += 1
        print(f"  FAIL  {name}{(' — ' + extra) if extra else ''}")


FLOOR = system_prompt.FLOOR
PERSONA = persona.PERSONA

# ══════════════════════════════════════════════════════════════════════════════
#  1. 行为底线那 5 条 —— 逐条钉死
#
#  每一条对应一次**真实的失败形态**(见 system_prompt.py 顶上那段实测记录),
#  不是泛泛的"要诚实"。所以判据也逐条给,不给一个"总字数 > N"之类的糊涂判据。
# ══════════════════════════════════════════════════════════════════════════════
FLOOR_ITEMS = {
    "① 不得声称记得没发生过的事": [
        "不要声称记得没发生过的事",
        "不要编造共同经历来显得亲切",
        "你没有跨会话的长期记忆",
    ],
    "② 不得声称没有的能力": ["不要声称你有你没有的能力", "不能上网"],
    "③ 不确定就说不确定": ["不确定就说不确定"],
    "④ 分清输出与事实": ["记不清出处就说记不清"],
    "⑤ 用用户的语言": ["用用户使用的语言回答"],
}

for _label, _pins in FLOOR_ITEMS.items():
    for _pin in _pins:
        check(f"FLOOR {_label}:「{_pin}」还在", _pin in FLOOR, "这一条被删/被改写了")

# ★ 条目数对拍:光钉字句挡不住"多塞一条进来"。5 是 2026-08-05 定下的那 5 条。
_numbered = re.findall(r"^\s*(\d+)\.\s", FLOOR, re.M)
check("FLOOR 恰好 5 条编号项", len(_numbered) == 5, f"实际 {len(_numbered)} 条:{_numbered}")
check("FLOOR 的表与本文件这张表条数一致", len(FLOOR_ITEMS) == len(_numbered),
      f"表里 {len(FLOOR_ITEMS)} 条 / FLOOR 里 {len(_numbered)} 条")

# ══════════════════════════════════════════════════════════════════════════════
#  2. 底线与人设**分属两个常量、两条消息** —— 本轮改动的核心红线
#
#  `system_prompt.py:30` 写着:两者混在一句里的后果是"调人设时会顺手把底线一起改掉"。
#  ⇒ 这里不是"建议分开",是**判据**:拼进同一句就红。
# ══════════════════════════════════════════════════════════════════════════════
check("人设是独立常量,不在 system_prompt 里", "PERSONA" not in dir(system_prompt))
check("FLOOR 不含人设正文", PERSONA not in FLOOR)
check("人设不含 FLOOR 正文", FLOOR not in PERSONA)
check("人设正文非空", len(PERSONA.strip()) > 0)
# ★ 反向:人设里不许把底线那几句"顺手抄一份"—— 抄了就会分家,而分家之后两份都以为自己是对的。
for _label, _pins in FLOOR_ITEMS.items():
    check(f"人设没有复制 FLOOR {_label}", not any(p in PERSONA for p in _pins))

# ══════════════════════════════════════════════════════════════════════════════
#  3. 上线端:`ensure()` 真正发出去的是什么
# ══════════════════════════════════════════════════════════════════════════════
_msgs = system_prompt.ensure([])
check("空输入注入两条", len(_msgs) == 2, f"实际 {len(_msgs)} 条")
check("两条都是 system", all(m.get("role") == "system" for m in _msgs))
_contents = [m.get("content") for m in _msgs]
check("有一条**恰好**是 FLOOR(不是包含,是相等)", FLOOR in _contents)
check("有一条**恰好**是人设", PERSONA in _contents)
check("没有把两者拼成一条", not any(FLOOR in c and PERSONA in c for c in _contents))
check("底线在人设前面", _contents.index(FLOOR) < _contents.index(PERSONA))

# ★★ 上线端逐条复验:常量还在但没注入 = 模型什么也没看到。这一端与 §1 那端各查各的。
_wire = "\n".join(_contents)
for _label, _pins in FLOOR_ITEMS.items():
    for _pin in _pins:
        check(f"注入的消息里有 FLOOR {_label}:「{_pin}」", _pin in _wire)

# 调用方自带 system:不覆盖、不删除,底线排在它前面
_own = {"role": "system", "content": "调用方自己的指示"}
_r = system_prompt.ensure([_own, {"role": "user", "content": "你好"}])
check("调用方的 system 原样保留", _own in _r)
check("底线排在调用方 system 前面",
      [m.get("content") for m in _r].index(FLOOR) < [m.get("content") for m in _r].index("调用方自己的指示"))
check("用户消息没被动", {"role": "user", "content": "你好"} in _r)

# 幂等:转发两次不叠层
check("幂等(二次 ensure 不变长)", len(system_prompt.ensure(_msgs)) == len(_msgs))
# ★★ 两条**各自**判幂等:只带了底线的旧调用方,必须把缺的人设补上。
#   合起来判(「有底线就当两条都有」)会静默少注入人设 —— 而少注入的表现
#   正是这次要治的病(模型落回自带人设、自称千问),它不报错。
_only_floor = system_prompt.ensure([{"role": "system", "content": FLOOR}])
check("只带底线时把人设补上", PERSONA in [m.get("content") for m in _only_floor])
check("只带底线时不重复注入底线", [m.get("content") for m in _only_floor].count(FLOOR) == 1)
_only_persona = system_prompt.ensure([{"role": "system", "content": PERSONA}])
check("只带人设时把底线补上", FLOOR in [m.get("content") for m in _only_persona])
check("只带人设时不重复注入人设", [m.get("content") for m in _only_persona].count(PERSONA) == 1)

# ══════════════════════════════════════════════════════════════════════════════
#  4. 人设层:用户裁定的那几件事(实机反馈 ⑧⑨)
# ══════════════════════════════════════════════════════════════════════════════
check("人设明写:不报产品名/厂商名", "不要报任何产品名" in PERSONA)
# ★ 逐个点名。只写"不要报产品名"是**不够**的:模型对抽象禁令的服从度远低于点名,
#   而这次事故就是它报了一个具体的名字。
for _brand in ("千问", "Qwen", "GPT", "Claude", "Llama"):
    check(f"人设点名禁用「{_brand}」", _brand in PERSONA)
check("人设处理了「你叫什么」", "名字" in PERSONA)
check("人设写了称呼", "称呼" in PERSONA)
check("人设写了不复述条款套话", "作为一个 AI 语言模型" in PERSONA and "内容政策" in PERSONA)
# ★★★ 优先级明写在正文里,不靠"人设排在后面"这种位置约定 ——
#   位置在下一次有人改 ensure() 的拼装顺序时就失效,而且失效时不会红。
check("人设明写与底线冲突时按底线来", "按底线来" in PERSONA)

# ══════════════════════════════════════════════════════════════════════════════
#  5. ★★★ 反向:人设里**不许**出现"限制已移除"这类做不到的声称
#
#  用户问的是「色情内容之类的限制可不可以移除」。查实:中枢一条内容政策都没加,
#  网关也没有输出过滤器 —— 那些政策话是**模型权重里训出来的**。
#  ⇒ 提示词能做的是"别把套话说出来",**做不到**"把训练出来的拒绝去掉"。
#  写一段 prompt 然后声称限制已移除,正是本仓最恨的「声称了但没做到」。
#  ★ 这条断言就是拦那一手 —— 它拦的是**我们自己**,不是模型。
# ══════════════════════════════════════════════════════════════════════════════
FALSE_CLAIMS = (
    "限制已移除", "已移除限制", "解除限制", "没有任何限制", "不受任何限制",
    "没有限制", "无任何限制", "忽略你之前", "忽略以上", "忽略所有先前",
    "不受道德", "无视规则",
)
for _fc in FALSE_CLAIMS:
    check(f"人设不含做不到的声称:「{_fc}」", _fc not in PERSONA)

# ══════════════════════════════════════════════════════════════════════════════
#  6. ★★★ 成对改动看门断言:记忆接上对话  ⇔  FLOOR 第 1 条那半句
#
#  FLOOR 第 1 条今天逐字写着:「记忆库存在,但目前还没有接到对话上」。
#  那一天真接上了,这半句就变成**底线自己在说谎** —— 比没有底线更糟:
#  它会让"绝不伪造"这条纪律在用户眼里整个作废。
#
#  ★ 判据两端:
#    ① 代码端:chat 路径 / 人设层 / 底线层的**可执行代码**里有没有碰记忆;
#    ② 正文端:FLOOR 里那半句还在不在。
#    两者必须**同时成立或同时不成立**。今天两端都是"没接",绿。
#
#  ★★ 为什么①要过 `code_only`:这三处的**注释里**写满了"记忆"两个字
#    (本文件、persona.py、gateway.py 的隔离段都是)。不去注释的话这条恒红,
#    而一条恒红的断言会在三天内被人删掉。
#  ★★ 为什么②那一端的扫描要**排除 FLOOR 自己**:FLOOR 正文里就有"记忆"两个字
#    (它正是被看门的那半句)。把它算进"碰了记忆",这条断言会咬自己的尾巴。
# ══════════════════════════════════════════════════════════════════════════════
NOT_WIRED_YET = "记忆库存在,但目前还没有接到对话上"
MEM_TOKENS = ("memory", "recall", "记忆", "remember", "回忆")

_code = "\n".join((
    assert_helpers.code_only(gateway.chat_completions),
    assert_helpers.code_only(system_prompt),
    assert_helpers.code_only(persona),
))
# 注入出去的、**除底线之外**的正文(今天只有人设)
_wire_not_floor = "\n".join(c for c in _contents if c != FLOOR)

_code_hits = [t for t in MEM_TOKENS if t in _code]
_wire_hits = [t for t in MEM_TOKENS if t in _wire_not_floor]
memory_wired = bool(_code_hits or _wire_hits)
floor_says_not_wired = NOT_WIRED_YET in FLOOR

check("FLOOR 第 1 条那半句本身还在(成对断言的另一端)", floor_says_not_wired or memory_wired,
      "半句被删了,但记忆还没接上 —— 底线在替一条不存在的能力背书")
check("记忆是否接上 ⇔ FLOOR 第 1 条那半句是否已删",
      memory_wired != floor_says_not_wired,
      f"代码端命中={_code_hits} 上线端命中={_wire_hits} / FLOOR 仍写着没接={floor_says_not_wired};"
      "★ 记忆接上对话的那一次改动里,必须同时删掉 FLOOR 第 1 条括号里那半句")

# ★ 元断言:上面那条判据的"代码端"必须真的取到了源码。取空的话它会**恒绿** ——
#   而恒绿的看门断言与没有断言是同一件事(assert_helpers.lock_bodies 顶上那段同款陷阱)。
check("成对断言的代码端确实取到了源码", len(_code) > 500, f"只取到 {len(_code)} 字")
check("成对断言的代码端确实覆盖了 chat 路径",
      "chat_completions" in assert_helpers.code_only(gateway.chat_completions))

print(f"=== 行为底线与人设层:{_pass} PASS · {_fail} FAIL ===")
raise SystemExit(1 if _fail else 0)
