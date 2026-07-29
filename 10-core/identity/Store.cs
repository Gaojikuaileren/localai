// P3b S2.2 -- JSON membership store (devices / device_certificates / identity_generation).
// Single-writer registry, single-host: one store.json written atomically (temp + move). Read by the
// Python gateway in S3 for fingerprint->principal mapping. Packet §7.1: certificate proves "holds a
// CA-signed key"; the membership store proves "this device is still allowed" -- both required.
//
// Status vocab (packet §7.1): device = provisioning|active|revoked; cert = candidate|active|
// superseded|revoked|expired. caller_tier is always LAN_DEVICE (P3b does not widen P3a).

using System.Text.Json;

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

    public static Store LoadOrEmpty(string dir)
    {
        var p = PathOf(dir);
        if (!File.Exists(p)) return new Store { SnapshotCreatedAt = Now() };
        return JsonSerializer.Deserialize<Store>(File.ReadAllText(p)) ?? new Store();
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
