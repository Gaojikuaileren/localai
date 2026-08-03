using System.Text.Json;

namespace LocalAI.Client.Services.Pet;

public sealed record PetClip(
    string Id, string Group, string Track,
    int IndependentFrames, int Ticks, double DurationMs,
    bool Loop, bool V1a, bool MustFinish,
    string DirFrom, string DirTo, bool RuntimeMirror);

public sealed record PetEdge(
    string From, string To, string? Clip,
    bool MustFinish, bool Preempt,
    string? AtEvent, string? RequestMode, int Priority)
{
    /// `*any` / `*visible` 这类通配来源。它们是抢占规则,不参与寻路。
    public bool IsWildcard => From.Length > 0 && From[0] == '*';
}

public sealed record PetStateDef(string Name, string? LoopClip, IReadOnlyList<string> InsertClips);

// 动画状态机的**唯一事实来源** —— loading-cow-cat-animation-manifest-v1.json。
//
// 边界纪律(讨论定稿):行为层与助手只能提意图,**永远不能点名 clip**。
// 所以本类只对外暴露「状态 → 状态」的寻路,不暴露"直接播某个 clip"。
// 姿态权威在 PetAnimator,这里只负责把 JSON 变成一张可查的图,并且**fail-closed**:
// 任何引用不到的 clip/状态、任何没人播的孤儿 clip,都是错误而不是警告。
public sealed class PetManifest
{
    public IReadOnlyDictionary<string, PetClip> Clips { get; }
    public IReadOnlyDictionary<string, PetStateDef> States { get; }
    public IReadOnlyList<PetEdge> Edges { get; }
    public IReadOnlyList<string> ParallelLayers { get; }
    public int Fps { get; }

    readonly Dictionary<string, List<PetEdge>> _adj;

    PetManifest(Dictionary<string, PetClip> clips, Dictionary<string, PetStateDef> states,
                List<PetEdge> edges, List<string> parallel, int fps)
    {
        Clips = clips; States = states; Edges = edges; ParallelLayers = parallel; Fps = fps;

        _adj = new Dictionary<string, List<PetEdge>>(StringComparer.Ordinal);
        foreach (var e in edges)
        {
            if (e.IsWildcard || e.From == e.To) continue;   // 自环(turn_180)不改状态,不参与寻路
            if (!_adj.TryGetValue(e.From, out var list)) _adj[e.From] = list = new List<PetEdge>();
            list.Add(e);
        }
    }

    public static PetManifest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var fps = root.TryGetProperty("fps", out var f) ? f.GetInt32() : 6;

