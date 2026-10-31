namespace ThunderID.AspNetCore.BackchannelLogout;

/// <summary>
/// Single-instance, in-memory <see cref="ILogoutSessionStore"/>. Entries are kept for
/// <c>retention</c>, which must be at least as long as a cookie session can stay valid.
/// </summary>
public sealed class InMemoryLogoutSessionStore : ILogoutSessionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _endedSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset IssuedAt, DateTimeOffset Expires)> _subjectLogouts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _seenJwtIds = new(StringComparer.Ordinal);
    private readonly TimeSpan _retention;
    private readonly TimeProvider _timeProvider;

    public InMemoryLogoutSessionStore(TimeSpan retention, TimeProvider? timeProvider = null)
    {
        if (retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention));
        }

        _retention = retention;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<bool> TryRecordLogoutAsync(LogoutToken token, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            Prune(now);

            if (_seenJwtIds.ContainsKey(token.JwtId))
            {
                return Task.FromResult(false);
            }

            var expires = now + _retention;
            if (token.SessionId is not null)
            {
                _endedSessions[token.SessionId] = expires;
            }
            else if (token.Subject is not null)
            {
                // Keep the latest sub-only logout: it covers every session that began before it.
                if (!_subjectLogouts.TryGetValue(token.Subject, out var existing) || existing.IssuedAt < token.IssuedAt)
                {
                    _subjectLogouts[token.Subject] = (token.IssuedAt, expires);
                }
            }

            // Remember the jti until the token itself can no longer be presented.
            _seenJwtIds[token.JwtId] = token.ExpiresAt > now ? token.ExpiresAt + TimeSpan.FromMinutes(5) : now + TimeSpan.FromMinutes(5);
            return Task.FromResult(true);
        }
    }

    public Task<bool> IsSessionEndedAsync(string? sessionId, string? subject, DateTimeOffset? authTime, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (sessionId is not null && _endedSessions.TryGetValue(sessionId, out var sessionExpires) && sessionExpires > now)
            {
                return Task.FromResult(true);
            }

            if (subject is not null && _subjectLogouts.TryGetValue(subject, out var logout) && logout.Expires > now)
            {
                // A session with no known start time is treated as older than the logout.
                return Task.FromResult(authTime is null || authTime.Value < logout.IssuedAt);
            }

            return Task.FromResult(false);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _endedSessions.Where(e => e.Value <= now).Select(e => e.Key).ToList())
        {
            _endedSessions.Remove(key);
        }

        foreach (var key in _subjectLogouts.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList())
        {
            _subjectLogouts.Remove(key);
        }

        foreach (var key in _seenJwtIds.Where(e => e.Value <= now).Select(e => e.Key).ToList())
        {
            _seenJwtIds.Remove(key);
        }
    }
}
