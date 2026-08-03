namespace LocalAI.Client.Services.Pet;

// 动画状态机 —— **姿态的唯一权威**。
//
// 承重的一条边界:行为层与助手只能提意图,永远不能点名 clip、不能指定帧、不能指定时长。
// 本类有权拒绝、推迟,或把一个意图改写成一条合法的过渡链。
// 只要这条边界成立,规范 §6 的四条铁律(禁 cross-fade / 只在兼容接触帧切换 /
// 事件等 can_exit / 转身必须走 turn_180)就是结构上做不到违反,而不是靠自觉。
public sealed class PetAnimator
{
    /// 走路时每 tick 的水平位移(逻辑像素)。
    /// ★ 占位常量 —— 正式值必须来自每帧的 root_delta。帧数据(manifest 的 frames[])尚未交付,
    ///   在它落地之前 UsesPlaceholderRootDelta 恒为 true,界面据此如实标注。
    public const int PlaceholderWalkStepPx = 3;

    readonly PetManifest _m;
    readonly List<PetIntentAudit> _audit = new();
    readonly HashSet<string> _loading = new(StringComparer.Ordinal);
    readonly Queue<PetEdge> _path = new();

    string? _targetState;
    PetFacing? _targetFacing;
    string? _pendingPerform;
    PetEdge? _activeEdge;
    int _targetX;
    bool _hasTargetX;
    long _assistantWindowStart;
    int _assistantInWindow;

    public PetAnimator(PetManifest manifest)
    {
        _m = manifest;
        State = _m.States.ContainsKey("suspended") ? "suspended" : _m.States.Keys.First();
    }

    public string State { get; private set; }
    public PetFacing Facing { get; private set; } = PetFacing.Left;
    public string? PlayingClip { get; private set; }
    public int ClipTick { get; private set; }
    public long Tick { get; private set; }
    public int X { get; private set; }

    /// 帧数据未交付前恒真。位移用的是占位常量而非 root_delta。
    public bool UsesPlaceholderRootDelta => true;

    /// 加载环是独立并行层,不进身体队列;隐藏于 suspended / behind_door(契约 loading_contract)。
    public bool LoadingVisible => _loading.Count > 0 && State is not ("suspended" or "behind_door");

    public IReadOnlyList<PetIntentAudit> Audit => _audit;

    // ---- 意图入口 ----

    public PetIntentResult Post(PetIntent intent)
    {
        var r = Evaluate(intent);
        _audit.Add(new PetIntentAudit(Tick, intent, r));
        if (_audit.Count > 512) _audit.RemoveRange(0, 256);
        return r;
    }

