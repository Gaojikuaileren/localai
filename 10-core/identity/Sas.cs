// P3b S2.3 -- six-word Short Authentication String for initial pairing (packet §5).
//
// transcript (deterministic CBOR map, canonical) -> SHA-256 digest
//   -> HKDF-SHA256(ikm=digest, salt=SHA256("localai-pair-v1/salt"), info="localai-pair-v1/sas", L=9)
//   -> take the top 66 bits -> six 11-bit indices -> six words from the frozen word list.
//
// Both host and client build the SAME transcript independently and compare the six words by eye.
// Replacing ANY MITM-swappable field (CA, server leaf, CSR SPKI, either nonce, request id, hub id,
// claim-secret hash, protocol version) changes the digest and therefore the words.

using System.Security.Cryptography;
using System.Text;

namespace LocalAI.Identity;

public sealed record PairTranscript(
    int ProtocolVersion,
    string HubId,
    byte[] CaCertSha256,
    byte[] CaSpkiSha256,
    byte[] ServerLeafSha256,
    byte[] ClientCsrSpkiSha256,
    byte[] ClaimSecretHash,
    byte[] ClientNonce,
    byte[] ServerNonce,
    byte[] RequestId);

public static class Sas
{
    public const string Context = "localai-pair-v1";

    // Minimal deterministic CBOR: definite-length map, FIXED field order (frozen by protocol version),
    // shortest-form head. Both sides run this exact code, so the bytes match -- no cross-impl canonical
    // sorting needed. Only the CBOR types we use (text string / uint / byte string / map) are supported.
    static void Head(List<byte> b, int major, ulong val)
    {
        int m = major << 5;
        if (val < 24) b.Add((byte)(m | (int)val));
        else if (val < 0x100) { b.Add((byte)(m | 24)); b.Add((byte)val); }
        else if (val < 0x10000) { b.Add((byte)(m | 25)); b.Add((byte)(val >> 8)); b.Add((byte)val); }
        else if (val < 0x100000000UL) { b.Add((byte)(m | 26)); for (int i = 3; i >= 0; i--) b.Add((byte)(val >> (8 * i))); }
        else { b.Add((byte)(m | 27)); for (int i = 7; i >= 0; i--) b.Add((byte)(val >> (8 * i))); }
    }
    static void Text(List<byte> b, string s) { var u = Encoding.UTF8.GetBytes(s); Head(b, 3, (ulong)u.Length); b.AddRange(u); }
    static void Bytes(List<byte> b, byte[] v) { Head(b, 2, (ulong)v.Length); b.AddRange(v); }
    static void Uint(List<byte> b, ulong v) => Head(b, 0, v);

    static byte[] EncodeTranscript(PairTranscript t)
    {
        var b = new List<byte>(256);
        Head(b, 5, 11);   // map with 11 entries
        Text(b, "context"); Text(b, Context);
        Text(b, "protocol_version"); Uint(b, (ulong)t.ProtocolVersion);
        Text(b, "hub_id"); Text(b, t.HubId);
        Text(b, "ca_cert_sha256"); Bytes(b, t.CaCertSha256);
        Text(b, "ca_spki_sha256"); Bytes(b, t.CaSpkiSha256);
        Text(b, "server_leaf_sha256"); Bytes(b, t.ServerLeafSha256);
        Text(b, "client_csr_spki_sha256"); Bytes(b, t.ClientCsrSpkiSha256);
        Text(b, "claim_secret_hash"); Bytes(b, t.ClaimSecretHash);
        Text(b, "client_nonce"); Bytes(b, t.ClientNonce);
        Text(b, "server_nonce"); Bytes(b, t.ServerNonce);
        Text(b, "request_id"); Bytes(b, t.RequestId);
        return b.ToArray();
    }

    public static (string[] words, int[] indices) Derive(PairTranscript t)
    {
        var digest = SHA256.HashData(EncodeTranscript(t));
        var salt = SHA256.HashData(Encoding.UTF8.GetBytes(Context + "/salt"));
        var info = Encoding.UTF8.GetBytes(Context + "/sas");
        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, digest, 9, salt, info); // 9 bytes = 72 bits

        var idx = new int[6];
        int bit = 0;
        for (int wordI = 0; wordI < 6; wordI++)   // top 66 bits -> 6 x 11
        {
            int v = 0;
            for (int b = 0; b < 11; b++)
            {
                int bytePos = bit / 8;
                int bitInByte = 7 - (bit % 8);
                v = (v << 1) | ((okm[bytePos] >> bitInByte) & 1);
                bit++;
            }
            idx[wordI] = v;
        }
        var words = new string[6];
        for (int i = 0; i < 6; i++) words[i] = Wordlist.Word(idx[i]);
        return (words, idx);
    }
}
