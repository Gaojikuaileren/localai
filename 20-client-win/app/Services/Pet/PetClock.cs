namespace LocalAI.Client.Services.Pet;

// 宠物的 6 fps 固定步长时钟(动画规范 §5)。
//
// 纪律三条,每条都有对应断言:
//  1. 单调时钟 + accumulator。**不用** 连续 Task.Delay(166) —— 那会累积漂移。
//  2. 单次卡顿最多追赶 MaxCatchUpTicks 个 tick;更长直接丢弃积压并重同步。
//     理由:规范明写「禁止快速补播一串动作」—— 补播会让猫在卡顿后瞬移一段。
//  3. 时钟回拨(NTP 校时 / 休眠恢复)不得产生负 tick,一律重同步。
//
// 这个类不持有任何 UI 依赖,故可在自检里用假时间源逐 tick 推。
public sealed class PetClock
{
    public const int Fps = 6;
    public const double TickMs = 1000.0 / Fps;   // 166.666…
    public const int MaxCatchUpTicks = 2;

    double _acc;
    long _lastMs = long.MinValue;

    /// 自 Reset 以来实际推进的 tick 总数(被丢弃的积压不计入)。
    public long Ticks { get; private set; }

    /// 发生过多少次重同步(卡顿超预算 / 时钟回拨)。诊断用,不影响播放。
    public int Resyncs { get; private set; }

    public void Reset(long nowMs)
    {
        _lastMs = nowMs;
        _acc = 0;
        Ticks = 0;
        Resyncs = 0;
    }

    /// 返回本次应推进的 tick 数,范围 0..MaxCatchUpTicks。
    /// 调用方每收到 1 个 tick 就把状态机推一步,并在**同一 tick 内**同时提交 sprite 与 root_delta
    /// (规范 §5:身体动画和窗口位移不得错相)。
    public int Advance(long nowMs)
    {
        if (_lastMs == long.MinValue) { _lastMs = nowMs; return 0; }

        var dt = nowMs - _lastMs;
        _lastMs = nowMs;

        if (dt < 0) { _acc = 0; Resyncs++; return 0; }   // 时钟回拨:不产生负 tick

        _acc += dt;
        var n = (int)(_acc / TickMs);
        if (n <= 0) return 0;

        _acc -= n * TickMs;

        if (n > MaxCatchUpTicks)
        {
            n = MaxCatchUpTicks;
            _acc = 0;                                     // 丢弃积压,不补播
            Resyncs++;
        }

        Ticks += n;
        return n;
    }
}
