// P3c -- 「当前使用者」的推测(仅用于显示)。
//
// 用户裁定(2026-07-30):左下角那一格显示【推测出来的当前使用者】,不是连接状态 ——
//   连接状态只在右边的 token 块里出现,不要两处都写"未连接中枢"。
//   主机没连上时,沿用【上次推测的缓存】,而不是退化成"未连接"。
//
// ★★ 铁律(gateway.py:227 / D45):这只是【显示】。任何可见范围/权限判定【绝不】读这里 ——
//    主体只来自主机的成员表(见 MemberContext)。这个类连 MemberContext 都不碰。
//
// ★ 诚实:真正的身份识别要靠【另一套判定框架】(用户说的:如 AI 识别等),那套还没有。
//   在它到位之前,这里的来源只有三种,并且【每种都如实标注来源】,不冒充"已识别":
//     Hub    —— 主机在线并下发了成员显示名(最可信)
//     Cache  —— 上次主机给过、这次没连上(沿用缓存)
//     Local  —— 从没连上过,退回本机 Windows 账户名 —— 这只是【猜的】

namespace LocalAI.Client.Services;

public enum IdentitySource { Hub, Cache, Local }

public sealed record IdentityGuessResult(string DisplayName, IdentitySource Source)
{
    /// <summary>是不是"猜的"(非主机下发)—— 界面据此弱化显示,不让人误以为已确认身份。</summary>
    public bool IsGuess => Source != IdentitySource.Hub;

    public string SourceNote => Source switch
    {
        IdentitySource.Hub => "由主机成员表确认",
        IdentitySource.Cache => "沿用上次识别结果(主机未连上)",
        _ => "按本机账户推测(尚未与主机确认)",
    };
}

public static class IdentityGuess
{
    /// <summary>
    /// 推测当前使用者。主机在线且给过显示名 -> 用它并【写入缓存】;
    /// 否则沿用缓存;都没有就退回本机账户名(明确标注是推测)。
    /// </summary>
    public static IdentityGuessResult Current(HubClient hub, AppSettings settings)
    {
        var cached = settings.CachedMemberDisplayName;

        if (hub.State == HubState.Online && !string.IsNullOrWhiteSpace(cached))
            return new IdentityGuessResult(cached!, IdentitySource.Hub);

        if (!string.IsNullOrWhiteSpace(cached))
            return new IdentityGuessResult(cached!, IdentitySource.Cache);   // ★ 连不上就沿用上次的,不显示"未连接"

        // 从没被主机确认过 —— 退回本机 Windows 账户名,并如实说明这是推测
        var local = Environment.UserName;
        return new IdentityGuessResult(string.IsNullOrWhiteSpace(local) ? "本机用户" : local, IdentitySource.Local);
    }

    /// <summary>主机下发成员显示名时调用:存进缓存,供以后离线时沿用。</summary>
    public static void Remember(AppSettings settings, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName) || settings.CachedMemberDisplayName == displayName) return;
        settings.CachedMemberDisplayName = displayName;
        settings.Save();
    }
}
