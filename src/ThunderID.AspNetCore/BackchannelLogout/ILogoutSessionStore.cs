namespace ThunderID.AspNetCore.BackchannelLogout;

/// <summary>
/// Remembers which sessions ThunderID has ended, so a cookie session can be refused on its next
/// request. The default is in memory: a deployment with more than one instance needs a shared
/// implementation, because each notification reaches only one instance.
/// </summary>
public interface ILogoutSessionStore
{
    /// <summary>
    /// Records a validated logout. Returns <c>false</c> when this token's <c>jti</c> was already
    /// recorded, which the caller treats as a replay.
    /// </summary>
    Task<bool> TryRecordLogoutAsync(LogoutToken token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a session has been ended: its <c>sid</c> was named in a logout, or a <c>sub</c>-only
    /// logout for its subject was issued after the session began.
    /// </summary>
    Task<bool> IsSessionEndedAsync(string? sessionId, string? subject, DateTimeOffset? authTime, CancellationToken cancellationToken = default);
}
