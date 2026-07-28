// P3b S2.2 -- stable hub identity. Real identity = hub_id (UUID) + project CA; the ".local" name is
// only a routing/TLS label derived from hub-id-short = first 80 bits of the UUID as lowercase RFC 4648
// Base32 (no padding, 16 chars). Packet §4.1: implementations must not shorten it further.

namespace LocalAI.Identity;

public static class HubId
{
    const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567"; // RFC 4648 base32, lowercase

    public static string Short(Guid hub)
    {
        var b = hub.ToByteArray();            // 16 bytes; take the first 10 (80 bits)
        return Base32(b.AsSpan(0, 10));       // 80 / 5 = exactly 16 chars, no padding needed
    }

    static string Base32(ReadOnlySpan<byte> data)
    {
        var sb = new System.Text.StringBuilder(16);
        int buffer = 0, bits = 0;
        foreach (var by in data)
        {
            buffer = (buffer << 8) | by;
            bits += 8;
            while (bits >= 5) { bits -= 5; sb.Append(Alphabet[(buffer >> bits) & 31]); }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }
}
