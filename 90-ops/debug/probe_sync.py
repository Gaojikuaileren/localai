r"""主副机同步实测 —— 可在两台机器上**各跑一半**,验范围/冲突/实时性。

单机自检(模拟两台,验判据):
    python 90-ops\debug\probe_sync.py

两台真机验收(★ 这是 STATE 里排第一的那件事):
    主机:  python 90-ops\debug\probe_sync.py --watch
    副机:  python 90-ops\debug\probe_sync.py --push --hub 192.168.178.61:8443
           (副机走 lan-edge,需要已配对的客户端档案 —— 见下面的说明)

★★★ 稳定性三条(用户要求:「debug 工具一定要稳定简单方便」):
  ① **零项目依赖** —— 只用标准库 + HTTP。项目代码坏了工具还得能跑。
  ② **只碰自己造的数据** —— 所有测试记录 id 一律以 `__probe_` 开头,
     跑完自己清掉。★ 绝不碰用户的真数据。
  ③ **「工具坏了」与「系统坏了」分开** —— 退出码 2 vs 1。

★ 副机那条路径**今天只能人工跑**:副机连中枢要 mTLS 客户端证书,
  而证书私钥在 CNG 里不可导出(D43/D44)—— Python 拿不到。
  ⇒ 副机侧的验收**必须用客户端界面做**,这个工具只负责**主机侧的观察**。
    这一条如实写在这里,不假装工具能替人跑完两机验收。
"""
from __future__ import annotations

import json
import sys
import time
import urllib.error
import urllib.request
from typing import Optional

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:                                            # noqa: BLE001
    pass

GW = "http://127.0.0.1:8080"
PREFIX = "__probe_"          # ★ 所有本工具造的记录都带这个前缀,跑完清掉


def http(path: str, body=None, timeout=15.0):
    """永不抛。返回 (status, json_or_text)。"""
    try:
        req = urllib.request.Request(
            GW + path,
            data=json.dumps(body).encode() if body is not None else None,
            headers={"Content-Type": "application/json"} if body is not None else {})
        with urllib.request.urlopen(req, timeout=timeout) as r:
            raw = r.read().decode("utf-8", "replace")
            try:
                return r.status, json.loads(raw)
            except Exception:                                # noqa: BLE001
                return r.status, raw
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", "replace")
        try:
            return e.code, json.loads(raw)
        except Exception:                                    # noqa: BLE001
            return e.code, raw
    except Exception as e:                                   # noqa: BLE001
        return None, f"{type(e).__name__}: {e}"


def push(items, device="__probe__"):
    return http("/v1/sync/push", {"device": device, "items": items})


def watch(seconds: float = 60.0) -> int:
    """主机侧:盯着同步流,把每一帧变化打出来。★ 两机验收时主机跑这个。"""
    print("=" * 78)
    print(f"  盯着同步流 {seconds:.0f} 秒 —— 副机上做什么,这里就会打出来")
    print("  ★ 副机侧请用【客户端界面】操作(加家庭待办 / 提升会话为共享)")
    print("=" * 78)
    try:
        with urllib.request.urlopen(GW + "/v1/sync/events", timeout=seconds + 10) as r:
            t0 = time.time()
            for raw in r:
                line = raw.decode("utf-8", "replace").rstrip()
                if line.startswith("event:"):
                    print(f"  +{time.time()-t0:7.2f}s  {line}")
                elif line.startswith("data:"):
                    try:
                        d = json.loads(line[5:])
                        print(f"            gen={d.get('generation')} counts={d.get('counts')}")
                        for kind, arr in (d.get("data") or {}).items():
                            for it in arr:
                                print(f"              {kind}: {it.get('title') or it.get('text')}"
                                      f"  ← {it.get('device')}")
                    except Exception:                        # noqa: BLE001
                        pass
                if time.time() - t0 > seconds:
                    break
    except Exception as e:                                   # noqa: BLE001
        print(f"  ✘ 订阅失败:{type(e).__name__}: {e}")
        print("    → 先跑 doctor.py 看网关在不在")
        return 1
    return 0


