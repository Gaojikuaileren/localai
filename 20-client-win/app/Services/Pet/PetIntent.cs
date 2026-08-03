namespace LocalAI.Client.Services.Pet;

// 宠物的**唯一**输入通道 —— 反应层事件、行为逻辑树、助手,三者走完全同一个入口。
//
// 为什么是同一个:助手在结构上就不可能做出反应层做不到的事,而不是靠一层权限判断去拦。
// 这是 P3a 反复吃过的那类亏的反面(unseal_for_client 硬编码 TRUSTED_LOCAL
// → 隔离退化成「信任调用方诚实」)。这里没有"可信调用方"这个概念。
//
// ★ v1a 结构性地不存在 Speak —— 哑巴猫不是配置项,是这个枚举里没有那一项。

public enum PetIntentKind
{
    /// 歇着(坐/趴)
    Rest,
    /// 醒着但闲(站)
    Idle,
    Sleep,
    Wake,
    /// 加载环开/关。不碰身体轨。
    Attend,
    /// 走到家里的某个 x(逻辑像素)
    MoveTo,
    /// 转向。只能经 turn_180,不许一帧镜像。
    Face,
    /// 点名一个**表演**动作。只能从当前状态的 insert_clips 里选 —— 过渡 clip 不在表里。
    Perform,
    /// 去挠门
    ScratchDoor,
    /// 穿门离开(去桌面 / 去另一台 PC)
    EnterDoor,
    /// 从门里回来
    ExitDoor,
    /// 被鼠标拎起
    Grab,
    /// 放下
    Drop,
    /// 紧急隐藏:独占全屏 / 系统休眠 / 租约丢失 / 客户端退出。绕过一切,不播退场演出。
    Suspend,
}

public enum PetIntentSource
{
    /// 客户端已有的真忙碌态、鼠标事件等。
    Reaction,
    /// 行为逻辑树自己的决定。
    Behavior,
    /// 已加载助手。受最严格的白名单与速率限制。
    Assistant,
    /// 用户直接操作(拖猫、点门)。
    User,
}

public enum PetIntentOutcome
{
    /// 已受理并立即生效(或已开始解算过渡链)。
    Accepted,
    /// 已受理但被推迟 —— 当前 clip 必须播完 / 撞冷却 / 要等 can_exit。
    Deferred,
    /// 被拒。原因见 Reason。
    Rejected,
}

public readonly record struct PetIntent(
    PetIntentKind Kind,
    PetIntentSource Source,
    int X = 0,
    bool On = false,
    PetFacing Facing = PetFacing.Left,
    string? Clip = null,
    string? DoorId = null);

public readonly record struct PetIntentResult(PetIntentOutcome Outcome, string Reason, long DeferredUntilTick = 0)
{
    public static PetIntentResult Ok(string reason = "") => new(PetIntentOutcome.Accepted, reason);
    public static PetIntentResult Defer(long untilTick, string reason) => new(PetIntentOutcome.Deferred, reason, untilTick);
    public static PetIntentResult No(string reason) => new(PetIntentOutcome.Rejected, reason);

    public bool IsAccepted => Outcome == PetIntentOutcome.Accepted;
}

public enum PetFacing { Left, Right }

public static class PetIntentPolicy
{
    /// 助手能投的意图。**枚举白名单** —— 新增一个 Kind 默认落在"拒绝"这边,
    /// 这是本项目的固定审查视角:任何白/黑名单,先问「加一个新值默认落哪边」。
    static readonly HashSet<PetIntentKind> AssistantAllowed = new()
    {
        PetIntentKind.Rest,
        PetIntentKind.Idle,
        PetIntentKind.Sleep,
        PetIntentKind.Wake,
        PetIntentKind.MoveTo,
        PetIntentKind.Face,
        PetIntentKind.Perform,
        PetIntentKind.ScratchDoor,
    };

    /// 助手**不能开门**,只能让猫去挠门 —— 门是用户对空间的授权。
    /// 权限检查因此不是一段 if,而是角色行为本身。
    public static bool CanAssistantOpenDoors => false;

    /// 助手的意图速率上限(每分钟)。一只每秒换三个动作的猫本身就是打扰,
    /// 哪怕它一句话没说(D40:在场不算打扰,开口才算)。
    public const int AssistantIntentsPerMinute = 6;

    public static bool IsAllowed(PetIntentKind kind, PetIntentSource source) => source switch
    {
        PetIntentSource.Assistant => AssistantAllowed.Contains(kind),
        PetIntentSource.Reaction => kind != PetIntentKind.Perform,   // 事件不点名表演动作
        _ => Enum.IsDefined(kind),
    };
}

/// 每一条意图的处置都要留痕:谁、什么时候、投了什么、动画层怎么处置的。
public readonly record struct PetIntentAudit(long Tick, PetIntent Intent, PetIntentResult Result);
