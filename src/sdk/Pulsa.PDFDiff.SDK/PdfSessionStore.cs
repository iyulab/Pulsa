using System.Collections.Concurrent;

namespace PulsaPDFDiff;

/// <summary>
/// In-memory store for uploaded PDF images, keyed by session ID.
/// Entries expire after a configurable timeout.
/// </summary>
public class PdfSessionStore
{
    private readonly ConcurrentDictionary<string, PdfSession> _sessions = new();
    private readonly TimeSpan _expiry = TimeSpan.FromMinutes(30);

    public PdfSession Create(List<string> base64Images)
    {
        Cleanup();
        var session = new PdfSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Images = base64Images,
            CreatedAt = DateTime.UtcNow
        };
        _sessions[session.Id] = session;
        return session;
    }

    public PdfSession? Get(string id)
    {
        if (_sessions.TryGetValue(id, out var session))
        {
            if (DateTime.UtcNow - session.CreatedAt < _expiry)
                return session;
            _sessions.TryRemove(id, out _);
        }
        return null;
    }

    private void Cleanup()
    {
        var cutoff = DateTime.UtcNow - _expiry;
        foreach (var kvp in _sessions)
        {
            if (kvp.Value.CreatedAt < cutoff)
                _sessions.TryRemove(kvp.Key, out _);
        }
    }
}

public class PdfSession
{
    public string Id { get; set; } = "";
    public List<string> Images { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public int PageCount => Images.Count;
}
