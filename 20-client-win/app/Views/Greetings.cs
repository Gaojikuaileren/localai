// P3c -- 主页顶部的问候语。
//   主句:按时段(夜/晨/午/下午/晚)给一句招呼。
//   副句:一句暖心小短句 —— 设计上是"来自小助手的问候"。
//
// ★ 诚实说明:中枢的本地模型【尚未接入】,现在还没法真的让 AI 现场生成。
//   所以副句先用一组【本地精选短句】按时段轮换(同一小时内稳定,不每秒乱跳);
//   接入模型后,由 SubFor 改成向模型取一句结合当下情境(天气/日程)的问候即可,
//   接口位置不变。界面上【不】标注"AI 生成",以免在未接入时冒充模型的产出。

namespace LocalAI.Client.Views;

public static class Greetings
{
    /// <summary>按小时给时段主句。</summary>
    public static string TitleFor(int hour) => hour switch
    {
        < 5 => "夜深了",
        < 11 => "早上好",
        < 14 => "中午好",
        < 18 => "下午好",
        _ => "晚上好",
    };

    static readonly string[] Night =
    {
        "夜深了,早点休息吧。",
        "别熬太晚,身体要紧。",
        "愿你今晚有个好梦。",
        "放下手机,给眼睛也放个假。",
    };
    static readonly string[] Morning =
    {
        "新的一天,慢慢来就好。",
        "先喝杯水,再开始吧。",
        "今天也请多多关照。",
        "愿你一天都顺顺利利。",
        "记得吃早饭,别空着肚子。",
    };
    static readonly string[] Noon =
    {
        "记得好好吃顿午饭。",
        "忙里偷个闲,歇一歇。",
        "午后小憩片刻会更精神。",
        "吃饱了才有力气呀。",
    };
    static readonly string[] Afternoon =
    {
        "喝口水,伸个懒腰吧。",
        "下午也要加油呀。",
        "累了就歇一会儿,不急。",
        "别忘了留点时间给自己。",
    };
    static readonly string[] Evening =
    {
        "辛苦一天啦,放松一下。",
        "回家路上注意安全。",
        "晚饭想吃点什么呢？",
        "今天你已经做得很好了。",
    };

    static string[] BucketFor(int hour) => hour switch
    {
        < 5 => Night,
        < 11 => Morning,
        < 14 => Noon,
        < 18 => Afternoon,
        < 22 => Evening,
        _ => Night,
    };

    /// <summary>
    /// 副句:按时段选一句。★ 用"年内第几天 × 小时"当索引 —— 同一小时内稳定(不每秒乱跳),
    /// 跨小时/跨天会换 —— 而不是随机(随机每次调用都变,会闪)。
    /// </summary>
    public static string SubFor(DateTime now)
    {
        var bucket = BucketFor(now.Hour);
        var idx = (now.DayOfYear * 24 + now.Hour) % bucket.Length;
        return bucket[idx];
    }
}
