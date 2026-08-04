// P3c/P4-S9 -- 只剩【自动启用预设】的标签。
//
// ★★ 2026-08-04(P4-S9)删掉了这里的 `Def[] All`(chat.8b / chat.8b.long / chat.30b /
//   speech / vlm / image)。那是客户端自造的**第三套词汇** —— 跟网关别名(chat.default…)
//   对不上,跟显存组件 id(llm.assistant.8b@16k…)也对不上,谁也映射不到谁;
//   勾了不会发生任何事,而界面看着像配置好了。
//   ⇒ 组件清单现在由中枢下发(GET /v1/gpu/components),见 Views/ComponentPicker.cs。
//   ★ 不是"注释掉留着以后参考" —— 留着它就是它回来的方式。删干净,历史在 git 里。
//
// 这里保留的 Presets 是【真的】:四个 key 与 config/vram-budget.toml 的 [presets.*] 逐字对应。

namespace LocalAI.Client.Views;

public static class ModelCatalog
{
    /// <summary>自动启用预设的可选项。★ key 必须与 vram-budget.toml 的 [presets.*] 同名。</summary>
    public static readonly (string Key, string Label)[] Presets =
    {
        ("none",         "不自动启用"),
        ("daily",        "日常(8B + 语音)"),
        ("long_context", "长上下文(8B/32K)"),
        ("deep",         "深度模式(30B-A3B)"),
        ("vision",       "视觉(8B + VLM)"),
    };
}
