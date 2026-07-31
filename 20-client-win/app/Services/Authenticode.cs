// P3c -- Authenticode 签名验证。同传的虚拟声卡(VB-CABLE)是【第三方内核驱动】,
// 装它要提权、不可逆。用户裁定(2026-07-31):提权运行安装程序【之前】,必须确认这个 exe
// 确实由「VB-Audio Software」签发、证书链有效 —— 这是 Windows 验证"它真是 VB-Audio 出的"
// 的标准手段,由系统证书库背书,比我们自己算一个哈希强(哈希只能防"下载后被改",
// 挡不住"一开始下的就是假的";而签名验证的是【出品方身份】)。
//
// 两道关,都要过:
//   ① WinVerifyTrust(WINTRUST_ACTION_GENERIC_VERIFY_V2)—— 签名有效 + 证书链通到受信任根;
//   ② 签名者证书的主体(Subject)含 "VB-Audio" —— 是它,不是别的合法签名者。
//
// ★ 只读、不改任何东西;失败就【拒绝运行】,绝不"验不了就放行"(与安装包哈希闸同一口径)。

using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace LocalAI.Client.Services;

public static class Authenticode
{
    /// <summary>
    /// 这个文件是否由 VB-Audio 官方签名、且签名有效可信。
    /// </summary>
    /// <param name="signer">带出签名者主体(给界面如实显示"由谁签的");失败时是错误原因。</param>
    public static bool VerifySignedByVbAudio(string path, out string signer)
    {
        signer = "";
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            signer = "文件不存在";
            return false;
        }

        // ① 信任验证:签名有效 + 链通到受信任根
        var trust = VerifyTrust(path);
        if (trust != 0)
        {
            signer = trust switch
            {
                0x800B0100 => "没有数字签名",             // TRUST_E_NOSIGNATURE
                0x800B0109 => "签名的根证书不受信任",       // CERT_E_UNTRUSTEDROOT
                0x800B010C => "签名证书已被吊销",           // CERT_E_REVOKED
                0x80096010 => "文件被改动过,签名失效",      // TRUST_E_BAD_DIGEST
                _ => $"签名验证未通过(0x{trust:X8})",
            };
            return false;
        }

        // ② 出品方:签名者主体必须是【VB-Audio 的已知签名身份】。
        //   ★ 实测(2026-07-31,下了官方 Pack45 验的):VB-CABLE 的安装程序由
        //     「BUREL VINCENT Entrepreneur individuel」签发 —— 这是 VB-Audio 创始人 Vincent Burel
        //     以法国个体户身份签的,证书主体【不含 "VB-Audio"】。所以只认 "VB-Audio" 会把【真安装包】拒之门外。
        //   这几个已知身份放一起认;都不含则拒。将来他换成公司名重签,把新主体加进来即可。
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            signer = cert.Subject;
            var ok = AcceptedSigners.Any(s => cert.Subject.Contains(s, StringComparison.OrdinalIgnoreCase));
            if (!ok)
            {
                signer = $"签名有效,但出品方不是 VB-Audio:{cert.Subject}";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            signer = "读取签名者证书失败:" + ex.Message;
            return false;
        }
    }

    /// <summary>VB-Audio 的已知代码签名身份(证书主体里含其一即认)。见 VerifySignedByVbAudio ②。</summary>
    static readonly string[] AcceptedSigners =
    {
        "BUREL VINCENT",   // VB-Audio 创始人 Vincent Burel · 个体户 —— 当前实际签名主体
        "Vincent Burel",
        "VB-Audio",        // 兼容:某些产品/将来若以此名重签
    };

    // ---------------------------------------------------------------- WinVerifyTrust 互操作
    static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    const uint WTD_UI_NONE = 2;
    const uint WTD_REVOKE_NONE = 0;          // 不查吊销(要联网;链有效 + 出品方核对已足够,且要能离线装)
    const uint WTD_CHOICE_FILE = 1;
    const uint WTD_STATEACTION_VERIFY = 1;
    const uint WTD_STATEACTION_CLOSE = 2;
    const uint WTD_SAFER_FLAG = 0x100;

    /// <returns>0 = S_OK(可信);其余为 HRESULT 错误码。</returns>
    static uint VerifyTrust(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
        };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        Marshal.StructureToPtr(fileInfo, pFile, false);

        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = WTD_CHOICE_FILE,
            pFile = pFile,
            dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = WTD_SAFER_FLAG,
        };
        var pData = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
        Marshal.StructureToPtr(data, pData, false);

        try
        {
            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            var result = WinVerifyTrust(IntPtr.Zero, action, pData);

            // 必须再调一次 CLOSE 释放 WinVerifyTrust 内部分配的状态,否则泄漏
            data = Marshal.PtrToStructure<WINTRUST_DATA>(pData);
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            Marshal.StructureToPtr(data, pData, false);
            WinVerifyTrust(IntPtr.Zero, action, pData);

            return result;
        }
        catch
        {
            return 0x80004005;   // E_FAIL:互操作本身出错也当作不可信
        }
        finally
        {
            Marshal.FreeHGlobal(pFile);
            Marshal.FreeHGlobal(pData);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