        var clips = new Dictionary<string, PetClip>(StringComparer.Ordinal);
        if (root.TryGetProperty("clips", out var clipsEl) && clipsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in clipsEl.EnumerateObject())
            {
                var c = p.Value;
                string dirFrom = "left", dirTo = "left"; bool mirror = false;
                if (c.TryGetProperty("direction", out var d) && d.ValueKind == JsonValueKind.Object)
                {
                    dirFrom = Str(d, "from") ?? "left";
                    dirTo = Str(d, "to") ?? "left";
                    mirror = Bool(d, "runtime_mirror");
                }
                clips[p.Name] = new PetClip(
                    p.Name,
                    Str(c, "group") ?? "", Str(c, "track") ?? "body",
                    Int(c, "independent_frames"), Int(c, "ticks"), Dbl(c, "duration_ms"),
                    Bool(c, "loop"), Bool(c, "v1a"), Bool(c, "must_finish"),
                    dirFrom, dirTo, mirror);
            }
        }

        var states = new Dictionary<string, PetStateDef>(StringComparer.Ordinal);
        if (root.TryGetProperty("states", out var statesEl) && statesEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in statesEl.EnumerateObject())
            {
                var inserts = new List<string>();
                if (p.Value.TryGetProperty("insert_clips", out var ins) && ins.ValueKind == JsonValueKind.Array)
                    foreach (var i in ins.EnumerateArray()) if (i.ValueKind == JsonValueKind.String) inserts.Add(i.GetString()!);
                states[p.Name] = new PetStateDef(p.Name, Str(p.Value, "loop_clip"), inserts);
            }
        }

        var edges = new List<PetEdge>();
        if (root.TryGetProperty("edges", out var edgesEl) && edgesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in edgesEl.EnumerateArray())
            {
                edges.Add(new PetEdge(
                    Str(e, "from") ?? "", Str(e, "to") ?? "", Str(e, "clip"),
                    Bool(e, "must_finish"), Bool(e, "preempt"),
                    Str(e, "at_event"), Str(e, "request_mode"),
                    e.TryGetProperty("priority", out var pr) && pr.ValueKind == JsonValueKind.Number ? pr.GetInt32() : 0));
            }
        }

        var parallel = new List<string>();
        if (root.TryGetProperty("parallel_layers", out var pl) && pl.ValueKind == JsonValueKind.Array)
            foreach (var i in pl.EnumerateArray()) if (i.ValueKind == JsonValueKind.String) parallel.Add(i.GetString()!);

        return new PetManifest(clips, states, edges, parallel, fps);
    }

    public static PetManifest Load(string path) => Parse(File.ReadAllText(path));

    // ---- 校验:全部 fail-closed,返回空列表才算通过 ----
    //
    // ★ 最后一条(孤儿 clip)是这套工具里唯一能挡住「画了没人播的帧」的检查。
    //   契约里那份 mobility_cut 删掉 stalk 却留下 stand_to_stalk/stalk_to_stand,
    //   就是这个形状 —— 8 张帧成为不可达资产。
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        foreach (var (name, st) in States)
        {
            if (st.LoopClip is { } lc && !Clips.ContainsKey(lc))
                errors.Add($"状态 {name} 的 loop_clip 指向不存在的 clip: {lc}");
            foreach (var ic in st.InsertClips)
                if (!Clips.ContainsKey(ic))
                    errors.Add($"状态 {name} 的 insert_clip 指向不存在的 clip: {ic}");
        }

        foreach (var e in Edges)
        {
            if (!e.IsWildcard && !States.ContainsKey(e.From))
                errors.Add($"边 {e.From}->{e.To} 的起点状态不存在");
            if (!States.ContainsKey(e.To))
                errors.Add($"边 {e.From}->{e.To} 的终点状态不存在");
            if (e.Clip is { } ec)
            {
                if (!Clips.TryGetValue(ec, out var clip))
                    errors.Add($"边 {e.From}->{e.To} 引用不存在的 clip: {ec}");
                else if (e.MustFinish && (clip.Loop || !clip.MustFinish))
                    errors.Add($"边 clip {ec} 声明 must_finish,但 clip 本身是循环或未标 must_finish");
            }
        }

        foreach (var (id, c) in Clips)
        {
            var expected = c.Ticks * 1000.0 / Fps;
            if (Math.Abs(c.DurationMs - expected) > 0.001)
                errors.Add($"clip {id} 的 duration_ms 与 ticks/fps 不一致(差 {Math.Abs(c.DurationMs - expected):0.###} ms)");
            if (c.IndependentFrames < 1 || c.Ticks < 1)
                errors.Add($"clip {id} 的 independent_frames / ticks 必须为正");
            if (c.Group == "transition" && (c.Loop || !c.MustFinish))
                errors.Add($"clip {id} 属 transition 组,必须非循环且 must_finish");
        }

        // 孤儿 clip:没有任何状态或边会播它
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var st in States.Values)
        {
            if (st.LoopClip is { } lc) referenced.Add(lc);
            foreach (var ic in st.InsertClips) referenced.Add(ic);
        }
        foreach (var e in Edges) if (e.Clip is { } ec) referenced.Add(ec);
        foreach (var layer in ParallelLayers) referenced.Add(layer);

        foreach (var id in Clips.Keys)
            if (!referenced.Contains(id))
                errors.Add($"孤儿 clip {id}:没有任何状态或边会播它 —— 画了没人用");

        // 可达性:每个状态都要能从 suspended 走到。
        // 通配边(`*visible -> dangle`、`*any -> suspended`)是抢占规则,不参与寻路,
        // 但它们**确实**让目标从任意状态可达 —— 故这里作为起点一并播种,否则会误报。
        if (States.ContainsKey("suspended"))
        {
            var seeds = new List<string> { "suspended" };
            foreach (var e in Edges) if (e.IsWildcard) seeds.Add(e.To);

            var seen = Reachable(seeds);
            foreach (var name in States.Keys)
                if (!seen.Contains(name))
                    errors.Add($"状态 {name} 从 suspended 不可达 —— 这条支路在运行时是死的");
        }

        return errors;
    }

    /// 通配边(抢占规则)。Grab / Suspend 这类走它,而不是走寻路。
    public PetEdge? WildcardEdgeTo(string to)
    {
        foreach (var e in Edges) if (e.IsWildcard && e.To == to) return e;
        return null;
    }

    HashSet<string> Reachable(IEnumerable<string> from)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var q = new Queue<string>();
        foreach (var s in from) if (seen.Add(s)) q.Enqueue(s);
        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            if (!_adj.TryGetValue(cur, out var outs)) continue;
            foreach (var e in outs)
                if (seen.Add(e.To)) q.Enqueue(e.To);
        }
        return seen;
    }

    /// 从 from 走到 to 的最短过渡链。返回 null 表示不可达。
    /// 状态机必须自己解出这条链 —— 行为层只说"我要去坐着",不拼 sit_to_loaf 那一串。
    public IReadOnlyList<PetEdge>? FindPath(string from, string to)
    {
        if (!States.ContainsKey(from) || !States.ContainsKey(to)) return null;
        if (from == to) return Array.Empty<PetEdge>();

        var prev = new Dictionary<string, PetEdge>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        var q = new Queue<string>(); q.Enqueue(from);

        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            if (!_adj.TryGetValue(cur, out var outs)) continue;
            foreach (var e in outs)
            {
                if (!seen.Add(e.To)) continue;
                prev[e.To] = e;
                if (e.To == to)
                {
                    var path = new List<PetEdge>();
                    for (var at = to; at != from; at = prev[at].From) path.Add(prev[at]);
                    path.Reverse();
                    return path;
                }
                q.Enqueue(e.To);
            }
        }
        return null;
    }

    /// 该状态的转身边。判据是**clip 本身改变朝向**(direction.from != direction.to),
    /// 而不是"第一条自环" —— 否则将来往 stand 上挂第二条自环(如自包含的 pounce)会把转身顶掉。
    /// 转身只能走这条,不许一帧镜像(规范 §6)。
    public PetEdge? TurnEdge(string state)
    {
        foreach (var e in Edges)
        {
            if (e.IsWildcard || e.From != state || e.To != state || e.Clip is null) continue;
            if (Clips.TryGetValue(e.Clip, out var c) && c.DirFrom != c.DirTo) return e;
        }
        return null;
    }

    static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool Bool(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    static int Int(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    static double Dbl(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
