"""向量轨的底座:编码 · 编码指纹 · 双 Qdrant 路由(§4.2)

三件事,每件都有一条它独有的失效模式:

★★ 一、**编码指纹**(本模块存在的首要理由)
   向量之间只有在【用同一套编码参数产生】时才可比。改了模型、改了 query/passage 前缀、
   改了维度或归一化方式之后,**老向量与新查询就不在同一个空间里** ——
   检索不会报错,只会**悄悄变差**:召回率掉下去,而你以为只是"模型今天状态不好"。
   这是典型的静默劣化(§12.3 明令禁止)。
   → 把编码参数做成指纹存进 PG,启动时双向比对;不一致就**拒绝启动**并打印差异。

★★ 二、**载荷不带正文**
   Qdrant 里只放指针 {kind, row_id, write_seq, sensitivity},正文永远只在 PG。
   理由不是省空间:
     · 正文存两份 ⇒ D33② 的 tombstone 删除要同时删两处,漏一处就等于没删
     · S2 隔离要在两个系统里各做一遍,而 Qdrant 侧只有 api_key 这一层
     · 检索结果必须回 PG 取正文 ⇒ 天然经过 repo ⇒ 天然被 seal 成 TaintedText
   ⇒ 向量库泄露 = 泄露"哪条记忆存在、有多长",而不是记忆内容本身。

★ 三、**S2 走独立实例**(§4.11.4 结构性隔离)
   S0/S1 → mem_main(6333) · S2 → mem_s2(6335,独立进程+端口+api_key)。
   路由按 sensitivity_domain 决定,不是按 payload 过滤 ——
   「漏 collection 句柄是响亮的异常,漏 payload filter 是沉默的」。
"""
from __future__ import annotations

import hashlib
import json
import tomllib
from dataclasses import dataclass, asdict
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import httpx

from tainted import TaintedText, unseal_for_embedding

PATHS_TOML = Path(__file__).resolve().parents[2] / "config" / "paths.toml"

# ── 编码参数:改这里任何一项都会改变指纹,进而要求重建全部向量 ──────
EMBED_URL = "127.0.0.1:18084"
EMBED_MODEL = "BAAI/bge-m3"
DIM = 1024
DISTANCE = "Cosine"
# bge-m3 在检索场景下不需要 query/passage 前缀(与 e5 系列不同)。
# ★ 但把它显式写进指纹 —— 将来若有人加了前缀,指纹会变、启动会失败,
#   而不是让新查询与老向量悄悄错位。
QUERY_PREFIX = ""
PASSAGE_PREFIX = ""
NORMALIZE = True          # bge-m3 输出已归一化;显式记录以防将来换模型


class VectorError(RuntimeError):
    pass


class FingerprintMismatch(VectorError):
    """编码参数与库里已有向量不是同一套 —— 拒绝启动(fail-closed)。"""


@dataclass(frozen=True)
class EncodingFingerprint:
    model: str
    dim: int
    distance: str
    query_prefix: str
    passage_prefix: str
    normalize: bool

    @property
    def digest(self) -> str:
        blob = json.dumps(asdict(self), sort_keys=True, ensure_ascii=False)
        return hashlib.sha256(blob.encode("utf-8")).hexdigest()[:16]

    def diff(self, other: "EncodingFingerprint") -> List[str]:
        out = []
        for k, v in asdict(self).items():
            ov = asdict(other).get(k)
            if v != ov:
                out.append(f"{k}: 库里={ov!r} 当前={v!r}")
        return out


def current_fingerprint() -> EncodingFingerprint:
    return EncodingFingerprint(EMBED_MODEL, DIM, DISTANCE,
                               QUERY_PREFIX, PASSAGE_PREFIX, NORMALIZE)


def verify_fingerprint(conn) -> EncodingFingerprint:
    """启动期双向比对。首次运行则登记;不一致则**拒绝启动**。

    ★ 为什么必须 fail-closed:不一致时检索【不会报错】,只会悄悄变差。
      让它启动 = 让一个静默劣化的系统跑下去,而你无从察觉。
    """
    cur_fp = current_fingerprint()
    with conn.cursor() as c:
        c.execute("SELECT digest, params FROM mem.vector_space WHERE space_id='default'")
        row = c.fetchone()
        if row is None:
            c.execute(
                "INSERT INTO mem.vector_space (space_id, digest, params) VALUES ('default',%s,%s)",
                (cur_fp.digest, json.dumps(asdict(cur_fp), ensure_ascii=False)))
            conn.commit()
            return cur_fp
        stored_digest, stored_params = row[0], row[1]
    if stored_digest != cur_fp.digest:
        old = EncodingFingerprint(**(stored_params if isinstance(stored_params, dict)
                                     else json.loads(stored_params)))
        raise FingerprintMismatch(
            "编码参数与库里已有向量不是同一套 —— 拒绝启动。\n  " +
            "\n  ".join(cur_fp.diff(old)) +
            f"\n  库里 digest={stored_digest} 当前 digest={cur_fp.digest}\n"
            "  向量只有在【同一套参数下产生】时才可比。改了参数就必须重建全部向量,\n"
            "  否则检索不会报错、只会悄悄变差(§12.3 禁止静默降级)。"
        )
    return cur_fp


# ── 编码(唯一实现)────────────────────────────────────────────────
def _post(url: str, payload: dict, timeout: float = 120.0) -> dict:
    with httpx.Client(timeout=timeout) as cl:
        r = cl.post(url, json=payload)
        r.raise_for_status()
        return r.json()