    PetIntentResult Evaluate(PetIntent it)
    {
        if (!Enum.IsDefined(it.Kind))
            return PetIntentResult.No("未知意图 —— 白名单外一律拒绝");
        if (!PetIntentPolicy.IsAllowed(it.Kind, it.Source))
            return PetIntentResult.No($"{it.Source} 不允许投递 {it.Kind}");

        if (it.Source == PetIntentSource.Assistant && !CheckAssistantRate())
            return PetIntentResult.No($"助手意图速率超限(每分钟 {PetIntentPolicy.AssistantIntentsPerMinute} 条)");

        switch (it.Kind)
        {
            // 紧急:绕过 can_exit 与 must_finish,立即生效,不播退场演出(规范 §5)
            case PetIntentKind.Suspend:
                HardSuspend();
                return PetIntentResult.Ok("立即隐藏并释放");

            case PetIntentKind.Attend:
                var id = it.Clip ?? "default";
                if (it.On) _loading.Add(id); else _loading.Remove(id);
                return PetIntentResult.Ok(it.On ? "加载环点亮" : "加载环熄灭");

            case PetIntentKind.Grab:
                if (State is "suspended" or "behind_door") return PetIntentResult.No("猫不在场,拎不到");
                if (State == "dangle") return PetIntentResult.Ok("已经拎着了");
                // 契约 request_mode = defer_until_current_must_finish:拖拽不打断必须播完的过渡
                if (InMustFinish())
                    return PetIntentResult.Defer(Tick + RemainingTicks(), "当前过渡必须播完");
                // 走通配边而非寻路 —— `*visible -> dangle` 从任意可见状态直达
                if (_m.WildcardEdgeTo("dangle") is not { } grab) return PetIntentResult.No("转移图上没有拎起的通配边");
                _path.Clear(); _targetState = null; _targetFacing = null; _pendingPerform = null; _hasTargetX = false;
                StartEdge(grab);
                return PetIntentResult.Ok("拎起来了");

            case PetIntentKind.Drop:
                if (State != "dangle") return PetIntentResult.No("没被拎着,无从放下");
                return SetTarget("stand", "放下");

            case PetIntentKind.Perform:
                if (it.Clip is not { } pc) return PetIntentResult.No("Perform 必须点名一个 clip");
                if (!_m.States.TryGetValue(State, out var sd) || !sd.InsertClips.Contains(pc))
                    return PetIntentResult.No($"{pc} 不在状态 {State} 的表演白名单里");
                _pendingPerform = pc;
                return CanExitNow() ? PetIntentResult.Ok("表演已排入")
                                    : PetIntentResult.Defer(Tick + RemainingTicks(), "等当前 clip 到可离开的帧");

            case PetIntentKind.Face:
                if (it.Facing == Facing) return PetIntentResult.Ok("已经是这个朝向");
                _targetFacing = it.Facing;
                return PetIntentResult.Ok("转身已排入(必经 turn_180)");

            case PetIntentKind.MoveTo:
                _targetX = it.X; _hasTargetX = true;
                return SetTarget("walk", $"走向 x={it.X}");

            case PetIntentKind.Idle: return SetTarget("stand", "站着");
            case PetIntentKind.Rest: return SetTarget("sit", "歇着");
            case PetIntentKind.Sleep: return SetTarget("sleep", "去睡");
            case PetIntentKind.Wake: return SetTarget("stand", "醒来");
            case PetIntentKind.ScratchDoor: return SetTarget("scratch_door", "去挠门");

            case PetIntentKind.EnterDoor:
                if (it.Source == PetIntentSource.Assistant)
                    return PetIntentResult.No("助手不能开门,只能让猫去挠门");
                return SetTarget("behind_door", "穿门离开");

            case PetIntentKind.ExitDoor:
                if (State != "behind_door") return PetIntentResult.No("猫不在门里");
                return SetTarget("stand", "从门里回来");
        }
        return PetIntentResult.No("未处理的意图");
    }

    PetIntentResult SetTarget(string state, string reason)
    {
        if (!_m.States.ContainsKey(state)) return PetIntentResult.No($"目标状态 {state} 不存在");
        if (state != State && _m.FindPath(State, state) is null)
            return PetIntentResult.No($"{State} 到 {state} 在转移图上不可达");

        // latest-wins:意图不排队。规范 §5 为卡顿禁止了"补播一串动作",
        // 同一条理由适用于意图 —— 排队会让猫补演一串已经过时的动作。
        _targetState = state;
        _path.Clear();

        return CanExitNow() ? PetIntentResult.Ok(reason)
                            : PetIntentResult.Defer(Tick + RemainingTicks(), reason + "(等当前 clip 可离开)");
    }

    bool CheckAssistantRate()
    {
        const long WindowTicks = 60 * PetClock.Fps;
        if (Tick - _assistantWindowStart >= WindowTicks) { _assistantWindowStart = Tick; _assistantInWindow = 0; }
        if (_assistantInWindow >= PetIntentPolicy.AssistantIntentsPerMinute) return false;
        _assistantInWindow++;
        return true;
    }

    void HardSuspend()
    {
        _path.Clear();
        _activeEdge = null;
        _targetState = null;
        _targetFacing = null;
        _pendingPerform = null;
        _hasTargetX = false;
        PlayingClip = null;
        ClipTick = 0;
        State = "suspended";
    }

    // ---- 播放 ----

    /// 推进一个 tick。调用方每从 PetClock 拿到 1 个 tick 就调一次。
    public void Advance()
    {
        Tick++;

        if (PlayingClip is { } cur && _m.Clips.TryGetValue(cur, out var clip))
        {
            ClipTick++;
            if (ClipTick >= clip.Ticks)
            {
                if (clip.Loop) ClipTick = 0;
                else CompleteClip(clip);
            }
        }

        if (State == "walk" && _hasTargetX) StepWalk();

        Pump();
    }

