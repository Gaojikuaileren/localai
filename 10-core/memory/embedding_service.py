"""embedding / rerank 服务  ·  CPU  ·  127.0.0.1:18084  (§4.2)

记忆向量轨的 CPU 侧:bge-m3 生成 1024 维 dense 向量(与 Qdrant collection 维度对齐),
bge-reranker-v2-m3 做重排。网关已把 embedding.default / rerank.default 路由到本端口。

★ 状态:代码就绪,【尚未跑测】(需 2.3GB 模型 + torch,由 install-embedding.ps1 下载安装)。
  不像 E1 是实测过的。跑通后再在 worklog 标「已验证」。

接口:
  GET  /health
  POST /v1/embeddings   OpenAI 兼容:{input: str|[str], model} → {data:[{embedding,index}],...}
  POST /rerank          {query, documents:[str], top_n?} → {results:[{index,score,document?}]}

模型:环境变量 EMB_MODEL / RERANK_MODEL 覆盖;默认 BAAI/bge-m3 · BAAI/bge-reranker-v2-m3。
  HF_HOME 由 install 脚本从 paths.toml 的 [cache] hf 设,故本文件无硬编码路径。
"""
from __future__ import annotations

import os
from contextlib import asynccontextmanager
from typing import List, Optional, Union

from fastapi import FastAPI
from fastapi.responses import JSONResponse
from pydantic import BaseModel

EMB_MODEL = os.environ.get("EMB_MODEL", "BAAI/bge-m3")
RERANK_MODEL = os.environ.get("RERANK_MODEL", "BAAI/bge-reranker-v2-m3")
DIM = 1024  # bge-m3 dense 维度,须与 Qdrant mem_main/mem_s2 collection 一致

_state = {"emb": None, "rerank": None}


def _load():
    """惰性加载(启动就加载,避免首请求超时)。CPU、fp32。"""
    from FlagEmbedding import BGEM3FlagModel, FlagReranker
    if _state["emb"] is None:
        _state["emb"] = BGEM3FlagModel(EMB_MODEL, use_fp16=False)
    if _state["rerank"] is None:
        _state["rerank"] = FlagReranker(RERANK_MODEL, use_fp16=False)


@asynccontextmanager
async def lifespan(app: FastAPI):
    _load()
    yield
    _state["emb"] = None
    _state["rerank"] = None


app = FastAPI(title="LocalAI Hub Embedding/Rerank", version="0.1.0-p2", lifespan=lifespan)


class EmbedReq(BaseModel):
    input: Union[str, List[str]]
    model: Optional[str] = None


class RerankReq(BaseModel):
    query: str
    documents: List[str]
    top_n: Optional[int] = None
    return_documents: bool = False


@app.get("/health")
async def health():
    ready = _state["emb"] is not None and _state["rerank"] is not None
    return {"status": "ok" if ready else "loading", "dim": DIM,
            "emb_model": EMB_MODEL, "rerank_model": RERANK_MODEL}


@app.post("/v1/embeddings")
async def embeddings(req: EmbedReq):
    texts = [req.input] if isinstance(req.input, str) else list(req.input)
    if not texts:
        return JSONResponse(status_code=400, content={"error": "input 为空"})
    out = _state["emb"].encode(texts, batch_size=16, max_length=8192)["dense_vecs"]
    data = [{"object": "embedding", "index": i, "embedding": vec.tolist()}
            for i, vec in enumerate(out)]
    total = sum(len(t) for t in texts)
    return {"object": "list", "data": data, "model": EMB_MODEL,
            "usage": {"prompt_tokens": total, "total_tokens": total}}


@app.post("/rerank")
async def rerank(req: RerankReq):
    if not req.documents:
        return {"results": []}
    pairs = [[req.query, d] for d in req.documents]
    scores = _state["rerank"].compute_score(pairs, normalize=True)
    if not isinstance(scores, list):
        scores = [scores]
    ranked = sorted(enumerate(scores), key=lambda x: x[1], reverse=True)
    if req.top_n:
        ranked = ranked[: req.top_n]
    results = []
    for idx, score in ranked:
        item = {"index": idx, "score": float(score)}
        if req.return_documents:
            item["document"] = req.documents[idx]
        results.append(item)
    return {"results": results, "model": RERANK_MODEL}


if __name__ == "__main__":
    # 自测:python embedding_service.py --selftest(需模型已下载)
    import sys
    if "--selftest" in sys.argv:
        _load()
        v = _state["emb"].encode(["你好世界"])["dense_vecs"][0]
        print(f"embed dim = {len(v)} (期望 {DIM})")
        s = _state["rerank"].compute_score([["猫", "一只猫"], ["猫", "一辆车"]], normalize=True)
        print(f"rerank scores = {s} (第一个应更高)")
