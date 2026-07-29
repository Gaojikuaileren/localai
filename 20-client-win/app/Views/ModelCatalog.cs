// P3c -- 模型清单(客户端侧的占位清单,按【角色】列出,不含实测显存数字)。
//
// ★ 诚实与不重复:显存 peak 的唯一事实来源是主机的 config/vram-budget.toml(纪律:数字不散落进代码)。
//   这里只列用户看得懂的【角色】名,供"系统 › 模型"里做启用/规则的偏好设置;
//   接入 GPU Broker(P4)后,以【中枢下发的可用模型清单】为准替换这份占位,并由 Broker 按显存预算实际装载。

namespace LocalAI.Client.Views;

public static class ModelCatalog
{
    public sealed record Def(string Key, string Name, string Role);

    public static readonly Def[] All =
    {
        new("chat.8b",      "日常对话 · 8B",        "常驻主力"),
        new("chat.8b.long", "长上下文 · 8B / 32K",  "长文档"),
        new("chat.30b",     "深度模式 · 30B-A3B",   "难题深想(显存吃紧)"),
        new("speech",       "语音 · 听写 + 朗读",   "ASR + TTS"),
        new("vlm",          "视觉 · 看图理解",      "VLM"),
        new("image",        "绘图 · SDXL",          "出图"),
    };

    /// <summary>自动启用预设的可选项(与 vram-budget.toml 的 presets 对应;标签仅显示用)。</summary>
    public static readonly (string Key, string Label)[] Presets =
    {
        ("none",         "不自动启用"),
        ("daily",        "日常(8B + 语音)"),
        ("long_context", "长上下文(8B/32K)"),
        ("deep",         "深度模式(30B-A3B)"),
        ("vision",       "视觉(8B + VLM)"),
    };
}
