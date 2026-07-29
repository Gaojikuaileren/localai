// P3c -- 完整性等级护栏(决议 D46)。
//
// 教训来自 P3b 实机上线:TPM/CNG 的用户密钥**绑定创建它的进程完整性等级**。
// 身份是在普通用户(Medium)下铸的,于是提权(High)进程打不开,报「密钥集不存在」——
// 这个坑当时烧掉了一整轮排查。客户端持有设备证书私钥,同样必须始终 Medium。
//
// ★ 用 Mandatory Label SID 直接判**完整性等级本身**,而不是 IsInRole(Administrator) ——
//   后者只是等级的代理:管理员账户在 UAC 过滤令牌下运行时它为 false,而
//   "以管理员身份运行"的普通程序其实也可能出现判断偏差。SID 才是权威。

using System.Security.Principal;

namespace LocalAI.Client.Services;

public static class Elevation
{
    // S-1-16-8192 = Medium(普通)· 12288 = High(管理员)· 16384 = System
    const string HighSid = "S-1-16-12288";
    const string SystemSid = "S-1-16-16384";

    /// <summary>当前进程是否运行在 High 及以上完整性等级(= 会打不开身份密钥)。</summary>
    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            foreach (var g in id.Groups ?? Enumerable.Empty<IdentityReference>())
            {
                var v = g.Value;
                if (v == HighSid || v == SystemSid) return true;
            }
            return false;
        }
        catch
        {
            // 判不出来时**不**阻断:宁可放行也不要让用户开不了应用(这不是安全边界,是防呆)。
            return false;
        }
    }

    public const string RefuseMessage =
        "本程序不能以管理员身份运行。\n\n" +
        "本机的设备密钥绑定在你的普通用户身份上,提权运行会打不开它(症状是「密钥集不存在」)。\n" +
        "请关掉这个窗口,用普通方式(双击图标)重新打开。";
}
