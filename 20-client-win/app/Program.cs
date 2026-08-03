// P3c -- 客户端入口。两条路径:
//   localai-client              正常启动(WPF 窗口)
//   localai-client --tray       启动但直接进托盘(开机自启用这个,不打扰登录)
//   localai-client --selftest   **无界面**自检,给自动化回归用(项目习惯:PASS=n FAIL=0)
//
// 自定义 Main 的原因:WPF 默认由 App.xaml 生成入口,但我们需要在建窗口之前先做单实例判断
// 与 selftest 分流。因此 csproj 里把 App.xaml 降为 Page,由这里显式 InitializeComponent + Run。

using System.Runtime.InteropServices;
using LocalAI.Client.Services;

namespace LocalAI.Client;

public static class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] static extern bool AllocConsole();
    const int AttachParentProcess = -1;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            // WinExe 没有控制台:先蹭调用者的,蹭不到就自己开一个,否则自检输出无处可去。
            if (!AttachConsole(AttachParentProcess)) AllocConsole();
            return Selftest.Run();
        }

        // 转盘渲染诊断(调试用):--wheeltest <输出目录>
        var wt = Array.IndexOf(args, "--wheeltest");
        if (wt >= 0)
        {
            if (!AttachConsole(AttachParentProcess)) AllocConsole();
            var outDir = wt + 1 < args.Length ? args[wt + 1] : ".";
            return WheelTest.Run(outDir);
        }

        // D46 护栏:提权运行会打不开设备密钥(TPM/CNG 用户密钥绑定创建时的完整性等级)。
        // ★ 用户裁定(2026-08-03):「以管理员启动也要能直接用」—— 所以【不拒绝,先自己降权重开】。
        //   密钥模型一点没动:进程只是落到它本来就该在的那个 Medium 上下文里。
        //   重开不成才回落到拒绝 —— 绝不"假装重开了"然后继续在 High 上跑。
        if (Elevation.IsElevated())
        {
            if (Elevation.TryRelaunchAtMediumIntegrity(args)) return 0;
            System.Windows.MessageBox.Show(
                Elevation.RefuseMessage + Environment.NewLine + Environment.NewLine
                    + "(试过自动以普通身份重开,没成功:" + Elevation.RelaunchNote + ")",
                "本地 AI 中枢", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Stop);
            return 3;   // 与 lan-edge 的护栏同一退出码
        }

        using var instance = SingleInstance.Acquire();
        if (!instance.IsFirst)
        {
            instance.SignalExisting();   // 已有实例 -> 叫醒它的窗口,自己安静退出
            return 0;
        }

        var startHidden = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        var app = new App(instance, startHidden);
        app.InitializeComponent();
        return app.Run();
    }
}
