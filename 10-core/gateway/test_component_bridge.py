"""P4-S2 · 词表桥测试(别名 ↔ 显存组件)。纯 assert:python test_component_bridge.py

★★ 这座桥存在的理由:网关按**别名**路由(`assistant.fast`),显存闸按**组件 id** 记账
  (`llm.assistant.8b@16k`)。2026-08-04 实测两套词表**零交集** —— 网关源码里连
  `component` 这个概念都没有。没有桥,Broker 就答不出「要服务这个别名,得让哪个组件驻留」,
  而那是 P4 的全部前提。

★★ 必须是【声明】不是【推导】。实测:按 `"llm.assistant." + contract` 拼组件 id,
  5 个聊天别名里**只有 2 个**拼得对(contract 列混着四套后缀约定),其余得到不存在的 id。
  本文件第 5 组把这件事钉成断言 —— 免得将来有人"顺手"改成推导。
"""

import sys
import tomllib
from pathlib import Path

import gateway

_pass = _fail = 0


def check(name, cond, extra=""):
    global _pass, _fail
    if cond:
        _pass += 1
    else:
        _fail += 1
        print(f"  FAIL  {name} {extra}")


_REPO = Path(__file__).resolve().parents[2]
with open(_REPO / "config" / "vram-budget.toml", "rb") as f:
    COMPONENTS = set(tomllib.load(f)["components"])
with open(_REPO / "10-core" / "gateway" / "registry.toml", "rb") as f:
    _reg_raw = tomllib.load(f)
ALIASES = _reg_raw["aliases"]
UNALIASED = _reg_raw.get("unaliased", {})

print("=== 1. 每个别名都必须显式声明 components_any_of ===")
for n, a in ALIASES.items():
    check(f"{n} 有 components_any_of", "components_any_of" in a)
    check(f"{n} 是数组", isinstance(a.get("components_any_of"), list))

print("=== 2. ★ 零显存别名必须写 no_gpu_reason(省略 = fail-open)===")
for n, a in ALIASES.items():
    if not a.get("components_any_of"):
        check(f"{n} 空表配了理由", str(a.get("no_gpu_reason", "")).strip() != "",
              "空表是一个【判断】,必须留下依据供人复核")

print("=== 3. 引用的组件 id 必须逐字存在 ===")
for n, a in ALIASES.items():
    for c in a.get("components_any_of", []):
        check(f"{n} → {c} 存在于 vram-budget.toml", c in COMPONENTS,
              "拼错一个字,Broker 会以为这个别名不需要任何组件")

print("=== 4. ★★ 反向全表:每个组件要么被别名覆盖,要么在 [unaliased] 里写明理由 ===")
#   正向只管"别名引用的组件存不存在";事故的形状是**新增的组件没人用却在参与算术**。
_covered = {c for a in ALIASES.values() for c in a.get("components_any_of", [])}
_orphan = sorted(COMPONENTS - _covered - set(UNALIASED))
check(f"没有孤儿组件(实测 orphan={_orphan})", not _orphan,
      "一个『谁都不用』的组件要么是欠账、要么是该删,不该无声地躺在预算表里参与算术")
_stale = sorted(set(UNALIASED) - COMPONENTS)
check(f"[unaliased] 里没有已不存在的组件(实测 stale={_stale})", not _stale,
      "留着会掩盖下一次真的孤儿")
for c, reason in UNALIASED.items():
    check(f"[unaliased] 的 {c} 写了理由", str(reason).strip() != "")
check("★ 今天确有未被别名覆盖的组件,且已登记(speech 两档 —— P5 未接)",
      set(UNALIASED) == {"speech.lite", "speech.full"}, str(set(UNALIASED)))

print("=== 5. ★★ 必须是声明,不能改成从 contract 推导 ===")
#   把"推导会错"这件事钉成可执行的证据,而不是注释里的一句话。
_derivable, _broken = [], []
for n, a in ALIASES.items():
    if a.get("kind") not in ("chat", "chat_multimodal"):
        continue
    guess = "llm.assistant." + str(a.get("contract", ""))
    (_derivable if guess in COMPONENTS else _broken).append(n)
