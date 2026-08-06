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
//
// ★★★ 2026-08-06 如实记账(V2 车道 · D90 未决项④的处置):
//   `AutoStartPreset`(连上中枢就自动装预设)已作废撤掉 —— 它与 D87 裁定①
//   「不做开机预热」正面矛盾。**于是这张表今天没有任何读取方。**
//   ★ 写出来而不是任它躺着:「随包发布的死代码」正是 D92 点名的 A5 那个形状。
//   ★ 不删它的理由是**具体的**,不是"以后也许有用":D87 裁定⑤「可自定义,
//     但必须有一个预设」仍然有效,而它的落点是「模型选择策略」那片占位框
//     (ModelsView.StrategyPlaceholder)—— 那件事还没做。
//   ⇒ 它做出来之前,这张表是**待接线的材料**;若那件事被裁掉,这张表跟着删。

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
