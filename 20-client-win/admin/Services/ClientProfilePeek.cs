// V21 -- 管理端**只读**看一眼客户端的配对档案。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ 纪律③(每个 json 只能有一个写者)在这里的样子:**读可以,写绝对不行。**
//
//  `profile.json` 归**客户端**独占 —— 它是客户端配对时写的,里面有设备证书与拨号地址。
//  本文件只有 `File.ReadAllText` + `JsonDocument`,**一个写入 API 都没有**,
//  而这一点由元断言钉着(「每个 json 在两个工程里合起来只能有一个写点」)。
//
//  ★ 已经有先例:管理端读客户端的 `settings.json`(裁定③ 皮肤同步)与它的锁文件。
//    `AdminPaths.cs` 顶部那句写得很清楚:「读别人的目录是对的,写才不对。」
// ══════════════════════════════════════════════════════════════════════════════
//
// ★★ 为什么**必须**读它,不能省(两处都是承重的):
//   ① `HubAdmin.ProbeAsync(expectHubId)` —— 只连得上还不够,要核对它自报的 hubId
//      是不是本机这套身份。不核对的话,同机跑着另一个中枢时管理端会认错东家
//      (`AdminProbeResult.WrongHub` 那一档会永远走不到);
//   ② 「自己不能解除自己」(D47 用户裁定)—— 设备表里哪一条是**这台主机自己**,
//      判据是设备证书指纹,而那张证书在客户端的档案里。
//      读不到就**保守地当作"不是自己"**? 不行 —— 那会给主机自己那条摆上解除按钮。
//      ⇒ 读不到时如实说读不到,并且**不显示解除按钮**(见 HostHubView.IsThisMachine)。
//
// ★ 读不到不是错误:主机上的客户端可能还没配过对(那时确实没有档案)。

using System.Text.Json;
using LocalAI.Client.Services;

namespace LocalAI.Admin.Services;

public static class ClientProfilePeek
{
    /// <summary>客户端配对档案的位置。★ 与 `ClientTransport` 写它的地方是同一个路径推导。</summary>
    public static string ProfilePath => Path.Combine(AppPaths.StateDir, "profile.json");

    static JsonElement? Read()
    {
        try
        {
            if (!File.Exists(ProfilePath)) return null;
            return JsonDocument.Parse(File.ReadAllText(ProfilePath)).RootElement.Clone();
        }
        catch { return null; }   // 读不动 / 不是 JSON ⇒ 当作没有,**不猜**
    }

    static string? Str(string name)
    {
        if (Read() is not { } r) return null;
        // ★ 大小写两种都试:`ClientProfile` 用的是 PascalCase 属性名,而 JSON 序列化器
        //   的命名策略将来可能变。**试两次**比赌一次稳,而且读错了只会返回 null(不编)。
        foreach (var key in new[] { name, char.ToLowerInvariant(name[0]) + name[1..] })
            if (r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    /// <summary>这台主机上的客户端配到的是哪个中枢;null = 还没配过对 / 读不到。</summary>
    public static string? HubId() => Str("HubId");

    /// <summary>这台主机上的客户端的设备证书(base64 DER);null = 还没配过对 / 读不到。</summary>
    public static string? DeviceCertB64() => Str("DeviceCertB64");

    /// <summary>这台主机上的客户端的拨号地址;null = 还没配过对 / 读不到。</summary>
    public static string? Dial() => Str("Dial");

    /// <summary>主机上的客户端配过对了吗。★ 判据是**档案里真有证书**,不是"文件在不在"。</summary>
    public static bool ClientPaired() => !string.IsNullOrWhiteSpace(DeviceCertB64());
}