check(f"★ 按 contract 拼 id 会拼错大多数别名(拼对 {len(_derivable)} / 拼错 {len(_broken)})",
      len(_broken) > len(_derivable),
      f"拼对={_derivable} 拼错={_broken} —— 若哪天全拼得对,也仍不该改推导:"
      f"contract 是给人看的契约字符串,不是 id")
check("拼错的里包含 assistant.fast(contract='8b' 不是任何组件 id)", "assistant.fast" in _broken)

print("=== 6. 启动期强制:五条 fail-closed 真的会拒绝启动 ===")
import copy


def _expect_refuse(mutate, why, reason_word=""):
    aliases = copy.deepcopy(ALIASES)
    mutate(aliases)
    try:
        gateway._check_component_bridge(aliases)
    except gateway.RegistryError as e:
        if reason_word and reason_word not in str(e):
            check(f"{why}(理由词 '{reason_word}')", False, str(e)[:120])
            return
        check(why, True)
        return
    except Exception as e:                                   # noqa: BLE001
        check(why, False, f"抛了别的异常 {type(e).__name__}")
        return
    check(why, False, "居然通过了")


_expect_refuse(lambda al: al["assistant.fast"].pop("components_any_of"),
               "缺 components_any_of → 拒绝启动", "缺少必填")
_expect_refuse(lambda al: al["assistant.fast"].__setitem__("components_any_of", "8b"),
               "components_any_of 不是数组 → 拒绝启动", "必须是数组")
_expect_refuse(lambda al: (al["assistant.fast"].__setitem__("components_any_of", []),
                           al["assistant.fast"].pop("no_gpu_reason", None)),
               "★ 空表却没写 no_gpu_reason → 拒绝启动(fail-open 的入口)", "no_gpu_reason")
_expect_refuse(lambda al: al["assistant.fast"]["components_any_of"].append("llm.bogus@99k"),
               "引用不存在的组件 → 拒绝启动", "不存在的组件")
#   反向全表:把覆盖 vlm.small 的唯一别名改走,它就成了孤儿。
#   ★ 变异必须**同时**补上 no_gpu_reason —— 否则会先撞上第③条(空表缺理由),
#     走不到孤儿检查。这一点本身也证明五条是【按顺序逐条】把关的,不是一个笼统的 try。
def _orphan_vlm(al):
    al["assistant.vision"]["components_any_of"] = []
    al["assistant.vision"]["no_gpu_reason"] = "变异:假装它不需要显存"


_expect_refuse(_orphan_vlm,
               "★★ 组件变成孤儿(无别名且未登记 unaliased)→ 拒绝启动", "没有任何别名驱动")

print("=== 7. 导出函数:一个方向声明,另一个方向导出 ===")
check("components_for_alias 返回声明值",
      gateway.components_for_alias("assistant.voice") == ["llm.assistant.8b@8k"],
      str(gateway.components_for_alias("assistant.voice")))
check("★ assistant.fast 是 any_of 三档(D25:上下文由实际装载决定,别名不钉死)",
      len(gateway.components_for_alias("assistant.fast")) == 3,
      str(gateway.components_for_alias("assistant.fast")))
check("aliases_for_component 由代码导出(8b@8k 被 fast 与 voice 共用)",
      gateway.aliases_for_component("llm.assistant.8b@8k") == ["assistant.fast", "assistant.voice"],
      str(gateway.aliases_for_component("llm.assistant.8b@8k")))
check("未被覆盖的组件导出为空表", gateway.aliases_for_component("speech.lite") == [])
check("★ 反向映射【不在 toml 里再声明一遍】(声明一处、导出一处,不可能打架)",
      "aliases_any_of" not in str(_reg_raw) and "used_by" not in str(_reg_raw))
check("未知别名不被当成『不需要显存』", gateway.components_for_alias("nonexistent.alias") == [])

print("=== 8. 桥不改变既有路由行为(本片只加声明,不动 chat 路径)===")
check("别名数量未变", len(gateway.REGISTRY) == 10, str(len(gateway.REGISTRY)))
check("egress 判定未受影响", gateway.REGISTRY["escalate.cloud"]["egress"] is True)
check("local_only 判定未受影响", gateway.REGISTRY["assistant.resident"]["local_only"] is True)
check("路由仍全部已归类", gateway.unclassified_routes() == [])

print(f"\n=== 词表桥:{_pass} PASS · {_fail} FAIL ===")
sys.exit(1 if _fail else 0)
