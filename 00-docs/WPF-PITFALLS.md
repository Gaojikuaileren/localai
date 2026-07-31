# WPF 反复踩的坑 — 写下来是为了不再犯

> 用户要求(2026-07-31):把反复踩的坑写进文件。
> 收录标准:**在这个项目里真的踩过、而且不止一次**,或者**一次就让程序打不开**。
> 每条都写清:症状 / 为什么会这样 / 正确写法 / 有没有护栏。
> 排在最前面的是【代价最大】的那条。

---

## 1. ★★★ 一个元素不能有两个逻辑父级 —— 已踩 5 次,最近一次让程序打不开

**症状**
`System.InvalidOperationException: 指定的元素已经是另一个元素的逻辑子元素。请先将其断开连接。`
如果它发生在**构造期**(比如 `HomeView` 的构造函数里),后果不是某块界面坏掉,而是
**整个客户端启动即崩、根本打不开**。

**为什么会这样**
WPF 里每个元素只能挂在一个父容器下。最容易中招的写法是
「先把它交给了 A,后来又交给 B」,而**中间隔了几十行**,写的时候完全想不起来:

```csharp
var card = new Border { Child = body };     // body 的父级已经是 card
...
var host = new Grid();
host.Children.Add(body);                    // ★ 当场抛 —— body 还挂在 card 上
host.Children.Add(gripZone);
card.Child = host;
```

**正确写法 —— 先断开,再改挂**
```csharp
card.Child = null;                          // ★ 先断开
var host = new Grid();
host.Children.Add(body);
host.Children.Add(gripZone);
card.Child = host;
```
更好的做法是**一开始就按最终结构搭**:先把 `host` 组装好,再 `new Border { Child = host }`,
根本不出现"改挂"这一步。

**历史(每一次都是同一个形状)**
| # | 位置 | 表现 |
|---|---|---|
| 1 | HistoryBoardView 筛选开关 | 面板崩 |
| 2 | 收藏夹 chip 重建 | 抽屉崩 |
| 3 | `InterpretLayout()` 二次调用 `NotesCard()` | **整个翻译工作空间打不开** |
| 4 | 会话面板统一期 | 面板崩 |
| 5 | `HomeView.WeatherCard()` 手柄叠层(2026-07-31) | **整个客户端打不开** |

**护栏**:见第 2 条 —— 光靠"下次小心"是挡不住的,已经证明了五次。

---

## 2. ★★★ 构造期崩溃:业务断言一条也覆盖不到

**症状**
`selftest 1096 PASS / 0 FAIL`,而客户端**连启动都不行**。

**为什么会这样**
自检断言检查的是「逻辑对不对」,而**视图从来没有被真正构造过**。
一个在 `HomeView` 构造函数里抛的异常,对所有断言都是不可见的 ——
它们跑的时候根本没碰过那个类。

这与中枢侧那次一模一样:`gateway.py` 引用了一个不存在的模块,整整一天起不来,
而 6 个测试文件里能跑的那两个恰好都不 `import gateway`。
**"这东西根本起不来"这一类故障,只能靠专门的冒烟测试抓,业务测试永远抓不到。**

**护栏**
- 中枢:`10-core/gateway/test_imports.py` —— 扫目录逐个 import。
- 客户端:`Selftest` 里的**主要视图构造冒烟** —— 真的 `new` 一遍每个顶层视图,只断言「不抛」。
- `--wheeltest` 的整页渲染也能抓到,但它**要人主动去跑**;改完版面【必须】跑一次再说完事。

---

## 3. ★★★ 对象初始化器在**构造函数之后**才跑 —— 宿主配置属性会"设了没用"

**症状**
```csharp
var v = new CalendarView(Mode.Month) { HideDayArea = true, LeftGutter = 44 };
```
两个开关**一个都没生效**,界面上跟没写一样。

**为什么会这样**
C# 的对象初始化器是**先跑构造函数、再逐个赋值**。而这类组件的构造函数末尾通常有一次
`Rebuild()` —— 那也往往是**唯一**一次重建。等初始化器把值写进去,重建早就过去了。

