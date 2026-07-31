// P3c -- 虚拟声卡安装包的【版本清单】。
//
// ★★ 这份清单的唯一理由是那串 SHA-256。没有可信的哈希,自动下载就等于"从网上抓个 exe 就跑",
//   那是这套方案里唯一能把整台机器搭进去的地方。所以:
//   · 哈希为空 = 清单无效 = 【不允许自动下载】,界面退回"请自备安装包";
//   · 哈希对不上 = 下载的文件当场删除,不给"仍然继续"的选项。
//
// ★ 清单可以被覆盖:{state}\audio-driver\manifest.json 存在就用它,否则用内置的。
//   这样换版本、换镜像不用重新发客户端,而校验规则一点没松。
//
// ★ 内置清单【故意留空哈希】:我没有在这台机器上核对过官方安装包的 SHA-256,
//   凭印象写一串十六进制不是"默认值",是伪造证据 —— 那比没有更危险。
//   在核对之前,自动下载保持关闭,界面如实说明,走离线安装。

namespace LocalAI.Client.Services;

public static class AudioDriverManifest
{
    /// <summary>用户可覆盖的清单位置。</summary>
    public static string Path => System.IO.Path.Combine(AudioDriver.OfflineDir, "manifest.json");

    static AudioDriverPackage? _cached;
    static bool _loaded;

    public static AudioDriverPackage? Current
    {
        get
        {
            if (_loaded) return _cached;
            _loaded = true;
            _cached = Load();
            return _cached;
        }
    }

    /// <summary>重新读一次(用户刚放了新清单)。</summary>
    public static void Reload() { _loaded = false; _cached = null; }

    static AudioDriverPackage? Load()
    {
        // ① 用户自备的清单优先
        var user = ClientStore.Load<AudioDriverPackage>(Path);
        if (IsUsable(user)) return user;

        // ② 内置清单。★ 信任模型改为【Authenticode 签名】(用户裁定 2026-07-31):
        //    提权运行安装程序前,验证它由 VB-Audio 官方签发(见 Authenticode + AudioDriver.RunInstaller)。
        //    所以下载不再需要预先钉死哈希 —— 哈希退为【可选的额外一层】(清单里填了才多比对一次)。
        //    下载来源仍锁死在官方 https 域(见 IsUsable),防清单被本地文件改成任意 URL。
        var builtin = new AudioDriverPackage(
            Version: "官方最新",
            Url: "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack.zip",
            Sha256: "",                       // 留空 = 不额外比对哈希;运行前的把关交给 Authenticode 签名
            Bytes: 0,
            ManifestDate: new DateTime(2026, 7, 31));
        return IsUsable(builtin) ? builtin : null;
    }

    /// <summary>
    /// 清单可用 = 版本、地址、哈希三者齐全,【且下载地址落在编译进程序的白名单里】。
    /// ★ 白名单是关键(2026-07-31 审计):用户自备的 manifest.json 会压过内置清单,而它同时带 Url + Sha256 ——
    ///   一个本地明文文件就能把下载地址改成任意 URL、期望哈希改成那份恶意包的哈希(自证自洽必过校验),
    ///   随后提权运行。哈希闸只保证"下的和清单说的一致",挡不住"清单本身被换掉"。
    ///   所以下载来源必须钉死在 VB-Audio 官方域;用户清单只能换版本/换官方镜像,不能换信任来源。
    /// </summary>
    static readonly string[] AllowedHosts = { "download.vb-audio.com", "vb-audio.com", "www.vb-audio.com" };

    public static bool IsUsable(AudioDriverPackage? p)
        => p is not null
           && !string.IsNullOrWhiteSpace(p.Version)
           && !string.IsNullOrWhiteSpace(p.Url)
           // ★ 不再要求 Sha256 非空 —— 运行前的把关交给 Authenticode 签名(用户裁定 2026-07-31)。
           //   哈希若填了会作为额外一层比对(见 AudioDriver.DownloadAsync),没填也能下。
           && Uri.TryCreate(p.Url, UriKind.Absolute, out var u)
           && u.Scheme == Uri.UriSchemeHttps                       // 不走明文 http
           && AllowedHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase);
}
