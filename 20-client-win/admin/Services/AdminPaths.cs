// V14 -- 管理端自己的本机状态位置。
//
// ★★ 真正的定义在 `..\app\Services\AppPaths.cs`(本项目 csproj link 了它),**不在这里**。
//   本文件只是个转名字的门面。为什么这么绕:客户端也要用同一个目录与应用键
//   (它要探"管理端在不在跑",裁定第 1 条起之前先看一眼),
//   而两边各定义一份的那一天,表现是**每次都起出第二个管理端**,且两边各自都"对"。
//   ⇒ 定义只留一份,两个 csproj 编译同一个文件。
//
// ★ 管理端仍然要**读**客户端的目录:皮肤同步读它的 settings.json(裁定③),
//   探"客户端在不在跑"读它的锁文件。读别人的目录是对的,写才不对。

using LocalAI.Client.Services;

namespace LocalAI.Admin.Services;

public static class AdminPaths
{
    public static string StateDir => AppPaths.AdminStateDir;

    /// <summary>应用键 —— 进锁文件名与唤醒/退出事件名。客户端那边是 <c>Client</c>。</summary>
    public static string AppKey => AppPaths.AdminAppKey;
}