def encode_texts(texts: List[str], *, is_query: bool = False) -> List[List[float]]:
    """编码的**唯一**实现。两处调用(写入时 / 检索时)必须共用它,否则前缀会漂移。"""
    if not texts:
        return []
    prefix = QUERY_PREFIX if is_query else PASSAGE_PREFIX
    payload = {"input": [prefix + t for t in texts]}
    data = _post(f"http://{EMBED_URL}/v1/embeddings", payload)
    vecs = [d["embedding"] for d in data["data"]]
    for v in vecs:
        if len(v) != DIM:
            raise VectorError(f"编码维度 {len(v)} 与指纹 {DIM} 不符")
    return vecs


def encode_tainted(t: TaintedText, *, is_query: bool = False) -> List[float]:
    """密封正文的编码入口 —— 经具名解封点,会记账(§4.6.1)。"""
    raw = unseal_for_embedding(t, endpoint=EMBED_URL)
    return encode_texts([raw], is_query=is_query)[0]


def rerank(query: str, docs: List[str], top_n: int = 8) -> List[Tuple[int, float]]:
    """重排。返回 [(原始下标, 分数)],已按分数降序。"""
    if not docs:
        return []
    data = _post(f"http://{EMBED_URL}/rerank",
                 {"query": query, "documents": docs, "top_n": top_n})
    return [(r["index"], float(r["score"])) for r in data["results"]]


# ── 双 Qdrant 路由 ────────────────────────────────────────────────
def _paths() -> Dict[str, Any]:
    with open(PATHS_TOML, "rb") as f:
        return tomllib.load(f)["memory"]


def _api_key(cfg_path: str) -> str:
    """从 qdrant config.yaml 读 api_key。

    ★ 必须剥掉行尾注释:配置里那行是
        api_key: <key>    # 无 SSPI 等价物,只能 bearer;...
      直接 split(':') 会把中文注释一起当成 key,塞进 HTTP header 立刻
      UnicodeEncodeError(header 只能 ASCII)。实测踩过。
    """
    txt = Path(cfg_path).read_text(encoding="utf-8")
    for line in txt.splitlines():
        s = line.strip()
        if not s.startswith("api_key:"):
            continue
        val = s.split(":", 1)[1]
        val = val.split("#", 1)[0].strip().strip('"').strip("'")   # 去注释与引号
        if not val:
            break
        if not val.isascii():
            raise VectorError(f"api_key 含非 ASCII 字符,解析多半出错了: {cfg_path}")
        return val
    raise VectorError(f"读不到 api_key: {cfg_path}")


@dataclass
class QdrantTarget:
    base: str
    api_key: str
    collection: str


def client_for(sensitivity: str) -> QdrantTarget:
    """★ 按 sensitivity 选**实例**,不是按 payload 过滤(§4.11.4)。

    漏了 payload filter 是沉默的失败;而这里若路由错了,拿到的 api_key 连不上另一个实例 ——
    是响亮的 401。**用结构把错误变吵。**
    """
    p = _paths()
    if sensitivity == "S2":
        return QdrantTarget(f"http://127.0.0.1:{p['qdrant_s2_http_port']}",
                            _api_key(p["qdrant_s2_config"]), "mem_s2")
    return QdrantTarget(f"http://127.0.0.1:{p['qdrant_http_port']}",
                        _api_key(p["qdrant_config"]), "mem_main")


def upsert_point(tgt: QdrantTarget, point_id: int, vector: List[float],
                 *, kind: str, row_id: int, write_seq: int, sensitivity: str) -> None:
    """写一个向量点。★ 载荷【只有指针】,绝无正文。"""
    payload = {"kind": kind, "row_id": row_id, "write_seq": write_seq,
               "sensitivity_domain": sensitivity}
    with httpx.Client(timeout=60.0) as cl:
        r = cl.put(f"{tgt.base}/collections/{tgt.collection}/points?wait=true",
                   headers={"api-key": tgt.api_key},
                   json={"points": [{"id": point_id, "vector": vector, "payload": payload}]})
        r.raise_for_status()


def search(tgt: QdrantTarget, vector: List[float], top_k: int = 50) -> List[Dict[str, Any]]:
    """ANN 检索。返回 [{id, score, payload}]。"""
    with httpx.Client(timeout=60.0) as cl:
        r = cl.post(f"{tgt.base}/collections/{tgt.collection}/points/search",
                    headers={"api-key": tgt.api_key},
                    json={"vector": vector, "limit": top_k, "with_payload": True})
        r.raise_for_status()
        return r.json()["result"]


def delete_point(tgt: QdrantTarget, point_id: int) -> None:
    """D33② tombstone 删除时必须同步删向量 —— 否则删了正文,向量还在,检索仍会命中。"""
    with httpx.Client(timeout=60.0) as cl:
        r = cl.post(f"{tgt.base}/collections/{tgt.collection}/points/delete?wait=true",
                    headers={"api-key": tgt.api_key}, json={"points": [point_id]})
        r.raise_for_status()


def assert_no_text_in_payload(payload: Dict[str, Any]) -> None:
    """架构断言:载荷里不得出现正文类字段。给测试与写路径共用。"""
    forbidden = {"text", "body", "statement", "content", "summary", "object"}
    bad = forbidden & set(payload)
    if bad:
        raise VectorError(f"向量载荷不得含正文字段: {sorted(bad)} —— 正文只存 PG")
