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
    /// 已启用组件的 peak 之和(GiB)。
    /// P4 的组件选择器落地前没有"已启用"状态 -> 0。届时改为读 broker 的实际启用集合。
    /// </summary>
    public static double EnabledModelsPeakGiB()
    {
        // 组件选择状态属于 P4(GPU Broker + 组件选择器)。现在没有任何组件被"启用",
        // 所以模型段恒为 0 —— 这是真话:此刻显存里确实没有经 broker 装载的模型。
        return 0;
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
