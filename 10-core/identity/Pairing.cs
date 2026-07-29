// P3b S2.3 -- initial pairing state machine (packet §5 / §5.1). In-process logic; the HTTP routes
// (/pair/enroll|status|claim|complete on the LAN Edge) are wired in S4. State machine:
//
//   pending ─┬─ denied
//            ├─ expired
//            └─ approved ─> certificate_issued ─> active
//
// Properties enforced here: pairing only inside an open host window; queue cap; CSR proof-of-
// possession; six-word SAS over the full transcript; claim requires claim-secret (constant-time)
// AND a challenge signature by the CSR key; idempotent claim; single approve (no re-approve/bulk).

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace LocalAI.Identity;

public sealed class Pending
{
    public string RequestId = "";
    public byte[] RequestIdBytes = [];
    public byte[] CsrDer = [];
    public byte[] CsrSpkiSha = [];
    public byte[] ClaimSecretHash = [];
    public byte[] ClientNonce = [];
    public byte[] ServerNonce = [];
    public int ProtoVer;
    public string DisplayName = "";
    public string Status = "pending";
    public string DeviceId = "";
    public X509Certificate2? Candidate;
    public string CandidateSha256 = "";
    public byte[]? CandidateShaBytes;
    public byte[]? ClaimNonce;
    public string[] SasWords = [];   // kept so the host console can re-print the six words on `list`
    public DateTimeOffset ExpiresAt;
}

public sealed record EnrollResult(string RequestId, byte[] ServerNonce, byte[] ClientCsrSpkiSha256, string[] Sas, int[] SasIndices);
public sealed record StatusResult(string Status, byte[]? ClaimNonce, string? CandidateSha256);

public sealed class Pairing
{
    public const int MaxPending = 8;

    readonly string _idDir;
    readonly string _caKeyName;
    readonly X509Certificate2 _caCert;
    readonly string _hubId;
    readonly byte[] _caCertSha, _caSpkiSha, _serverLeafSha;
    readonly Dictionary<string, Pending> _pending = new();
    readonly object _gate = new();   // Enroll runs on ASP.NET request threads; Approve/list on the host console thread

    bool _windowOpen;
    DateTimeOffset _windowExpires;

