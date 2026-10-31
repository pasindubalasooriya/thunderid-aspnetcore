namespace ThunderID.AspNetCore.BackchannelLogout;

/// <summary>A logout token that passed every validation check.</summary>
/// <param name="SessionId">The <c>sid</c> claim. When present, only that session ends.</param>
/// <param name="Subject">The <c>sub</c> claim. Used alone, it ends the subject's sessions that began before <paramref name="IssuedAt"/>.</param>
public sealed record LogoutToken(
    string Issuer,
    string? Subject,
    string? SessionId,
    string JwtId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

/// <summary>The outcome of validating a logout token. <see cref="Error"/> never contains the token.</summary>
public sealed record LogoutTokenValidationResult(LogoutToken? Token, string? Error)
{
    public bool IsValid => Token is not null;

    internal static LogoutTokenValidationResult Valid(LogoutToken token) => new(token, null);

    internal static LogoutTokenValidationResult Invalid(string error) => new(null, error);
}
