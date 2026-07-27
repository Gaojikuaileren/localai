# P1 overnight batch results

运行开始(过夜自主批处理)。每项独立 try/catch,脚本内不关机。

**开跑前状态**
```
baseline    : 1281 MiB
pstate/clock: P8, 637 MHz
```

## B1 · KV cache 实测(8B · F16 vs q8_0 · 8K/16K/32K)

| ctx | F16 峰值Δ | q8_0 峰值Δ | F16 KV | q8_0 KV |
|---|---|---|---|---|
| 8K | 5975 MiB | 5439 MiB | - | - |
| 16K | 7135 MiB | 6059 MiB | 1160 MiB (Δ) | 620 MiB (Δ) |
| 32K | 9455 MiB | 7363 MiB | 2320 MiB (Δ) | 1304 MiB (Δ) |

> 阈值:16K KV ≤ 1.5 GiB / 32K KV ≤ 2.8 GiB。KV = 相邻 ctx 峰值之差外推。

## C1 · daily 吞吐(8B@16K · 生成 512 · r=5)
  (c1) exit= -- see c1.err
> 阈值:tg ≥ 30 tok/s

## B2 · prefill 首 token(8B · pp @ 8K/16K/32K · r=3)

| ctx | pp tok/s | 推算首token延迟 |
|---|---|---|
  (b2-8192) exit= -- see b2-8192.err
  (b2-16384) exit= -- see b2-16384.err
  (b2-32768) exit= -- see b2-32768.err
> 阈值:8K < 300ms(语音)/ 16K < 1.5s / 32K < 5s

## B3a · 长上下文衰减(8B · 浅 vs 深 tg)
  (b3-shallow) exit= -- see b3-shallow.err
  (b3-deep) exit= -- see b3-deep.err

## B3b · q8_0 vs F16 检索准确率(needle-in-haystack)
