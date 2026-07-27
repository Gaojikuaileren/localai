# P1 overnight batch #2 (resume) results

接续第一批(B1/C1/B2 数据已在磁盘)。本批:B3a(-d 正确测衰减) · B3b · A3+C3 · E5 · VLM。
★ 修复:RunBench 改为直接解析 JSON,不再信 ExitCode(上一批误杀了 C1/B2/B3a 的汇总)。
```
baseline    : 1282 MiB
pstate/clock: P8, 645 MHz
```

## B3a · 长上下文衰减(8B · 纯生成 @ 深度 0 vs 32000 · 用 -d)
  深度 0     tg : 151.3 tok/s
  深度 32000 tg : 79.2 tok/s
  比值          : 52%   (阈值 >= 60%)

## B3b · q8_0 vs F16 检索准确率(needle)