难受的地方在于它**没有任何报错**,而且有时会"看起来对了"(比如别处的事件恰好触发了一次重建),
于是同一份代码在不同数据下表现不同,极难归因。

**正确写法**:凡是"会影响渲染"的公开属性,一律写成**改了就重建**,不要用自动属性:
```csharp
public bool HideDayArea
{
    get => _hideDayArea;
    set { if (_hideDayArea == value) return; _hideDayArea = value; Rebuild(); }
}
```
或者干脆做成构造函数参数。

---

## 4. ★★★ 无条件 `e.Handled = true` 会把父级的滚动**彻底堵死**

**症状**:光标停在某块内容上时,滚轮完全失灵 —— 那块自己不动,整页也不动。

**为什么会这样**:内层"自己要用滚轮"的控件先把事件吞了,而它**在边界上其实什么也没做**。
最阴的一种是**起手就已经在边界上**(比如缩放的默认值正好等于下限),于是开屏第一下就是死的。

**正确写法**:只有**真的动了**才吞:
```csharp
MouseWheel += (_, e) => e.Handled = Zoom(f, anchor);   // Zoom 返回"是否真的变了"
```
本项目 `Views/Wheel.cs` 的 `PassThrough()` 定的就是这条规矩:
**自己朝那个方向滚不动了,就把同一个滚轮事件重新抛给父级。**
新写的内层滚动/缩放区一律照办。

---

## 5. ★★★ 拖拽用「增量取整」→ 误差累加 + 反向死区

**症状**:慢慢拖时,被拖的边**跑得比光标快近一倍**;反向拖回去,前面一大段完全没反应。

**为什么会这样**:典型的错误写法是每帧算增量、四舍五入、施加,然后把基准挪到当前位置:
```csharp
var dh = (curY - FromY) / h * hours;
var snapped = Math.Round(dh / Snap) * Snap;
if (Math.Abs(snapped) < 0.001) return;    // ★ 提前 return,却【不更新 FromY】
Apply(snapped);
FromY = curY;                             // ★ 那半个颗粒的余量被吞掉了
```
不足半个颗粒时位移一路**攒**着,一旦跨过就施加**整整一个颗粒**,余量随即被清零 ——
误差**同号累加**。鼠标事件越密(也就是拖得越慢、越想拖准),偏得越狠。
越界/最小长度那几条守卫如果也写成 `return`,拖过头的位移会全部积在基准里,
回拖时得先"还"完才动 —— 那就是死区。

**正确写法**:**绝对口径** —— 记住按下那一刻的原始值与原始坐标,**全程不更新**,
每帧从原始值重算,守卫一律**夹住(Clamp)而不是 return**:
```csharp
var moved = (curY - FromY0) / h * hours;          // 从起手那一刻算起
var t = Math.Round((orig + moved) / Snap) * Snap; // ★ 对【绝对时刻】吸附
t = Math.Clamp(t, lo, hi);                        // ★ 夹住,不 return
```
额外好处:绝对吸附会把 9:07 这种脏时刻**一次性归到** 9:00/9:30 ——
增量吸附永远绕着自己的脏偏移转(9:07 → 9:37 → 10:07),等于把"颗粒"这条需求悄悄作废。

---

## 6. ★★ `Control.Focusable` 默认是 `true`

**症状**:Tab 焦点跑到显存条、板块容器这类根本不需要聚焦的东西上。

**为什么**:不只是按钮,`ContentControl` 这类纯容器天生也是 Tab 停靠点。
逐个去设 `IsTabStop=False` 是打地鼠,永远列不全。

**正确写法**:在窗口层把 WPF 自己的 Tab 导航整体关掉,焦点只由 `FocusPolicy` 白名单驱动:
```csharp
KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.None);
KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.None);
```
★ Tab 可以在隧道层(`PreviewKeyDown`)拦,**方向键不可以** ——
那会把输入框里的左右移光标、上下跨行、Home/End 全废掉。

---

## 7. ★★ `ContentControl` 的默认模板**不画** `Background`

