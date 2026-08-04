// P3c -- 读 config/vram-budget.toml。
// ★ 纪律(该文件头两行就写着):组件 peak 是唯一数据源,**别在代码里散落数字**。
//   所以这里只读、不硬编码任何显存数值 —— 读不到就如实返回 0/false,不用假设值兜底。
//
// 只做一件事:告诉界面「已启用的模型 max 占用」是多少。
// 组件选择器要等 P4;在那之前没有"已启用"的概念 -> 恒为 0(如实,不编造)。

namespace LocalAI.Client.Services;

public static class VramBudget
{
    static string? _tomlPath;
    static double? _cachedPeak;

    /// <summary>定位仓库里的 config/vram-budget.toml。找不到返回 null(客户端可能装在别处)。</summary>
    static string? FindToml()
    {
        if (_tomlPath is not null) return _tomlPath;
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var p = Path.Combine(dir, "config", "vram-budget.toml");
            if (File.Exists(p)) return _tomlPath = p;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    /// <summary>
    /// 一组组件 id 的 peak 之和(GiB)。★ 只认**中枢下发**的组件 id ——
    /// 客户端不维护第二份组件清单(P4-S9 之前 Views/ModelCatalog.cs 里那份自造清单
    /// chat.8b / speech / image 是**第三套词汇**,跟这里的 id 一个都对不上)。
    ///
    /// ★★ 认不出的 id **不当成 0**:那会让模型段偷偷少算,而少算的方向是
    ///   "看起来还有余量" —— fail-open。认不出就整体返回 null 的语义交给调用方,
    ///   这里返回已知部分并把未知项计入 <paramref name="unknown"/>,由界面如实说出来。
    /// </summary>
    public static double PeakSumGiB(IEnumerable<string> ids, ICollection<string>? unknown = null)
    {
        var peaks = Peaks();
        double sum = 0;
        foreach (var id in ids)
        {
            if (peaks.TryGetValue(id, out var p)) sum += p;
            else unknown?.Add(id);
        }
        return Math.Round(sum, 4);
    }

    static Dictionary<string, double>? _peaks;

    /// <summary>组件 id → peak。读本地那份 toml(与中枢同一份文件);读不到返回空表。</summary>
    public static Dictionary<string, double> Peaks()
    {
        if (_peaks is not null) return _peaks;
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        var path = FindToml();
        if (path is null) return _peaks = map;
        try
        {
            string? cur = null;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("[components.\""))
                {
                    var a = line.IndexOf('"'); var b = line.LastIndexOf('"');
                    cur = b > a ? line[(a + 1)..b] : null;
                }
                else if (line.StartsWith("[")) cur = null;      // 换段了
                else if (cur is not null && line.StartsWith("peak"))
                {
                    var v = line.Split('=', 2)[1].Split('#')[0].Trim();
                    if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out var d))
                        map[cur] = d;
                }
            }
        }
        catch { }
        return _peaks = map;
    }

    /// <summary>配置里的桌面保留下限(desktop_floor),仅用于界面上标一条参考线。读不到返回 null。</summary>
    public static double? DesktopFloorGiB()
    {
        if (_cachedPeak is not null) return _cachedPeak;
        var p = FindToml();
        if (p is null) return null;
        try
        {
            foreach (var raw in File.ReadAllLines(p))
            {
                var line = raw.Trim();
                if (!line.StartsWith("desktop_floor")) continue;
                var v = line.Split('=', 2)[1].Split('#')[0].Trim();
                if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var d))
                    return _cachedPeak = d;
            }
        }
        catch { }
        return null;
    }
}
