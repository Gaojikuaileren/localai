// P3c -- 主页(= 今天)。设计 §4 的信息层级:问候/简报 → 天气与时间 → 进行中的项目/待办 → 任务架与设备。
// 本轮只落地【外壳 + 诚实占位】:天气要等出境白名单(设计 §11「不能」清单),日历必须显示"未连接"
// 且**绝不伪造同步成功**(§4.5 / 状态矩阵 §8)。时间是本地时钟,现在就能真实显示。

using System.Windows.Controls;
using System.Windows.Threading;
using LocalAI.Client.I18n;

namespace LocalAI.Client.Views;

public sealed class HomeView : UserControl
{
    static readonly (string City, string Tz)[] Clocks =
    {
        ("科隆",   "W. Europe Standard Time"),
        ("武汉",   "China Standard Time"),
        ("札幌",   "Tokyo Standard Time"),
        ("纽约",   "Eastern Standard Time"),
    };

    readonly TextBlock _clockLine = Ui.Body("");
    readonly DispatcherTimer _timer;

    public HomeView()
    {
        Content = Ui.Page(
            Ui.Title(Strings.Get("nav.today")),

            // 四地时间:设计 §4.1 —— 纽约无天气,故时间与天气**分层**,四地在时间条里地位平等。
            Ui.Card(Ui.Stack(
                Ui.Subtitle("时间"),
                _clockLine,
                Ui.Caption("时区按 IANA/Windows 时区库解析,冬夏令时由系统处理(不写死 UTC 偏移)。")
            )),

            // 天气:诚实占位。设计 §11 明确天气要等"出境白名单"落地,现在不显示任何数字。
            Ui.Card(Ui.Stack(
                Ui.Subtitle("天气(科隆 · 武汉 · 札幌)"),
                Ui.Body("尚未接入。", muted: true),
                Ui.Caption("需要先落地固定白名单出站端点(只发预配置坐标、不发文字地址)。接入前不显示任何数值,也不显示过期缓存冒充实时。")
            )),

            // 日历:状态矩阵 §8 规定的固定文案,不可伪造
            Ui.Card(Ui.Stack(
                Ui.Subtitle("日历"),
                Ui.Body(Strings.Get("calendar.not_connected"), muted: true)
            ))
        );

        UpdateClocks();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateClocks();
        // 视图被换掉时停表,否则每个建过的 HomeView 都在后台空转(白耗电)。
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    void UpdateClocks()
    {
        var now = DateTimeOffset.UtcNow;
        var parts = new List<string>();
        foreach (var (city, tz) in Clocks)
        {
            try
            {
                var z = TimeZoneInfo.FindSystemTimeZoneById(tz);
                var t = TimeZoneInfo.ConvertTime(now, z);
                parts.Add($"{city} {t:HH:mm}");
            }
            catch { parts.Add($"{city} —"); }   // 时区库缺条目就明说取不到,不猜
        }
        _clockLine.Text = string.Join("    ", parts);
    }
}
