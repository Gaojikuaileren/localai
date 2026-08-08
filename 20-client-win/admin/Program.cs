// V14 -- 管理端入口。两条启动路径(裁定第 1、2 条):
//   localai-admin            双击图标 -> 正常显示界面(★ 不启动客户端)
//   localai-admin --tray     由主机客户端拉起 -> 直接进托盘,不弹窗、不抢焦点
//
// ★★ 用户澄清(2026-08-08):裁定里那句「管理员和普通用户都可以进」的原意是
//   **「不需要以管理员身份运行」** —— 双击就能开、不弹 UAC、不用右键"以管理员身份运行"。
//   ⇒ 本程序**不请求提权**(清单里没有 requireAdministrator),并且和客户端一样
//     扛得住被人手动提权启动:先问"设备密钥打不打得开",打不开就自己降权重开。
//   ★ 理由与 D46 完全相同:TPM/CNG 用户密钥绑定**铸造时**的完整性等级,
//     而管理端要去起客户端与整套栈 —— 它一旦在 High 上跑,子进程全都继承 High。

using LocalAI.Admin.Services;
using LocalAI.Client.Services;

namespace LocalAI.Admin;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // ── D46 同款护栏(理由见文件头)────────────────────────────────
        // ★★ 先问**真问题**:密钥打不打得开。打得开就放行 —— 无论完整性等级是什么。
        //   拿"是不是管理员"当判据,在 UAC 关闭的机器(EnableLUA=0)上恒为真,
        //   会把一台完全健康的机器判成不能用,而且理由是假的(ASSERTION-PITFALLS 第 9 条)。
        if (Elevation.IsElevated() && !Elevation.DeviceKeyUsable(out var keyNote))
        {
            // ★ 只许试一次:客户端那边实测过"降权重开出来的进程仍然是 High"会变成无限重开。
            const string relaunchedFlag = "--relaunched-as-user";
            var alreadyTried = Array.IndexOf(args, relaunchedFlag) >= 0;
            if (!alreadyTried && Elevation.TryRelaunchAtMediumIntegrity(
                    args.Append(relaunchedFlag).ToArray())) return 0;
            System.Windows.MessageBox.Show(
                "管理端不能以管理员身份运行。" + Environment.NewLine + Environment.NewLine
                + "本机的设备密钥绑定在你的普通用户身份上,提权运行会打不开它;"
                + "而管理端要去起客户端与整套栈,它一旦在管理员身份上跑,子进程会全部继承。"
                + Environment.NewLine + Environment.NewLine
                + (alreadyTried
                    ? "(已经自动以普通身份重开过一次,结果仍然是管理员身份 —— 这台机器上这条路不通,请直接双击图标打开。)"
                    : "(试过自动以普通身份重开,没成功:" + Elevation.RelaunchNote + ")")
                + Environment.NewLine + "(设备密钥:" + keyNote + ")",
                "本地 AI · 主机管理端",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Stop);
            return 3;   // 与客户端 / lan-edge 的护栏同一退出码
        }

        // ── 单实例 ─────────────────────────────────────────────────────
        // ★ 排他按**用户**(锁文件),唤醒按**会话**(命名事件)—— 见 InstanceLock 顶部。
        using var instance = InstanceLock.Acquire(AdminPaths.StateDir, AdminPaths.AppKey);
        if (!instance.IsFirst)
        {
            // 已经有一个在跑 -> 叫醒它的窗口,自己安静退出。
            // ★ 这一条同时满足裁定第 1 条与第 2 条的交叉情形:客户端已经把管理端拉起到托盘时,
            //   用户再双击图标,应当是**把那个已经在跑的面板打开**,而不是开出第二个。
            instance.SignalExisting();
            return 0;
        }

        var startHidden = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        var app = new App(instance, startHidden);
        app.InitializeComponent();
        return app.Run();
    }
}
