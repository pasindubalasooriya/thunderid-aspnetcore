namespace ThunderID.AspNetCore.BackchannelLogout;

/// <summary>A logout token that passed every validation check.</summary>
/// <param name="Issuer">The <c>iss</c> claim, ThunderID's issuer.</param>
/// <param name="SessionId">The <c>sid</c> claim. When present, only that session ends.</param>
/// <param name="Subject">The <c>sub</c> claim. Used alone, it ends the subject's sessions that began before <paramref name="IssuedAt"/>.</param>
/// <param name="JwtId">The <c>jti</c> claim, used to refuse a replayed token.</param>
/// <param name="IssuedAt">The <c>iat</c> claim.</param>
/// <param name="ExpiresAt">The <c>exp</c> claim.</param>
public sealed record LogoutToken(
    string Issuer,
    string? Subject,
    string? SessionId,
    string JwtId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// The outcome of validating a logout token. <see cref="Error"/> says which check failed: its
/// <see cref="ThunderIDException.Code"/> is one of <see cref="ThunderIDErrorCodes"/>, and its message
/// never contains the token.
/// </summary>
/// <param name="Token">The validated token, or <c>null</c> when a check failed.</param>
/// <param name="Error">Why the token was refused, or <c>null</c> when it's valid.</param>
public sealed record LogoutTokenValidationResult(LogoutToken? Token, ThunderIDException? Error)
{
    /// <summary>Whether the token passed every check, so <see cref="Token"/> is set.</summary>
    public bool IsValid => Token is not null;

    internal static LogoutTokenValidationResult Valid(LogoutToken token) => new(token, null);

    internal static LogoutTokenValidationResult Invalid(string code, string reason) => new(null, new ThunderIDRuntimeException(code, reason));
}
