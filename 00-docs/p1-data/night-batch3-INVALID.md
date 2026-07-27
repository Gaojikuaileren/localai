# P1 overnight batch #3 (detached, self-shutdown) results

HEARTBEAT 启动 · 2026-07-27 02:45:52
本批(独立进程,不随会话死):B3b · A3+C3 · E5 · VLM。结束自动 save+commit+cleanup+shutdown。
```
baseline: 1286 MiB · pstate: P3
```

## B3b · q8_0 vs F16 检索准确率(needle · q8_0 能否作默认的质量闸)
  F16 : 0 / 5 命中
  q8_0 : 0 / 5 命中
> 判据:q8_0 相对 F16 下降 <= 5 个百分点(5 样本方向性)。这条决定 B1/A2 挖出的 q8_0 能否作默认。

## A3 + C3 · 30B-A3B 可行性 / offload / 吞吐(q8_0 KV)
  层数 n_layer = 48

| ngl | 装载 | GPU驻留Δ | offload | tok/s(tg) |
|---|---|---|---|---|
| 48 | FAIL | - | - | - |
| 36 | FAIL | - | - | - |
| 26 | FAIL | - | - | - |
> 阈值:GPU 驻留 <= 12 GiB(AI 侧上限)且 tok/s >= 15。任一不达标 -> 30B-A3B 出局。

## E5 · Vigil 脉冲(1.7B · 纯 CPU · 160 token)
  单脉冲 wall clock : 90.1 s   (阈值 < 20 s)
  脉冲期间显存增量  : 323 MiB   (硬断言应为 0)
  核心秒(上界)      : 1441  (日阈值 5000)

## A2 · vlm.small 行 · Qwen2.5-VL-3B(候选,可换)
