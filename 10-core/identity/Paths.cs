// P3b S2.2 -- read logical roots from config/paths.toml (方案书 §11.1: no hardcoded paths).
// Mirrors the Python convention (locate config/paths.toml relative to the code, then read it).

namespace LocalAI.Identity;

public static class Paths
{
    public static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            if (File.Exists(Path.Combine(d.FullName, "config", "paths.toml"))) return d.FullName;
            d = d.Parent;
        }
        throw new InvalidOperationException("config/paths.toml not found above " + AppContext.BaseDirectory);
    }

    // Minimal reader for a single-quoted value under [state] (matches the project's simple parser).
    public static string State(string key)
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), "config", "paths.toml"));
        bool inState = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inState = line.StartsWith("[state]"); continue; }
            if (!inState || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            if (line[..eq].Trim() != key) continue;
            var v = line[(eq + 1)..];
            int q1 = v.IndexOf('\'');
            int q2 = q1 >= 0 ? v.IndexOf('\'', q1 + 1) : -1;
            if (q1 >= 0 && q2 > q1) return v.Substring(q1 + 1, q2 - q1 - 1);
        }
        throw new KeyNotFoundException("[state] " + key + " not found in paths.toml");
    }

    public static string IdentityDir() => State("identity");
    public static string SecretsDir() => State("secrets");
}
