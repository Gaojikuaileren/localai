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
    public long IdentityGeneration { get; set; }
    public string SnapshotCreatedAt { get; set; } = "";
    public List<Device> Devices { get; set; } = new();
    public List<DeviceCert> Certs { get; set; } = new();

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
