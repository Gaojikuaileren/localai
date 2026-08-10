// V30b -- 一条任务【长什么样】的**唯一**判据。底部横条与任务抽屉共用这一份。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ 用户裁定(2026-08-09)逐字:「**底部的任务进行栏**…现在有个正在载入中的进度条,不对,修」。
//
//  ★★ 上一轮我把它改在了**抽屉**上,横条一个字节没动 —— 而用户说的就是横条。
//    后果不只是"没修到":抽屉接了就绪闸会显示「已启用」,横条还在跑「正在载入」,
//    **同一条任务在两个地方自相矛盾**,比原来那个假进度条更坏。
//  ⇒ 判据收进这一处。两边都调它,谁也没有自己那一份。
//
//  ══════ 为什么是纯函数 ═══════════════════════════════════════════════════
//   横条那半是**改 XAML 里几个具名控件的属性**(`TaskBarProgress.IsIndeterminate = …`),
//   抽屉那半是**新建控件树**。两种写法没法共用同一段 UI 代码,但它们要回答的是
//   **同一个问题**:这一条要不要画进度条、右边那格写什么。
//   ⇒ 把那个问题抽成纯函数:两边各自去画,但**答案只有一处**,而且自检能直接喂形状给它。
//     (与 `TaskDrawerView.CanResume` 同一手法:界面上算的那个数,和断言验的那个数是同一段代码。)
// ══════════════════════════════════════════════════════════════════════════════

using LocalAI.Client.Services;

namespace LocalAI.Client.Views;

/// <summary>一条任务的呈现:要不要进度条 · 右边那格写什么。</summary>
/// <param name="ShowProgressBar">
/// 画不画进度条。★★ 模型装载**恒为 false** —— 判词:
/// **一个不知道自己进度的进度条,是在假装它知道**(它的 Progress 恒为 -1,
/// 走通用路径会变成 IsIndeterminate 的来回跑动画,而那条动画在说"我在推进")。
/// </param>
/// <param name="StateText">右边那一格的文字。模型装载取就绪闸的 <c>StateLabel</c>,真任务取百分比。</param>
public readonly record struct TaskRowLook(bool ShowProgressBar, string StateText)
{
    /// <summary>
    /// 这一条任务该怎么画。
    /// <para>★ <paramref name="gate"/> 只在 <c>t.IsModelLoad</c> 时被用到 ——
    /// 真任务的进度是它自己的事,与模型就绪毫无关系。</para>
    /// </summary>
    public static TaskRowLook For(RunningTask t, ModelGate gate)
        => t.IsModelLoad
            // ★ 模型装载:不画进度条,右边写它的**就绪态**(未启用 / 正在启用中 / 已启用)。
            ? new TaskRowLook(false, gate.StateLabel)
            // ★ 真任务:照旧。★★ 暂停态由 PercentText 自己说"已暂停"(它不显示百分比,
            //   一条停着的百分比会让人以为它还在动)——那条纪律在 RunningTask 里,不在这儿重写。
            : new TaskRowLook(!t.IsPaused, t.PercentText);
}
