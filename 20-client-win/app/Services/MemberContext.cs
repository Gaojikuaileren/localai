// P3c -- 「我是谁」与可见范围过滤(D45)。
//
// 铁律(gateway.py:227 / Store.cs 同款纪律):**主体只来自成员表**,客户端自报一律忽略。
// 所以这里【绝不】使用 AppSettings.CachedMemberDisplayName —— 那只是给界面显示的缓存,
// 普通用户拿记事本就能改;拿它做可见范围判定,等于把 D45 变成"信任本机 JSON 诚实"。
//
// 当前实况(诚实):成员会话尚未打通,中枢还没有向客户端下发过成员 id。
//   在那之前用一个**设备本地哨兵** LocalMemberId —— 它只匹配"本机本人建的东西",
//   而任何来自同步的外来条目都会带真实成员 id,因此永远不会被误判成本机的。
//   中枢下发真实 id 后:调用 AdoptLocalItems 把本地条目认领过去,然后 Current 变成真 id。
//
// 判定规则(fail-closed):
//   · 家庭(Family)范围 -> 所有成员可见;
//   · 个人 / 仅本人 -> **只有所有者本人可见**;
//   · **所有者未知(空)-> 一律不可见**。
//     ★ 这条是本项目反复吃亏的那类缺陷的解药:"新加的值默认落哪边"——这里默认落在【看不见】。

namespace LocalAI.Client.Services;

public static class MemberContext
{
    /// <summary>成员层打通前的设备本地哨兵。同步来的条目永远带真实成员 id,不会撞上它。</summary>
    public const string LocalMemberId = "local";

    static string? _fromHub;

    /// <summary>当前成员 id。中枢下发前 = 本地哨兵;下发后 = 中枢给的真实 id。</summary>
    public static string Current => string.IsNullOrWhiteSpace(_fromHub) ? LocalMemberId : _fromHub!;

    /// <summary>
    /// 中枢按证书指纹反查出成员后调用(P3c S4 之后接上)。
    /// ★ 必须先 AdoptLocalItems 再切换,否则本机既有条目会因所有者对不上而集体隐身。
    /// </summary>
    public static void SetFromHub(string memberId)
    {
        if (!string.IsNullOrWhiteSpace(memberId)) _fromHub = memberId;
    }

    /// <summary>供测试/复位用。</summary>
    public static void ResetToLocal() => _fromHub = null;

    /// <summary>
    /// 可见性判定。ownerMemberId 为空 => 不可见(fail-closed)。
    /// </summary>
    public static bool CanSee(ProjectScope scope, string? ownerMemberId, string? currentMemberId = null)
    {
        if (scope == ProjectScope.Family) return true;              // 家庭范围:全家可见
        if (string.IsNullOrWhiteSpace(ownerMemberId)) return false; // ★ 所有者未知 -> 看不见
        return ownerMemberId == (currentMemberId ?? Current);
    }
}