    void StepWalk()
    {
        var dir = _targetX >= X ? 1 : -1;
        var step = PlaceholderWalkStepPx * dir;
        if (Math.Abs(_targetX - X) <= PlaceholderWalkStepPx) { X = _targetX; _hasTargetX = false; _targetState = "stand"; }
        else X += step;
    }

    void CompleteClip(PetClip clip)
    {
        if (_activeEdge is { } e)
        {
            State = e.To;
            // 转身:朝向只在 turn_180 播完时翻,别处一律不许改
            if (clip.DirFrom != clip.DirTo)
            {
                Facing = Facing == PetFacing.Left ? PetFacing.Right : PetFacing.Left;
                if (_targetFacing == Facing) _targetFacing = null;
            }
            _activeEdge = null;
        }
        PlayingClip = null;
        ClipTick = 0;
    }

    void Pump()
    {
        // ★ 过渡在飞行中就不要再解算 —— 状态要等 clip 播完才变(CompleteClip 里改),
        //   而 CanExitNow 在最后一 tick 就已为真。少了这道闸,会在最后一 tick 用**旧状态**
        //   重新寻路,把同一段过渡无限重启,猫永远走不完第一步。
        if (_activeEdge is not null) return;
        if (!CanExitNow()) return;

        if (_path.Count > 0) { StartEdge(_path.Dequeue()); return; }

        if (_targetState is { } ts)
        {
            if (ts == State) { _targetState = null; }
            else
            {
                var path = _m.FindPath(State, ts);
                if (path is null || path.Count == 0) { _targetState = null; }
                else { foreach (var e in path) _path.Enqueue(e); StartEdge(_path.Dequeue()); return; }
            }
        }

        if (_targetFacing is { } tf && tf != Facing)
        {
            // 坐姿/趴姿没有转身 clip —— 要转身必须先起身(契约 direction_policy)
            if (State != "stand") { _targetState = "stand"; Pump(); return; }
            if (_m.TurnEdge("stand") is { } turn) { StartEdge(turn); return; }
            _targetFacing = null;   // 图上没有转身边 → 不假装转得了
        }

        if (_pendingPerform is { } perform)
        {
            _pendingPerform = null;
            StartClip(perform, null);
            return;
        }

        if (PlayingClip is null) StartStateLoop();
    }

    void StartEdge(PetEdge e)
    {
        if (e.Clip is null)
        {
            // 无过渡 clip 的边(如 walk↔trot 在共享接触帧直切)
            State = e.To;
            PlayingClip = null; ClipTick = 0; _activeEdge = null;
            StartStateLoop();
            return;
        }
        StartClip(e.Clip, e);
    }

    void StartClip(string clipId, PetEdge? edge)
    {
        if (!_m.Clips.ContainsKey(clipId)) return;
        PlayingClip = clipId;
        ClipTick = 0;
        _activeEdge = edge;
    }

    void StartStateLoop()
    {
        if (_m.States.TryGetValue(State, out var sd) && sd.LoopClip is { } lc && _m.Clips.ContainsKey(lc))
            StartClip(lc, null);
        else { PlayingClip = null; ClipTick = 0; }   // suspended / behind_door 没有循环 clip
    }

    // ---- 可离开判定 ----

    public bool CanExitNow()
    {
        if (PlayingClip is not { } cur || !_m.Clips.TryGetValue(cur, out var clip)) return true;
        if (clip.MustFinish) return ClipTick >= clip.Ticks - 1;
        if (clip.Loop)
        {
            // ★ 行走类循环只能在兼容的 paw_down 接触帧之间切换(规范 §6)。
            //   每帧的 contacts 尚未交付,故暂以循环末尾作为唯一接缝 —— 保守但不会滑步。
            //   帧数据落地后此处改读 contacts,并补上断言。
            if (clip.Group == "locomotion") return ClipTick >= clip.Ticks - 1;
            return true;
        }
        return ClipTick >= clip.Ticks - 1;
    }

    bool InMustFinish()
        => PlayingClip is { } cur && _m.Clips.TryGetValue(cur, out var c) && c.MustFinish && ClipTick < c.Ticks - 1;

    int RemainingTicks()
        => PlayingClip is { } cur && _m.Clips.TryGetValue(cur, out var c) ? Math.Max(0, c.Ticks - 1 - ClipTick) : 0;
}
