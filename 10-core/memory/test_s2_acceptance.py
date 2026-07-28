"""S2 验收:第二条例句端到端 + 编码指纹 + 载荷不带正文。
跑(需活库 + Qdrant + embedding):python test_s2_acceptance.py

  写入几条情节 → 问「上次聊的那个灯光问题」
    → 选路必须走【向量轨】
    → 命中金标情节(而不是无关的那几条)
    → 向量轨【不产出 answer】,只产出 passages
"""
import sys
from datetime import datetime, timedelta, timezone

import repo
import route
import track_vector
import vectors
from repo import EpisodeWrite
from route import Route
from tainted import TaintedText, seal, unseal_for_client, CallerTier

_p = _f = 0
TAG = "s2acc"


def check(name, cond, extra=""):
    global _p, _f
    if cond:
        _p += 1
        print(f"  PASS  {name}")
    else:
        _f += 1
        print(f"  FAIL  {name} {extra}")


try:
    conn = repo.connect()
except Exception as ex:
    print(f"  跳过:连不上 PG({type(ex).__name__})")
    sys.exit(0)

GOLD = f"{TAG} 我们讨论了客厅灯光太暗,考虑换成暖光灯带,色温 2700K"
NOISE = [
    f"{TAG} 我妹妹叫小雨,她在慕尼黑读书",
    f"{TAG} 今天买了牛奶和面包,超市在打折",
    f"{TAG} UE5 的 Nanite 在这个场景下掉帧,可能要关掉",
    f"{TAG} 明天下午三点有个会,记得提醒我",
]
now = datetime.now(timezone.utc)

