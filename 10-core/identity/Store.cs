// P3b S2.2 -- JSON membership store (devices / device_certificates / identity_generation).
// Multi-writer, single-host: writers = pairing approve/complete (lan-edge threads) + admin revoke
// (lan-edge loopback) + identity CLI (a SEPARATE process: revoke-device / add-member /
// set-device-member). All go through Store.Mutate(), serialized by a NAMED mutex (cross-process).
// One store.json written atomically (temp + move). Read by the
// Python gateway in S3 for fingerprint->principal mapping. Packet §7.1: certificate proves "holds a
// CA-signed key"; the membership store proves "this device is still allowed" -- both required.
//
// Status vocab (packet §7.1): device = provisioning|active|revoked; cert = candidate|active|
// superseded|revoked|expired. caller_tier is always LAN_DEVICE (P3b does not widen P3a).

using System.Text.Json;
using System.Threading;

namespace LocalAI.Identity;

public sealed class Device
{
    public string DeviceId { get; set; } = "";
    public string Status { get; set; } = "provisioning";
    public string CallerTier { get; set; } = "LAN_DEVICE";
    public long CurrentGeneration { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? ApprovedAt { get; set; }
    public string? RevokedAt { get; set; }
    public string? LastSeenAt { get; set; }
    public string UntrustedDisplayName { get; set; } = "";  // self-reported; escape before display, never into a prompt
    public string? FirstSeenIp { get; set; }
    // P3c/D45:每台设备有一个默认成员。这只是"谁大概率坐在这台机器前"的提示,
    // NOT authentication -- 看「仅本人」或高风险操作前仍需成员二次确认(D45 裁定 2)。
    public string? DefaultMemberId { get; set; }
}

// P3c/D45 -- 身份层从「设备」扩到「设备 × 成员」。P3b 只证明"这台设备被允许";
// 成员层回答"此刻是谁",是「仅本人」可见范围与成员二次确认的前提。
// ★ 成员 ≠ 认证:设备默认成员 + 语音语言只是**猜测**(D45:语音永不作认证)。
public sealed class Member
{
    public string MemberId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "member";   // member | admin(家庭安全管理员,恒有且仅需一名)
    public string CreatedAt { get; set; } = "";
}

public sealed class DeviceCert
{
    public string DeviceId { get; set; } = "";
    public long Generation { get; set; }
    public string CertSerial { get; set; } = "";
    public string CertSha256 { get; set; } = "";   // full DER leaf SHA-256 -- the lookup fingerprint
    public string SpkiSha256 { get; set; } = "";
    public string Status { get; set; } = "candidate";
    public string NotBefore { get; set; } = "";
    public string NotAfter { get; set; } = "";
}

public sealed class Store
{
    public const string RoleAdmin = "admin";     // 家庭安全管理员
    public const string RoleMember = "member";

    public long IdentityGeneration { get; set; }
    public string SnapshotCreatedAt { get; set; } = "";
    public List<Device> Devices { get; set; } = new();
    public List<DeviceCert> Certs { get; set; } = new();
    // P3c/D45。旧 store.json 无此键 -> 反序列化为空表(向前兼容);
    // Python 网关(gateway/membership.py)只按键读 Devices/Certs/IdentityGeneration,加键不影响它(向后兼容)。
    public List<Member> Members { get; set; } = new();

    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };
    static string Now() => DateTimeOffset.UtcNow.ToString("O");
    static string PathOf(string dir) => Path.Combine(dir, "store.json");

    // ★★ 跨进程串行的写入闸(2026-07-31 审计):store.json 此前是【无锁的全量 read-modify-write】,
    //   而写它的有三条并发路径:配对审批(控制台线程)、/pair/complete(LAN 口请求线程)、
    //   /admin/devices/revoke(回环 admin 请求线程),外加 identity CLI 是【另一个进程】
    //   (revoke-device / add-member / set-device-member)。Save 是 last-write-wins 全量覆盖,
    //   于是一次吊销可能被同时发生的一次配对整包写掉 —— 设备"复活"。
    //   必须用【命名 Mutex】(不能只用 lock):要跨进程,identity CLI 与 lan-edge 是两个 exe。
    static readonly Mutex Gate = new(false, @"Local\LocalAI.Identity.Store");

    /// <summary>取闸 → 读 → 改 → 写 → 放闸,整段原子。所有会写 store 的地方都走它。</summary>
    public static T Mutate<T>(string dir, Func<Store, T> change)
    {
        // 放弃的 Mutex(持有进程崩了)会抛 AbandonedMutexException,但锁已到手 —— 照常继续。
        try { Gate.WaitOne(); } catch (AbandonedMutexException) { }
        try
        {
            var s = LoadOrEmpty(dir);
            var r = change(s);
            s.Save(dir);
            return r;
        }
        finally { Gate.ReleaseMutex(); }
    }

