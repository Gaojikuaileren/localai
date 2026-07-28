// P3b S2.3 -- frozen, versioned SAS word list (2048 = 2^11 tokens, one per 11-bit index).
//
// ★ v0 is a PLACEHOLDER: deterministic consonant-vowel-consonant tokens (16 x 8 x 16 = 2048),
//   pronounceable and distinct, so the SAS mechanism can be built and tested now. The final
//   human-facing list (a curated bilingual zh/en list, or BIP-39) is a later polish -- swapping it
//   in changes only the displayed words, not the indices or the security. Tracked as a P3b.2/P3c item.

namespace LocalAI.Identity;

public static class Wordlist
{
    public const string Version = "localai-sas-wordlist-v0-placeholder";

    static readonly string[] Cons = { "b", "d", "f", "g", "k", "l", "m", "n", "p", "r", "s", "t", "v", "w", "z", "c" }; // 16
    static readonly string[] Vow = { "a", "e", "i", "o", "u", "ai", "ou", "ei" };                                       // 8

    public const int Size = 2048;

    public static string Word(int index)
    {
        if (index is < 0 or >= Size) throw new ArgumentOutOfRangeException(nameof(index));
        int c1 = index >> 7;          // 0..15
        int v = (index >> 4) & 7;     // 0..7
        int c2 = index & 15;          // 0..15
        return Cons[c1] + Vow[v] + Cons[c2];
    }
}
