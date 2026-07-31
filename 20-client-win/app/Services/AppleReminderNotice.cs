// P3c -- 识别 Apple「提醒事项已升级」的墓碑条目(2026-07-31 查证后加)。
//
// 背景(已查证,有 Apple 官方原文):
//   iOS 13 / macOS Catalina(2019)起,Apple 的提醒事项可以「升级」到新格式。
//   升级是【账号级、一次性、不可逆】的(Apple 原文:"After you update your iCloud reminders,
//   you can't revert them."),升级后数据搬进 Apple 私有的 CloudKit 存储,
//   CalDAV 上原本的任务集合【被删除】,只留下一个名字带 ⚠️ 的占位清单,里面装两条公告:
//       en:      "Where are my reminders?"
//                "The creator of this list has upgraded these reminders."
//       zh-Hans: "在哪里可以找到我的提醒事项?"
//                "此列表的创建者已升级这些提醒事项。"
//   DAVx⁵ 官方也写明:升级之后 CalDAV 客户端【再也读不到】提醒事项。
//
// ★★ 为什么必须单独识别、而不是当普通待办导入:
//   那两条不是用户的待办,是 Apple 在说"东西不在这儿"。把它们塞进「待办与家务」,
//   等于我们替 Apple 把一句通知伪装成了两条任务 —— 用户会以为同步成功了,
//   而真相恰恰相反:他的提醒事项【一条也没拿到,而且这条路永远拿不到】。
//   这正是本项目最该避免的那种"看起来成功"。
//
// ★ 判定用三层,任一命中即算 —— 不赌某一个特征:
//   A 集合名带 ⚠(U+26A0):最稳,Apple 就是这么命名那个占位清单的(且它删不掉,
//     iCloud 要求账号上至少有一个任务清单,删了会被服务端重建)。
//   B 描述里带 Apple 支持链接(HT210220 / 102457):与语言无关。
//   C 标题命中已知的本地化串表:兜底,但会随语言漏 —— 所以不能只靠它。

namespace LocalAI.Client.Services;

public static class AppleReminderNotice
{
    /// <summary>Apple 给那个占位清单起的名字里带这个警告号。集合层判定用它。</summary>
    public const char WarnSign = '⚠';

    /// <summary>与语言无关的标记:公告条目的描述里指向 Apple 的这篇支持文档。</summary>
    static readonly string[] DocMarkers = { "support.apple.com/HT210220", "support.apple.com/en-us/102457", "HT210220" };

    /// <summary>已确认的本地化标题(en / zh-Hans)。★ 兜底用 —— 换个语言就会漏,所以不是唯一判据。</summary>
    static readonly string[] KnownTitles =
    {
        "where are my reminders",
        "the creator of this list has upgraded these reminders",
        "在哪里可以找到我的提醒事项",
        "此列表的创建者已升级这些提醒事项",
    };

    /// <summary>这个 VTODO 是不是 Apple 的「已升级」公告(而不是用户真正的待办)。</summary>
    public static bool IsUpgradeNotice(string? summary, string? description)
    {
        // B:与语言无关的支持链接
        if (!string.IsNullOrEmpty(description))
            foreach (var m in DocMarkers)
                if (description.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;

        // C:已知标题(归一化后比对 —— 中文串用的是全角问号,大小写与标点都要抹平)
        if (string.IsNullOrWhiteSpace(summary)) return false;
        var s = Normalize(summary);
        foreach (var t in KnownTitles)
            if (s.Contains(t, StringComparison.Ordinal)) return true;

        return false;
    }

    /// <summary>集合层判定:清单名带 ⚠ 就是 Apple 的占位清单。</summary>
    public static bool IsUpgradedList(string? displayName)
        => !string.IsNullOrEmpty(displayName) && displayName.Contains(WarnSign);

    /// <summary>抹平大小写与全角/半角标点,好让串表比对不因标点而漏。</summary>
    static string Normalize(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        // 全角 -> 半角(只处理会出现在这两句里的那几个)
        t = t.Replace('？', '?').Replace('！', '!').Replace('。', '.').Replace('，', ',');
        return t.TrimEnd('?', '.', '!', ' ');
    }

    /// <summary>给用户看的那句实话 —— 说清"拿不到"而不是"没有"。</summary>
    public const string Explain =
        "这个提醒事项清单已被 Apple【升级】到新格式(iOS 13 / macOS Catalina 起),"
        + "升级后 Apple 不再通过 CalDAV 提供提醒事项 —— 我们这条路【拿不到】你的真实内容,"
        + "清单里只剩 Apple 的两条公告(已自动排除,没有当成待办导入)。\n"
        + "★ 这个升级是账号级且【不可逆】的,新建清单也不会重新出现在 CalDAV。\n"
        + "要在 Windows 上看提醒事项,目前只能用浏览器打开 iCloud.com;"
        + "若想让待办真正同步进来,可以在 iPhone 上添加一个【非 iCloud 的 CalDAV 账号】"
        + "(如 Nextcloud / Fastmail),在那类账号里建的清单仍是标准格式,我们能正常读取。";
}