    /// <summary>无返回值的便捷重载。</summary>
    public static void Mutate(string dir, Action<Store> change)
        => Mutate<object?>(dir, s => { change(s); return null; });

    public static Store LoadOrEmpty(string dir)
    {
        var p = PathOf(dir);
        if (!File.Exists(p)) return new Store { SnapshotCreatedAt = Now() };
        // ★ FileShare.ReadWrite|Delete:另一个 mutate 正在原子替换(temp+move)时,读不该抛
        //   IOException/500;这里容忍并发替换。
        try
        {
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return JsonSerializer.Deserialize<Store>(sr.ReadToEnd()) ?? new Store();
        }
        catch (IOException)
        {
            // 恰好撞上 move 的瞬间:重读一次(此刻新文件已就位)。
            return JsonSerializer.Deserialize<Store>(File.ReadAllText(p)) ?? new Store();
        }
    }

    public void Save(string dir)
    {
        SnapshotCreatedAt = Now();
        var p = PathOf(dir);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opt));
        File.Move(tmp, p, overwrite: true);   // atomic replace on NTFS
    }

    public Device AddProvisioning(string deviceId, string displayName, string? ip)
    {
        var d = new Device
        {
            DeviceId = deviceId,
            Status = "provisioning",
            CurrentGeneration = IdentityGeneration,
            CreatedAt = Now(),
            UntrustedDisplayName = displayName,
            FirstSeenIp = ip,
        };
        Devices.Add(d);
        return d;
    }

    public DeviceCert AddCandidate(string deviceId, string serial, string certSha256, string spkiSha256,
                                   string notBefore, string notAfter)
    {
        var c = new DeviceCert
        {
            DeviceId = deviceId,
            Generation = IdentityGeneration,
            CertSerial = serial,
            CertSha256 = certSha256,
            SpkiSha256 = spkiSha256,
            Status = "candidate",
            NotBefore = notBefore,
            NotAfter = notAfter,
        };
        Certs.Add(c);
        return c;
    }

    // candidate -> active; device -> active; generation++ (single monotonic counter).
    public void Activate(string deviceId, string certSha256)
    {
        IdentityGeneration++;
        var d = Devices.First(x => x.DeviceId == deviceId);
        d.Status = "active"; d.ApprovedAt = Now(); d.CurrentGeneration = IdentityGeneration;
        var c = Certs.First(x => x.CertSha256 == certSha256);
        c.Status = "active"; c.Generation = IdentityGeneration;
    }

    /// <summary>
    /// 清掉【批准了、但对方从来没来领证】的半截 `provisioning` 记录。
    ///
    /// ★ 2026-08-04 加。为什么需要它:设备记录是在 **Approve** 那一刻建的(`AddProvisioning`),
    ///   要等客户端 claim/complete 才转 `active`。中间任何一步失败,这条记录就**永远**停在
    ///   `provisioning` —— 全仓没有任何代码让它过期。
    ///   实测后果:证书名 bug 让 `/pair/status` 第一次握手就失败(见 `b136f01`),客户端每重试一次
    ///   就在中枢多留一条,机主的设备列表里**同一台机器出现了 6 次**(store.json 实证:
    ///   6 条 `HONGKONGPINGPON` 全是 `ApprovedAt: null`)。
    ///   ★ 那个握手 bug 已修,但**积累机制**是独立的缺陷:今后任何一次没走完的配对都会再留一条。
    ///
    /// ★ 判据只用**记录自己的时间**,不用自报的名字 —— `UntrustedDisplayName` 是对方随便写的,
    ///   拿它当去重键等于让局域网上任何人决定哪条记录该被清掉(与项目「自报值只作显示、永不作判据」同源)。
    ///
    /// ★ 落到 `revoked` 而不是新加一个状态:状态词表就 `provisioning|active|revoked` 三个,
    ///   客户端按 `Status != "revoked"` 过滤;新造一个词会让这些死记录继续显示在「已配对电脑」里。
    ///   也不 delete —— 项目纪律是留痕不删(`RevokedAt` 就是痕)。
    /// </summary>
    /// <param name="ttl">超过这个时长仍没领证就算死。★ 必须显著大于客户端的领证等待
    /// (`ApprovalWaitMs` ≈ 5 分钟),否则会掐掉正在进行中的配对。</param>
    /// <returns>本次清掉几条。</returns>
    public int SweepStaleProvisioning(TimeSpan ttl)
    {
        var cutoff = DateTimeOffset.UtcNow - ttl;
        var dead = Devices.Where(d => d.Status == "provisioning"
                                      && DateTimeOffset.TryParse(d.CreatedAt, out var c) && c < cutoff)
                          .ToList();
        if (dead.Count == 0) return 0;

        // 代数只加一次:这是一次清理,不是 N 次独立吊销。
        IdentityGeneration++;
        foreach (var d in dead)
        {
            d.Status = "revoked"; d.RevokedAt = Now();
            foreach (var c in Certs.Where(x => x.DeviceId == d.DeviceId && x.Status == "candidate"))
                c.Status = "revoked";
        }
        return dead.Count;
    }

    // Revoke the whole device: device -> revoked, all its live certs -> revoked; generation++.
    public void RevokeDevice(string deviceId)
    {
        IdentityGeneration++;
        var d = Devices.FirstOrDefault(x => x.DeviceId == deviceId)
                ?? throw new KeyNotFoundException("unknown device: " + deviceId);
        d.Status = "revoked"; d.RevokedAt = Now();
        foreach (var c in Certs.Where(x => x.DeviceId == deviceId &&
                                           (x.Status == "active" || x.Status == "candidate" || x.Status == "superseded")))
            c.Status = "revoked";
    }

    // ---------------------------------------------------------------- P3c/D45 成员层
    // 不动 IdentityGeneration:世代号是**设备准入**的版本(证书吊销要让网关立即失效);
    // 成员的增删改不影响任何证书的有效性,递增它会造成无意义的全局失效。

    public Member? FindMember(string memberId) => Members.FirstOrDefault(m => m.MemberId == memberId);
    public int AdminCount => Members.Count(m => m.Role == RoleAdmin);

    // 第一位成员自动成为家庭安全管理员(不然就没人能管安全设置了)。
    public Member AddMember(string displayName, string? role = null)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("member display name must not be empty");
        var m = new Member
        {
            MemberId = Guid.NewGuid().ToString(),
            DisplayName = displayName.Trim(),
            Role = role ?? (Members.Count == 0 ? RoleAdmin : RoleMember),
            CreatedAt = Now(),
        };
        if (m.Role is not (RoleAdmin or RoleMember))
            throw new ArgumentException("unknown role: " + m.Role);
        Members.Add(m);
        return m;
    }

    // fail-closed:绝不允许把最后一名家庭安全管理员降级或删掉(否则安全设置永久锁死)。
    public void SetMemberRole(string memberId, string role)
    {
        if (role is not (RoleAdmin or RoleMember)) throw new ArgumentException("unknown role: " + role);
        var m = FindMember(memberId) ?? throw new KeyNotFoundException("unknown member: " + memberId);
        if (m.Role == RoleAdmin && role != RoleAdmin && AdminCount <= 1)
            throw new InvalidOperationException("cannot demote the last family security admin");
        m.Role = role;
    }

    public void RemoveMember(string memberId)
    {
        var m = FindMember(memberId) ?? throw new KeyNotFoundException("unknown member: " + memberId);
        if (m.Role == RoleAdmin && AdminCount <= 1)
            throw new InvalidOperationException("cannot remove the last family security admin");
        Members.Remove(m);
        foreach (var d in Devices.Where(d => d.DefaultMemberId == memberId))
            d.DefaultMemberId = null;   // 设备的默认成员没了 -> 回到"未指定",而不是留悬空引用
    }

    public void RenameMember(string memberId, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("member display name must not be empty");
        (FindMember(memberId) ?? throw new KeyNotFoundException("unknown member: " + memberId)).DisplayName = displayName.Trim();
    }

    // 设备的默认成员。传 null 清除。成员必须已存在(不接受悬空引用)。
    public void SetDeviceDefaultMember(string deviceId, string? memberId)
    {
        var d = Devices.FirstOrDefault(x => x.DeviceId == deviceId)
                ?? throw new KeyNotFoundException("unknown device: " + deviceId);
        if (memberId is not null && FindMember(memberId) is null)
            throw new KeyNotFoundException("unknown member: " + memberId);
        d.DefaultMemberId = memberId;
    }

    public (Device device, DeviceCert cert)? FindByFingerprint(string certSha256)
    {
        var c = Certs.FirstOrDefault(x => x.CertSha256 == certSha256);
        if (c is null) return null;
        var d = Devices.FirstOrDefault(x => x.DeviceId == c.DeviceId);
        return d is null ? null : (d, c);
    }

    // The gateway's per-request test: active cert AND active device.
    public bool IsActive(string certSha256)
        => FindByFingerprint(certSha256) is { } t && t.device.Status == "active" && t.cert.Status == "active";
}