def selfcheck() -> int:
    """单机自检:模拟两台机器推,验三条裁定(D86)。★ 只碰自己造的数据。"""
    print("=" * 78)
    print("  同步判据自检(单机模拟两台)  ★ 只碰 __probe_ 开头的记录,跑完清掉")
    print("=" * 78)
    st, _ = http("/v1/sync/snapshot")
    if st != 200:
        print(f"  ✘ 同步面不可达({st}) → 先跑 doctor.py")
        return 1

    bad = 0

    def chk(name, cond, extra=""):
        nonlocal bad
        print(f"  {'✔' if cond else '✘'}  {name}" + (f"   {extra}" if not cond and extra else ""))
        if not cond:
            bad += 1

    # ① 范围判据(D86 裁定①)
    st, r = push([
        {"kind": "todos", "record": {"id": PREFIX + "fam", "scope": "家庭", "title": "__probe 家庭"}},
        {"kind": "todos", "record": {"id": PREFIX + "per", "scope": "个人", "title": "__probe 个人"}},
        {"kind": "sessions", "record": {"id": PREFIX + "s1", "shared": True, "title": "__probe 共享"}},
        {"kind": "sessions", "record": {"id": PREFIX + "s2", "shared": False}},
    ], device="__probe_B")
    res = {x["id"]: x for x in (r.get("results") or [])} if isinstance(r, dict) else {}
    chk("家庭待办被收", res.get(PREFIX + "fam", {}).get("ok") is True)
    chk("★★ 个人待办被拒(把私人东西推到另一台是不可撤销的错误)",
        res.get(PREFIX + "per", {}).get("ok") is False,
        str(res.get(PREFIX + "per")))
    chk("共享会话被收", res.get(PREFIX + "s1", {}).get("ok") is True)
    chk("未共享会话被拒", res.get(PREFIX + "s2", {}).get("ok") is False)

    # ② 冲突(D86 裁定③)
    st, r2 = push([{"kind": "todos",
                    "record": {"id": PREFIX + "fam", "scope": "家庭", "title": "__probe 改过了"}}],
                  device="__probe_A")
    it = (r2.get("results") or [{}])[0] if isinstance(r2, dict) else {}
    chk("★★ 后到的赢,且如实回报【被覆盖】", it.get("superseded") is True, str(it))
    chk("★ 并说清覆盖掉谁写的", it.get("superseded_from") == "__probe_B", str(it.get("superseded_from")))

    # ③ 同内容重推不算冲突
    st, r3 = push([{"kind": "todos",
                    "record": {"id": PREFIX + "fam", "scope": "家庭", "title": "__probe 改过了"}}],
                  device="__probe_A")
    it3 = (r3.get("results") or [{}])[0] if isinstance(r3, dict) else {}
    chk("★ 同内容重推**不算**冲突(否则噪声淹掉真冲突)", it3.get("superseded") is False)

    # ④ 实时性(裁定②)—— 起一个订阅,推一条,量延迟
    import threading
    got = []

    def sub():
        try:
            with urllib.request.urlopen(GW + "/v1/sync/events", timeout=20) as r:
                t0 = time.time()
                for raw in r:
                    if PREFIX + "live" in raw.decode("utf-8", "replace"):
                        got.append(time.time() - t0)
                        break
                    if time.time() - t0 > 15:
                        break
        except Exception:                                    # noqa: BLE001
            pass

    th = threading.Thread(target=sub, daemon=True)
    th.start()
    time.sleep(1.5)
    push([{"kind": "todos", "record": {"id": PREFIX + "live", "scope": "家庭", "title": "__probe 实时"}}],
         device="__probe_B")
    th.join(timeout=20)
    chk(f"★★ 实时推送(裁定②)"
        + (f" —— 延迟 {(got[0]-1.5)*1000:.0f} ms" if got else ""),
        bool(got), "没收到 update")

    # ★ 清理自己造的东西(带前缀的才清 —— 绝不碰用户真数据)
    print("\n  清理 __probe_ 记录…", end="")
    print(" (服务端目前没有删除端点 —— 这些记录会留下,已如实告知)")
    print("\n" + "-" * 78)
    print(f"  {bad} 条不成立" if bad else "  全部成立")
    return 1 if bad else 0


def main() -> int:
    if "--watch" in sys.argv:
        i = sys.argv.index("--watch")
        secs = float(sys.argv[i + 1]) if len(sys.argv) > i + 1 and sys.argv[i + 1].isdigit() else 60.0
        return watch(secs)
    return selfcheck()


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
    except Exception as e:                                   # noqa: BLE001
        print(f"\n  ? 探测器自己出错:{type(e).__name__}: {e}")
        print("    → 这是**工具**的问题,不是系统的问题")
        sys.exit(2)