**症状**:给覆盖层设了 `Background`,结果底下那一页整个透出来,连点击都穿透。

**为什么**:`ContentControl` 的默认模板只有一个 `ContentPresenter`,
它有 `Background` 属性但**从不绘制**。写了等于没写,而且没有任何警告。

**正确写法**:要底色和命中测试,就用 `Border`:
```xml
<Border Background="{DynamicResource BgWindow}">
  <ContentControl x:Name="Host"/>
</Border>
```

---

## 8. ★★ 跨主机重定向会**丢掉** `Authorization` 头

**症状**:凭据明明是对的,却一直 401,而且看起来像"密码错",极难归因。

**为什么**:`HttpClient` 在跨主机重定向时**按设计丢弃** `Authorization`(防凭据泄露给第三方主机)。
而 iCloud CalDAV 认证后正是要把你转到分区主机 `pNN-caldav.icloud.com`。

**正确写法**:关掉自动重定向,自己逐跳重建请求并重新挂凭据,
且**只跟随到白名单域**(见 `Services/AppleCalDav.cs` 的 `SendFollowingAsync`)。

---

## 9. ★ 主题模板里硬编码的 `Background` 会让局部 `Background` 失效

**症状**:给某个 `TextBox` 设了背景色,界面上却是灰底白字。

**为什么**:themed 模板里写死了 `Background`,局部设置被模板吃掉。

**正确写法**:需要"裸"控件时给它一个**光板模板**(见 `Theme/Controls.xaml` 的 `PlainTextBox`)。
★ 注意:一旦给控件指定了显式 `Style`,**隐式 Style 就完全不生效了** ——
该 Style 里要把需要的设定重新写一遍(`CaptionButton` 那次就是这么漏的)。

---

## 10. ★ `DoubleAnimation` + `AutoReverse` + `RepeatBehavior(2)` 停在**起始值**

**症状**:闪烁提示做完之后,那一块永久变灰。

**为什么**:动画结束后默认 `FillBehavior.HoldEnd`,而 AutoReverse 的"结束"正是**起始值**。

**正确写法**:要么 `FillBehavior.Stop`,要么干脆别用动画做高亮 ——
本项目改用了 `RevealHighlight`(Adorner 画虚线框,5 秒后自行消失)。

---

## 11. ★ 布局在光标底下重排 → 悬停状态乱跳

**症状**:鼠标移到折叠项上,它展开了,然后莫名其妙跳回默认项。

**为什么**:用**切换可见性**来展开/折叠 = 在光标底下把元素换掉。
元素易主的一瞬间 WPF 会抛一次 `MouseLeave`,立刻响应就会"啪地跳回去"。

**正确写法**
- 折叠用**高度动画 + `ClipToBounds`**,让摘要行**始终待在原处**,不换元素;
- `MouseLeave` **延迟一拍再实地确认** `IsMouseOver` 才动作 —— 只信一次瞬时事件必然误判;
- 悬停判定挂在**整张卡**上,不要只挂那一小条摘要行。

---

## 12. ★ `IsVisible` 对**离屏**的树是 `false`

**症状**:结构自检在离屏渲染下整段静默跳过,什么也没验到。

**正确写法**:自检里判断可见性用**声明的 `Visibility`**,不要用 `IsVisible`。

---

## 13. ★ `BringIntoView` 在 `Loaded` 优先级下还没有布局

**症状**:跳转到某个设置项,结果只滚到了顶部。

**正确写法**:排到 `DispatcherPriority.ContextIdle`,并自己算居中偏移(见 `CenterInView`)。

---

## 附:提交前的自问清单

- [ ] 改了版面 → 跑过 `--wheeltest` 并**真的看了图**了吗?
- [ ] 新加的元素有没有"先挂 A 再挂 B"?(第 1 条)
- [ ] 新加的顶层视图有没有进构造冒烟?(第 2 条)
- [ ] 动画结束后停在哪个值,确认过吗?(第 10 条)
- [ ] 悬停/焦点相关的改动,有没有"布局在光标底下重排"?(第 11 条)