    public Pairing(string identityDir, string secretsDir)
    {
        _idDir = identityDir;
        _caCert = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(Path.Combine(identityDir, "ca.cer")));
        using var serverCert = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(Path.Combine(identityDir, "server.cer")));
        var hub = JsonDocument.Parse(File.ReadAllText(Path.Combine(identityDir, "hub.json"))).RootElement;
        _hubId = hub.GetProperty("hub_id").GetString()!;
        var loc = JsonDocument.Parse(File.ReadAllText(Path.Combine(secretsDir, "identity-locators.json"))).RootElement;
        _caKeyName = loc.GetProperty("ca_key_name").GetString()!;
        _caCertSha = SHA256.HashData(_caCert.RawData);
        _caSpkiSha = SHA256.HashData(_caCert.PublicKey.ExportSubjectPublicKeyInfo());
        _serverLeafSha = SHA256.HashData(serverCert.RawData);
    }

    public string HubId => _hubId;   // clients need it to build the SAS transcript independently

    public void OpenWindow(TimeSpan duration) { lock (_gate) { _windowOpen = true; _windowExpires = DateTimeOffset.UtcNow + duration; } }
    public void CloseWindow() { lock (_gate) _windowOpen = false; }
    public bool WindowOpen { get { lock (_gate) return _windowOpen && DateTimeOffset.UtcNow <= _windowExpires; } }

    // Snapshot for the host console: what is waiting, and the six words to compare against the 2nd PC.
    public IReadOnlyList<(string RequestId, string DisplayName, string Status, string[] Sas)> ListPending()
    {
        lock (_gate)
            return _pending.Values.OrderBy(p => p.ExpiresAt)
                .Select(p => (p.RequestId, p.DisplayName, p.Status, p.SasWords)).ToList();
    }

    // Resolve a request by a short id prefix (host types the first few hex chars). Throws if ambiguous/none.
    public string ResolveByPrefix(string prefix)
    {
        lock (_gate)
        {
            var hits = _pending.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 1) return hits[0];
            throw new InvalidOperationException(hits.Count == 0 ? "no request matches " + prefix : "ambiguous prefix " + prefix);
        }
    }

    // The transcript both sides build independently; exposed so a client can recompute the SAS.
    public PairTranscript BuildTranscript(int protoVer, byte[] claimSecretHash, byte[] clientNonce,
                                          byte[] serverNonce, byte[] requestIdBytes, byte[] clientCsrSpkiSha)
        => new(protoVer, _hubId, _caCertSha, _caSpkiSha, _serverLeafSha, clientCsrSpkiSha,
               claimSecretHash, clientNonce, serverNonce, requestIdBytes);

    Pending Get(string reqId) => _pending.TryGetValue(reqId, out var p) ? p : throw new KeyNotFoundException("unknown request");

    public EnrollResult Enroll(byte[] csrDer, byte[] clientNonce, byte[] claimSecretHash, int protoVer, string displayName)
    {
        lock (_gate)
        {
            if (!_windowOpen || DateTimeOffset.UtcNow > _windowExpires)
                throw new InvalidOperationException("pairing window is closed");
            if (_pending.Values.Count(p => p.Status == "pending") >= MaxPending)
                throw new InvalidOperationException("pending queue is full");

            var csrPub = Ca.PublicKeyFromCsr(csrDer);   // verifies proof-of-possession
            var csrSpkiSha = SHA256.HashData(csrPub.ExportSubjectPublicKeyInfo());
            var reqIdBytes = RandomNumberGenerator.GetBytes(16);
            var reqId = Convert.ToHexString(reqIdBytes);
            var serverNonce = RandomNumberGenerator.GetBytes(32);

            var sas = Sas.Derive(BuildTranscript(protoVer, claimSecretHash, clientNonce, serverNonce, reqIdBytes, csrSpkiSha));
            _pending[reqId] = new Pending
            {
                RequestId = reqId,
                RequestIdBytes = reqIdBytes,
                CsrDer = csrDer,
                CsrSpkiSha = csrSpkiSha,
                ClaimSecretHash = claimSecretHash,
                ClientNonce = clientNonce,
                ServerNonce = serverNonce,
                ProtoVer = protoVer,
                DisplayName = displayName,
                Status = "pending",
                SasWords = sas.words,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            };
            return new EnrollResult(reqId, serverNonce, csrSpkiSha, sas.words, sas.indices);
        }
    }

    // Host-admin, single request only. Issues a candidate client cert + records a provisioning device.
    public void Approve(string reqId)
    {
      lock (_gate)
      {
        var p = Get(reqId);
        if (p.Status != "pending")
            throw new InvalidOperationException("only a pending request can be approved (status=" + p.Status + ")");

        var csrPub = Ca.PublicKeyFromCsr(p.CsrDer);
        var deviceId = Guid.NewGuid().ToString();
        var uri = "urn:localai:device:" + deviceId;
        var cert = Ca.IssueLeaf(_caKeyName, _caCert, csrPub, "device-" + deviceId[..8],
                                dnsSan: null, uriSan: uri, serverAuth: false, clientAuth: true, days: 90);

        p.DeviceId = deviceId;
        p.Candidate = cert;
        p.CandidateShaBytes = SHA256.HashData(cert.RawData);
        p.CandidateSha256 = Convert.ToHexString(p.CandidateShaBytes);
        p.ClaimNonce = RandomNumberGenerator.GetBytes(32);
        p.Status = "approved";

        var store = Store.LoadOrEmpty(_idDir);
        store.AddProvisioning(deviceId, p.DisplayName, null);
        store.AddCandidate(deviceId, cert.SerialNumber, p.CandidateSha256, Convert.ToHexString(p.CsrSpkiSha),
                           cert.NotBefore.ToString("O"), cert.NotAfter.ToString("O"));
        store.Save(_idDir);
      }
    }

    public void Deny(string reqId)
    {
        lock (_gate)
        {
            var p = Get(reqId);
            if (p.Status != "pending") throw new InvalidOperationException("only a pending request can be denied");
            p.Status = "denied";
        }
    }

    public StatusResult Status(string reqId, byte[] claimSecret)
    {
        lock (_gate)
        {
            var p = Get(reqId);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(claimSecret), p.ClaimSecretHash))
                throw new UnauthorizedAccessException("claim secret mismatch");
            return (p.Status is "approved" or "certificate_issued")
                ? new StatusResult(p.Status, p.ClaimNonce, p.CandidateSha256)
                : new StatusResult(p.Status, null, null);
        }
    }

    // Idempotent: proves claim-secret + a challenge signature by the CSR key, returns the candidate.
    public X509Certificate2 Claim(string reqId, byte[] claimSecret, byte[] challengeSig)
    {
        lock (_gate)
        {
            var p = Get(reqId);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(claimSecret), p.ClaimSecretHash))
                throw new UnauthorizedAccessException("claim secret mismatch");
            if (p.Status is not ("approved" or "certificate_issued"))
                throw new InvalidOperationException("request is not approved");

            var csrPub = Ca.PublicKeyFromCsr(p.CsrDer);
            using var verifier = csrPub.GetECDsaPublicKey()!;
            if (!verifier.VerifyData(ChallengeBytes(p), challengeSig, HashAlgorithmName.SHA256))
                throw new UnauthorizedAccessException("challenge signature invalid");

            p.Status = "certificate_issued";   // repeat calls return the same candidate
            return p.Candidate!;
        }
    }

    // The mTLS PoP on /pair/complete: the presented client cert must be this request's candidate.
    // presentedCertSha256 is null for in-process callers (trusted); the Edge passes the TLS cert's fp.
    public void Complete(string reqId, string? presentedCertSha256 = null)
    {
        lock (_gate)
        {
            var p = Get(reqId);
            if (p.Status != "certificate_issued")
                throw new InvalidOperationException("claim must succeed before complete");
            if (presentedCertSha256 is not null &&
                !string.Equals(presentedCertSha256, p.CandidateSha256, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("presented cert is not the candidate for this request");
            var store = Store.LoadOrEmpty(_idDir);
            store.Activate(p.DeviceId, p.CandidateSha256);   // generation++
            store.Save(_idDir);
            p.Status = "active";
        }
    }

    public string StatusOf(string reqId) { lock (_gate) return Get(reqId).Status; }

    // context/claim || request_id || claim_nonce || candidate_sha256 -- signed by the CSR key.
    static byte[] ChallengeBytes(Pending p)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.UTF8.GetBytes(Sas.Context + "/claim"));
        ms.Write(p.RequestIdBytes);
        ms.Write(p.ClaimNonce!);
        ms.Write(p.CandidateShaBytes!);
        return ms.ToArray();
    }

    // Exposed for the client side of the self-test to build the identical challenge.
    public static byte[] BuildChallenge(byte[] requestIdBytes, byte[] claimNonce, byte[] candidateShaBytes)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.UTF8.GetBytes(Sas.Context + "/claim"));
        ms.Write(requestIdBytes);
        ms.Write(claimNonce);
        ms.Write(candidateShaBytes);
        return ms.ToArray();
    }
}
