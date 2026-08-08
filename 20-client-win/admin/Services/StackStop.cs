// V14 裁定⑤ -- 托盘右键「关闭」= **关栈入口**。
//
// ★★ 承的是 D102 裁定④:「**关栈是人的动作,不是推断**」——
//   跨机空闲阈值 / 副机在线名单 / 定时巡检当时被整个撤掉,理由就是**它们在替人做判断**。
//   ⇒ 托盘右键那一下**就是那个"人的动作"**。本文件只负责把判据摆到人面前,不替人决定。
//
// ★ V9 已经做好了判据本身:`10-core/gateway/gateway.py:849` 的 `safe_to_stop_stack()`
//   —— 它**只回答**「现在关会不会切断别人」,**自己不关任何东西**。
//   在此之前它**有判据、没有入口**;这条入口就是那个缺口。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ 如实交代一件没做完的事(DEBT · V14 → V16)
//
//  `safe_to_stop_stack()` 今天**没有 HTTP 路由** —— 本车道实测:全仓只有它的定义
//  与 test_gpu_broker.py 里的直接调用,`gateway.py` 里没有任何路由暴露它。
//  而 `10-core/gateway/**` 是 **V16 车道正在动的禁区**,本车道不碰。
//
//  ⇒ 于是这里的选择是:**如实说读不到,并且仍然把决定权交给人**。
//    · **不**猜一个"应该没人在用"然后替人关掉 —— 那正是 D102 撤掉的那种推断;
//    · **不**因为读不到就禁用这个入口 —— 那等于把 D102 留下的空位继续空着;
//    · ⇒ 弹窗如实写「**读不到副机在不在用**」,由人决定。
//      这与 D99 裁定④同一条规矩:**置灰但不说原因等于骗人,而给个错的原因更坏**。
// ══════════════════════════════════════════════════════════════════════════════

namespace LocalAI.Admin.Services;

/// <summary>关栈之前问一句「现在关会不会切断别人」的结果。</summary>
/// <param name="Known">判据**读到了没有**。false = 读不到 —— 那时 <paramref name="Blocking"/> 无意义。</param>
/// <param name="Safe">读到了、而且现在关不会切断别人。</param>
/// <param name="Why">给人看的说明。★ 读不到时必须说"读不到",不许写成"没人在用"。</param>
public sealed record StopVerdict(bool Known, bool Safe, string Why);

public static class StackStop
{
    /// <summary>
    /// 中枢那条判据的路由 —— **今天还不存在**,等 V16 在网关那侧开出来。
    /// ★ 名字先定下来放这儿,是为了让"缺的是哪一条"具体、可搜、可交接,
    ///   而不是留一句"以后再说"。
    /// </summary>
    public const string SafeToStopRoute = "/v1/stack/safe-to-stop";

    /// <summary>
    /// 问一次「现在关栈会不会切断别人」。
    /// <para>★★ 今天必然返回 <c>Known=false</c> —— 见文件头那段 DEBT。
    /// 这**不是**占位实现:它如实表达了"判据在中枢、而入口还没开",
    /// 而调用方(托盘关闭)据此弹的是「读不到,仍要关吗」而不是「没人在用,关吧」。</para>
    /// </summary>
    public static Task<StopVerdict> QueryAsync()
        => Task.FromResult(new StopVerdict(
            Known: false, Safe: false,
            Why: "读不到副机在不在用 —— 中枢那条判据(safe_to_stop_stack)今天还没有对外的路由,"
               + $"计划开在 {SafeToStopRoute}。\n"
               + "★ 这里不替你猜:猜【应该没人用】然后替你关掉,正是 D102 撤掉的那种推断。"));

    /// <summary>
    /// 关栈之前给人看的那句话。★ 三种处境要说三句**不同**的话 ——
    /// 把"读不到"和"没人在用"合成一句,就是给一个**错的**理由。
    /// </summary>
    public static string ConfirmText(StopVerdict v) => v switch
    {
        { Known: false } => "要关掉整套 AI 栈吗?\n\n" + v.Why,
        { Safe: false } => "副机正在用,仍要关吗?\n\n" + v.Why,
        _ => "现在关不会切断别人。要关掉整套 AI 栈吗?\n\n" + v.Why,
    };
}
