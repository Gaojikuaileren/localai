// P3c -- Apple 账号凭据的本机保管(日历接入用)。
//
// 存什么:Apple ID(邮箱)+【专用密码 app-specific password】。
// ★ 为什么必须是专用密码而不是账号密码:Apple ID 开了两步验证之后,CalDAV 这类基本认证
//   【只认专用密码】,账号密码会被直接拒。专用密码在 appleid.apple.com 生成,可随时单独吊销,
//   吊销它不影响账号本身 —— 这也是我们只要它、不碰账号密码的原因。
//
// ★★ 保管方式:Windows DPAPI(CurrentUser 作用域)。
//   · 密文只有【当前 Windows 用户】能解开,换个用户/换台机器拷过去都打不开;
//   · 不需要提权(D46:客户端一律普通用户运行),不需要 TPM 仪式;
//   · 明文【只在内存里存在于一次请求期间】,不写日志、不进 crash.log(见 Redact)。
//
// ★ 落点在客户端自己的 state 目录,与主机的 {state}/secrets 无关(那边是 CA 私钥,另一套)。

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalAI.Client.Services;

/// <param name="AppleId">Apple ID(邮箱)。可显示。</param>
/// <param name="HasPassword">是否已保存专用密码(★ 明文永不出现在这个记录里)。</param>
public sealed record AppleAccountInfo(string AppleId, bool HasPassword);

public static class AppleCredentials
{
    static string Path_ => System.IO.Path.Combine(AppPaths.StateDir, "apple-account.json");

    /// <summary>DPAPI 的附加熵 —— 换一个应用/换一份文件就解不开,降低被别的程序顺手解密的可能。</summary>
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LocalAI/apple-caldav/v1");

    sealed class Stored
    {
        public string AppleId { get; set; } = "";
        /// <summary>DPAPI 密文的 Base64。★ 绝不是明文。</summary>
        public string PasswordProtected { get; set; } = "";
    }

    /// <summary>读当前账号(不含密码)。没配过返回 null。</summary>
    public static AppleAccountInfo? Load()
    {
        try
        {
            if (!File.Exists(Path_)) return null;
            var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(Path_));
            if (s is null || string.IsNullOrWhiteSpace(s.AppleId)) return null;
            return new AppleAccountInfo(s.AppleId, !string.IsNullOrEmpty(s.PasswordProtected));
        }
        catch { return null; }
    }

    /// <summary>存账号 + 专用密码(密码经 DPAPI 加密后才落盘)。</summary>
    public static bool Save(string appleId, string appPassword)
    {
        try
        {
            AppPaths.EnsureStateDir();
            // ★ 专用密码 Apple 生成时带连字符(xxxx-xxxx-xxxx-xxxx),用户多半连着连字符一起粘过来。
            //   实际提交时要不要去掉连字符由调用方决定 —— 这里【原样保存】,不替用户猜。
            var blob = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(appPassword), Entropy, DataProtectionScope.CurrentUser);
            var s = new Stored { AppleId = appleId.Trim(), PasswordProtected = Convert.ToBase64String(blob) };
            File.WriteAllText(Path_, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 取出明文专用密码 —— 只在【发起一次请求】时调用,用完即弃,绝不缓存进字段、绝不写日志。
    /// 解不开(换了 Windows 用户 / 文件被拷来的)返回 null,调用方据此提示"请重新填写"。
    /// </summary>
    public static string? Reveal()
    {
        try
        {
            if (!File.Exists(Path_)) return null;
            var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(Path_));
            if (s is null || string.IsNullOrEmpty(s.PasswordProtected)) return null;
            var plain = ProtectedData.Unprotect(
                Convert.FromBase64String(s.PasswordProtected), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; }   // DPAPI 解不开 = 不是本机本用户加密的
    }

    /// <summary>断开连接:删掉本机保存的账号与密码。★ 不碰 Apple 那边的任何东西。</summary>
    public static void Clear()
    {
        try { if (File.Exists(Path_)) File.Delete(Path_); } catch { }
    }

    /// <summary>
    /// 把可能含密码的文本抹掉再交给日志/界面。
    /// ★ 存在的理由:异常消息里常常带着请求头(含 Authorization: Basic ...),
    ///   照原样写进 crash.log 就等于把专用密码明文留在磁盘上。
    /// </summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var t = text;
        // Basic 认证头
        t = System.Text.RegularExpressions.Regex.Replace(
            t, @"(?i)\bBasic\s+[A-Za-z0-9+/=]+", "Basic ***");
        // Apple 专用密码的形状:xxxx-xxxx-xxxx-xxxx
        t = System.Text.RegularExpressions.Regex.Replace(
            t, @"\b[a-z]{4}-[a-z]{4}-[a-z]{4}-[a-z]{4}\b", "****-****-****-****");
        return t;
    }
}
