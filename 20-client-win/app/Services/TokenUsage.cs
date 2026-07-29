// P3c -- token 用量(占位)。
//
// ★ 诚实:模型用量统计尚未接入(模型要到 P4 GPU Broker 才按需装载,更谈不上累计用量)。
//   所以现在【一个真实数字都没有】—— Connected=false,各值为 null。界面据此显示"待接入"/"—",
//   绝不编造数字。接入后:由中枢按会话累加,这里改成向中枢取真实计数即可,界面结构不变。

namespace LocalAI.Client.Services;

public static class TokenUsage
{
    /// <summary>用量统计是否已接入。接入前恒为 false —— 界面显示"待接入",不出数字。</summary>
    public static bool Connected => false;

    // 接入后:今日 / 本周 / 本月 / 累计 的 token 消耗。未接入时全为 null(界面显示"—")。
    public static long? Today => null;
    public static long? Week => null;
    public static long? Month => null;
    public static long? Total => null;
}
