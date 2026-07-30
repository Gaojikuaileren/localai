// P3c -- 翻译历史(用户裁定 2026-07-30:取代原来的「学习笔记」板块)。
//
// ★ 关键决定:历史【不另存一份原文】,它是会话消息的一个【视图】。
//   理由:原文已经在会话里(还有温层归档兜着),再存一份就有两份真相 ——
//   删了会话、历史还在,或者反过来,迟早对不上。这里只存【收藏了哪几条】。
//
// 一条历史 = 翻译工作空间里用户发出的一条消息。点它跳回那条消息所在的会话与位置。

namespace LocalAI.Client.Services;

/// <param name="SessionId">这条历史属于哪个会话</param>
/// <param name="MessageId">消息的稳定标识(用于跳转与收藏;老消息可能没有 -> 用 StableKey)</param>
/// <param name="Key">收藏用的键 —— 等于消息的 StableKey,保证归档来回之后仍指向同一条</param>
public sealed record HistoryEntry(string SessionId, string? MessageId, string Key, string Text, DateTime At, bool Favorite);

public sealed class TranslationHistory
{
    readonly ChatCenter _chat;
    readonly HashSet<string> _favorites = new();

    public event Action? Changed;

    /// <summary>跳到某条历史所在的位置(会话 + 消息)。由会话区接手。</summary>
    public event Action<string, string>? JumpRequested;

    public TranslationHistory(ChatCenter chat)
    {
        _chat = chat;
        _chat.Changed += () => Changed?.Invoke();   // 会话变了,历史跟着变
    }

    /// <summary>
    /// 全部历史,最新在前。只取【翻译工作空间】里用户自己发的消息 ——
    /// 系统说明(AI 未接入的告知、兜底级联的解释)不是历史。
    /// </summary>
    public IEnumerable<HistoryEntry> All(bool favoritesOnly = false)
    {
        foreach (var s in _chat.AllTranslationSessions())
            foreach (var m in _chat.MessagesOf(s.SessionId))
            {
                if (m.Role != ChatRole.User) continue;
                if (string.IsNullOrWhiteSpace(m.Text)) continue;
                var key = m.StableKey;
                var fav = _favorites.Contains(key);
                if (favoritesOnly && !fav) continue;
                yield return new HistoryEntry(s.SessionId, m.MessageId, key, m.Text, m.At, fav);
            }
    }

    public IReadOnlyList<HistoryEntry> Latest(int take, bool favoritesOnly = false)
        => All(favoritesOnly).OrderByDescending(e => e.At).Take(take).ToList();

    public int FavoriteCount => _favorites.Count;

    public bool IsFavorite(string key) => _favorites.Contains(key);

    public void ToggleFavorite(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!_favorites.Add(key)) _favorites.Remove(key);
        Changed?.Invoke();
    }

    /// <summary>请求跳到某条历史 —— 会话区收到后选中会话并滚到那条消息。</summary>
    public void Jump(string sessionId, string key) => JumpRequested?.Invoke(sessionId, key);

    // ---------------------------------------------------------------- 存档(只存收藏)
    public List<string> Export() => _favorites.ToList();

    public void Import(List<string>? keys)
    {
        _favorites.Clear();
        if (keys is not null) foreach (var k in keys) if (!string.IsNullOrWhiteSpace(k)) _favorites.Add(k);
        Changed?.Invoke();
    }
}