try:
    # 清理旧数据
    with conn.cursor() as cur:
        cur.execute("SELECT id FROM mem.l2_episode WHERE body LIKE %s", (f"{TAG}%",))
        old = [r[0] for r in cur.fetchall()]
    for oid in old:
        try:
            vectors.delete_point(vectors.client_for("S0"), oid)
        except Exception:
            pass
    with conn.cursor() as cur:
        cur.execute("DELETE FROM mem.l2_episode WHERE body LIKE %s", (f"{TAG}%",))
    conn.commit()

    print("=== ① 编码指纹(启动期 fail-closed)===")
    fp = vectors.verify_fingerprint(conn)
    check("指纹校验通过并登记", fp.digest and len(fp.digest) == 16, fp.digest)
    check("指纹含模型/维度/前缀/归一化",
          all(k in vectors.asdict(fp) for k in
              ("model", "dim", "distance", "query_prefix", "passage_prefix", "normalize")))
    # 模拟参数漂移:库里存的与当前不一致 → 必须拒绝启动
    with conn.cursor() as cur:
        cur.execute("UPDATE mem.vector_space SET digest='deadbeefdeadbeef' WHERE space_id='default'")
    conn.commit()
    try:
        vectors.verify_fingerprint(conn)
        check("★ 指纹不一致必须拒绝启动", False, "竟然放行了")
    except vectors.FingerprintMismatch as ex:
        check("★ 指纹不一致 → 拒绝启动(fail-closed)", True)
        check("拒绝时给出差异与理由", "digest" in str(ex) and "重建" in str(ex))
    # 还原
    with conn.cursor() as cur:
        cur.execute("UPDATE mem.vector_space SET digest=%s WHERE space_id='default'", (fp.digest,))
    conn.commit()

    print("=== ② 写入情节并建向量索引 ===")
    ids = {}
    for i, text in enumerate([GOLD] + NOISE):
        w = EpisodeWrite(body=seal(text, sensitivity="S0", source="user_typed"),
                         event_at=now - timedelta(days=i * 2),
                         provenance="user_typed", source_confidence=0.6,
                         sensitivity_domain="S0", attestation_kind="assistant_infer",
                         source_ref={"kind": "flow", "session_id": "s2acc"})
        eid = repo.insert_episode(conn, w)
        conn.commit()
        with conn.cursor() as cur:
            cur.execute("SELECT write_seq FROM mem.l2_episode WHERE id=%s", (eid,))
            ws = cur.fetchone()[0]
        track_vector.index_episode(conn, eid, w.body, sensitivity="S0", write_seq=ws)
        conn.commit()
        ids[text] = eid
    check(f"写入 {len(ids)} 条情节并建索引", len(ids) == 5)

    print("=== ②b ★ 向量指针只能单向写(append-only 对索引列的例外)===")
    gold_id = ids[GOLD]
    # 幂等重建索引:写同一个值 —— 允许
    try:
        repo.set_episode_vector(conn, gold_id, gold_id)
        conn.commit()
        check("重建索引写同值 → 允许", True)
    except Exception as ex:
        conn.rollback()
        check("重建索引写同值 → 允许", False, f"{ex}")
    # ★ 重指到别的点 —— 必须拒绝(否则 tombstone 会删错点,正文没了向量还在)
    try:
        repo.set_episode_vector(conn, gold_id, gold_id + 99999)
        conn.commit()
        check("★★ 重指到别的向量点 → 必须拒绝", False, "竟然放行了")
    except repo.RepoError:
        conn.rollback()
        check("★★ 重指到别的向量点 → 必须拒绝", True)
    # 置 NULL(下架)→ 允许;再设回来 → 允许
    try:
        with conn.cursor() as cur:
            cur.execute("UPDATE mem.l2_episode SET vector_point_id=NULL WHERE id=%s", (gold_id,))
        conn.commit()
        repo.set_episode_vector(conn, gold_id, gold_id)
        conn.commit()
        check("置 NULL 下架再重设 → 允许(D33② 删除路径需要)", True)
    except Exception as ex:
        conn.rollback()
        check("置 NULL 下架再重设 → 允许(D33② 删除路径需要)", False, f"{ex}")
    # 正文仍然不可改
    try:
        with conn.cursor() as cur:
            cur.execute("UPDATE mem.l2_episode SET body='改写了' WHERE id=%s", (gold_id,))
        conn.commit()
        check("★ 正文仍然不可覆盖", False, "竟然放行了")
    except Exception:
        conn.rollback()
        check("★ 正文仍然不可覆盖", True)

    print("=== ③ 选路:必须走向量轨 ===")
    variants = ["上次聊的那个灯光问题", "我们之前讨论过的灯光问题",
                "你还记得那个灯光的事吗", "前几天说过的灯光"]
    for q in variants:
        d = route.route(q)
        check(f"「{q}」→ VECTOR_FIRST", d.route == Route.VECTOR_FIRST, f"→ {d.route.value}")

    print("=== ④ 向量轨检索:必须命中金标情节 ===")
    q = "上次聊的那个灯光问题"
    passages = track_vector.search(conn, q, sensitivity="S0")
    check("有返回", len(passages) > 0, f"{len(passages)}")
    if passages:
        top = passages[0]
        check("★★ 首条就是金标(灯光那条)", top.episode_id == ids[GOLD],
              f"实得 episode_id={top.episode_id}")
        body = unseal_for_client(top.body, caller=CallerTier.TRUSTED_LOCAL)
        check("首条正文确实讲灯光", "灯光" in body, body[:40])
        check("★ 返回的正文是密封的", isinstance(top.body, TaintedText))
        check("带溯源", "write_seq" in top.trace and "event_at" in top.trace)
        check("有衰减因子且 ≤1", 0 < top.decay <= 1.0, f"{top.decay}")

    print("=== ④b ★ 判别力:换个问题必须换命中(否则「总是第一」不算命中)===")
    # 只验证"灯光问题→灯光那条"是弱测试:若 rerank 坏掉、金标恰好排第一,照样过。
    # 真正的证据是【换个问题就换首条】。
    probes = [
        ("我妹妹在哪读书", "妹妹"),
        ("超市买了什么", "牛奶"),
        ("引擎掉帧怎么办", "Nanite"),
    ]
    for q2, want in probes:
        ps = track_vector.search(conn, q2, sensitivity="S0")
        top_body = unseal_for_client(ps[0].body, caller=CallerTier.TRUSTED_LOCAL) if ps else ""
        check(f"「{q2}」→ 首条含「{want}」", want in top_body, f"实得 {top_body[:34]}")
    ps = track_vector.search(conn, "我妹妹在哪读书", sensitivity="S0")
    check("★ 且此时首条不是灯光那条", ps and ps[0].episode_id != ids[GOLD])

    print("=== ④c tombstone:向量残留也不得泄正文(D33② fail-closed)===")
    # 拿噪声里那条「妹妹」开刀(不动金标,后面还要用)。
    # ★ 向量点【故意不删】—— 模拟"正文删了、向量清理失败"这个最难发现的半失败态。
    victim = ids[NOISE[0]]
    # ★★ 用【删除前后对比】证明是 tombstone 干的活,而不是靠"结果非空"。
    #   S5 给向量轨加了最低分闸(MIN_RERANK_SCORE)之后,查「我妹妹在哪读书」删掉妹妹那条,
    #   其余情节本就不相关、分数远低于闸,合法地返回空。所以旧断言「len>0」不再成立 ——
    #   那不是缺陷,是最低分闸在正确工作。改为:先证明删之前它确实被命中。
    ps_before = track_vector.search(conn, "我妹妹在哪读书", sensitivity="S0")
    check("★ 删除【前】妹妹那条确实被命中(否则本测试无说服力)",
          any(p.episode_id == victim for p in ps_before),
          f"删前返回 {[p.episode_id for p in ps_before]}")
    with conn.cursor() as cur:
        cur.execute("UPDATE mem.l2_episode SET redacted_at=now() WHERE id=%s", (victim,))
    conn.commit()
    ps = track_vector.search(conn, "我妹妹在哪读书", sensitivity="S0")
    check("★★ 正文被 tombstone 后,即便向量还在也检索不出来",
          all(p.episode_id != victim for p in ps), f"仍返回 {[p.episode_id for p in ps]}")
    # ★ 撤销删除必须被拒(与"悄悄复活旧值"是同一个失效模式)
    try:
        with conn.cursor() as cur:
            cur.execute("UPDATE mem.l2_episode SET redacted_at=NULL WHERE id=%s", (victim,))
        conn.commit()
        check("★★ 撤销 tombstone → 必须拒绝(D33② 恢复要走隔离区新增行)", False, "竟然放行了")
    except Exception:
        conn.rollback()
        check("★★ 撤销 tombstone → 必须拒绝(D33② 恢复要走隔离区新增行)", True)

    print("=== ⑤ ★ 向量轨不得产出 answer(静默答错的正门)===")
    check("Passage 类型上没有 answer 字段",
          "answer" not in track_vector.Passage.__dataclass_fields__,
          f"{set(track_vector.Passage.__dataclass_fields__)}")
    d = route.route(q)
    check("★ 即便走向量轨,answer 仍只能由结构化轨填", d.answer_allowed_from == "struct")

    print("=== ⑥ ★ 向量载荷不得带正文 ===")
    tgt = vectors.client_for("S0")
    hits = vectors.search(tgt, vectors.encode_texts([q], is_query=True)[0], top_k=3)
    check("检索到点", len(hits) > 0)
    for h in hits:
        pl = h.get("payload", {})
        check(f"点 {h['id']} 载荷只有指针",
              set(pl) <= {"kind", "row_id", "write_seq", "sensitivity_domain"}, f"{set(pl)}")
        check(f"点 {h['id']} 载荷无正文", "灯光" not in str(pl) and TAG not in str(pl))
    try:
        vectors.assert_no_text_in_payload({"kind": "episode", "body": "正文"})
        check("★ 架构断言能抓住带正文的载荷", False, "竟然放行")
    except vectors.VectorError:
        check("★ 架构断言能抓住带正文的载荷", True)

    print("=== ⑦ S2 走独立实例(结构性隔离)===")
    t_main, t_s2 = vectors.client_for("S0"), vectors.client_for("S2")
    check("S0 → mem_main", t_main.collection == "mem_main" and "6333" in t_main.base)
    check("S2 → mem_s2", t_s2.collection == "mem_s2" and "6335" in t_s2.base)
    check("★ 两实例 api_key 不同(路由错 = 响亮的 401)", t_main.api_key != t_s2.api_key)

    # ★★ 上面三条只验了【配置】。隔离本身要实测,否则"结构性隔离"只是注释里的一句话。
    import httpx as _hx
    wrong = vectors.QdrantTarget(t_s2.base, t_main.api_key, "mem_s2")   # 拿 main 的钥匙开 s2
    try:
        vectors.search(wrong, [0.0] * vectors.DIM, top_k=1)
        check("★★ 用 main 的 api_key 访问 s2 实例 → 必须被拒", False, "竟然放行了")
    except _hx.HTTPStatusError as ex:
        check("★★ 用 main 的 api_key 访问 s2 实例 → 必须被拒", ex.response.status_code in (401, 403),
              f"状态码 {ex.response.status_code}")

    # S2 内容写进 s2 实例后,在 main 实例里必须【根本不存在】(不是靠 payload 过滤挡住)
    probe_id = 990001
    vectors.upsert_point(t_s2, probe_id, vectors.encode_texts(["s2 隔离探针"])[0],
                         kind="episode", row_id=probe_id, write_seq=0, sensitivity="S2")
    try:
        with _hx.Client(timeout=30.0) as _c:
            r_s2 = _c.get(f"{t_s2.base}/collections/mem_s2/points/{probe_id}",
                          headers={"api-key": t_s2.api_key})
            r_mn = _c.get(f"{t_main.base}/collections/mem_main/points/{probe_id}",
                          headers={"api-key": t_main.api_key})
        check("探针在 s2 实例里存在", r_s2.status_code == 200, f"{r_s2.status_code}")
        check("★★ 同一 id 在 main 实例里不存在(物理隔离,非 payload 过滤)",
              r_mn.status_code == 404, f"{r_mn.status_code}")
    finally:
        try:
            vectors.delete_point(t_s2, probe_id)
        except Exception:
            pass

    print("=== ⑧ 清理 ===")
    for eid in ids.values():
        try:
            vectors.delete_point(tgt, eid)
        except Exception:
            pass
    with conn.cursor() as cur:
        cur.execute("DELETE FROM mem.l2_episode WHERE body LIKE %s", (f"{TAG}%",))
    conn.commit()
    check("测试数据已清", True)
finally:
    conn.close()

print(f"\n=== S2 验收:{_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
