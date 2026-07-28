"""选路器测试。跑:python test_route.py

★ 每一组都对应对抗性核验里点名的一个失败模式(2026-07-28,选路维度 18 条 FAIL)。
  验收硬卡在两条例句上,所以它们各有一整组变体测试。
"""
import sys
from route import route, Route, normalize, lexicon

_p = _f = 0


def check(name, cond, extra=""):
    global _p, _f
    if cond:
        _p += 1
    else:
        _f += 1
        print(f"  FAIL: {name} {extra}")


def r(q):
    return route(q)


print("=== ★ 验收例句 A:必须走结构化轨(向量轨答不出名字)===")
for q in [
    "我妹妹叫什么名字",          # ★ 原设计在这句上纸面即失败(集合成员判定不命中「叫什么名字」)
    "我妹妹叫什么",
    "妹妹的名字是什么",
    "我妹叫啥",
    "我妹妹叫啥来着",
    "我姐姐叫什么名字",
    "我二姐叫什么名字",          # ★ 排行称谓 —— 复用 E1 归一化会把「二姐」变「2姐」而漏掉
    "我三妹叫什么名字",
    "老三叫什么名字",
    "我爸爸叫什么名字",
    "我老婆叫什么",
]:
    d = r(q)
    check(f"A:{q}", d.route == Route.STRUCT_FIRST, f"→ {d.route.value}/{d.rule_id}")

print("=== ★ 验收例句 A 的德/英变体(用户在德语环境,中德混说是常态)===")
for q in [
    "Wie heißt meine Schwester",
    "Wie heisst meine Schwester",     # 无 ß 的常见写法
    "wie heißt meine schwester?",
    "what's my sister's name",
    "what is the name of my sister",
    "whats the name of my sister",
]:
    d = r(q)
    check(f"A(外语):{q}", d.route == Route.STRUCT_FIRST, f"→ {d.route.value}/{d.rule_id}")

print("=== ★ 验收例句 B:必须走向量轨 ===")
for q in [
    "上次聊的那个灯光问题",
    "我们之前讨论过的灯光问题",
    "你还记得那个灯光的事吗",
    "前几天说过的灯光",
    "letztes mal über das Licht",
    "the lighting thing we talked about last time",
]:
    d = r(q)
    check(f"B:{q}", d.route == Route.VECTOR_FIRST, f"→ {d.route.value}/{d.rule_id}")

print("=== ★ 归一化【不得】把中文数字转掉(与 E1 的 normalize 刻意不共用)===")
for s in ["二姐", "三妹", "老三", "两口子", "第二期项目"]:
    check(f"normalize 保住 {s}", normalize(s) == s, f"→ {normalize(s)!r}")
# 对照:E1 的 normalize 确实会转 —— 证明「不共用」是必要的而非洁癖
try:
    sys.path.insert(0, str(__import__("pathlib").Path(__file__).resolve().parents[1] / "gateway"))
    from e1_detector import normalize as e1n
    check("★ 对照:E1 的 normalize 确实会毁掉「二姐」", e1n("二姐") != "二姐", f"E1→{e1n('二姐')!r}")
except Exception:
    check("(E1 对照跳过)", True)

print("=== 子串匹配而非集合成员(原设计的纸面失败点)===")
d = r("我妹妹叫什么名字")
check("命中的是最长片段「叫什么名字」", d.signals["attribute_q"] == "叫什么名字", f"→ {d.signals['attribute_q']!r}")
check("关系词命中「妹妹」", d.signals["relation"] == "妹妹", f"→ {d.signals['relation']!r}")

print("=== ★ 默认必须扇出,不得默认单轨向量 ===")
for q in ["帮我总结今天的会议", "随便聊聊", "asdfghjkl", "", "Hallo", "今天天气怎么样"]:
    d = r(q)
    check(f"无强信号 → BOTH:{q!r}", d.route == Route.BOTH, f"→ {d.route.value}/{d.rule_id}")

print("=== ★ answer 只能由结构化轨填(静默答错的正门)===")
for q in ["我妹妹叫什么名字", "上次聊的那个灯光问题", "随便聊聊"]:
    check(f"answer_allowed_from=struct:{q}", r(q).answer_allowed_from == "struct")

print("=== 属性疑问优先于情节指示(用户要的是那个值,不是那次对话)===")
d = r("上次你说我妹妹叫什么来着")
check("关系+属性 压过 情节 → STRUCT_FIRST", d.route == Route.STRUCT_FIRST, f"→ {d.route.value}/{d.rule_id}")

print("=== ★ 纯函数:同输入同输出,且不依赖库里的数据 ===")
outs = {(r("我妹妹叫什么名字").route, r("我妹妹叫什么名字").rule_id) for _ in range(50)}
check("50 次调用结果恒定", len(outs) == 1)
import inspect, route as rt
src = inspect.getsource(rt)
for bad in ["psycopg", "connect(", "requests.", "httpx", "SELECT ", "cursor"]:
    check(f"route.py 不含 {bad!r}(不碰 IO)", bad not in src)

print("=== 无关系词但有属性疑问 → 仍走结构化(要确切值)===")
for q in ["生日是几号", "他住在哪", "wo wohnt sie"]:
    d = r(q)
    check(f"{q} → STRUCT_FIRST", d.route == Route.STRUCT_FIRST, f"→ {d.route.value}/{d.rule_id}")

print("=== 词表健康度 ===")
lx = lexicon()
check("relation 三语都有(≥60 条)", len(lx.relation) >= 60, f"{len(lx.relation)}")
check("attribute_q 三语都有(≥30 条)", len(lx.attribute_q) >= 30, f"{len(lx.attribute_q)}")
check("episodic 三语都有(≥25 条)", len(lx.episodic) >= 25, f"{len(lx.episodic)}")
check("attribute_q 按长度降序(保证最长匹配)",
      lx.attribute_q == sorted(lx.attribute_q, key=len, reverse=True))

print(f"\n=== {_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
