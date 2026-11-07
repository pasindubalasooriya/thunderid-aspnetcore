namespace ThunderID.AspNetCore;

/// <summary>The RFC 8693 token type identifiers ThunderID accepts.</summary>
public static class ThunderIDTokenTypes
{
    /// <summary>An OAuth 2.0 access token.</summary>
    public const string AccessToken = "urn:ietf:params:oauth:token-type:access_token";

    /// <summary>An OAuth 2.0 refresh token.</summary>
    public const string RefreshToken = "urn:ietf:params:oauth:token-type:refresh_token";

    /// <summary>An OpenID Connect ID token.</summary>
    public const string IdToken = "urn:ietf:params:oauth:token-type:id_token";

    /// <summary>A JWT.</summary>
    public const string Jwt = "urn:ietf:params:oauth:token-type:jwt";
}

/// <summary>An RFC 8693 token exchange, sent by <see cref="ThunderIDTokenExtensions.ExchangeThunderIDTokenAsync"/>.</summary>
public sealed class ThunderIDTokenExchangeRequest
{
    /// <summary>The token to exchange. When null, the signed-in user's access token, refreshed first if due.</summary>
    public string? SubjectToken { get; set; }

    /// <summary>The subject token's type. Default <see cref="ThunderIDTokenTypes.AccessToken"/>.</summary>
    public string SubjectTokenType { get; set; } = ThunderIDTokenTypes.AccessToken;

    /// <summary>
    /// The resource server the new token is for, as an absolute URI. It must be registered in ThunderID,
    /// whose answer is otherwise <c>invalid_target</c>; it becomes the new token's audience.
    /// </summary>
    public string? Resource { get; set; }

    /// <summary>Sent as <c>audience</c>. ThunderID doesn't use it: set <see cref="Resource"/> to choose the audience.</summary>
    public string? Audience { get; set; }

    /// <summary>Scopes to request. ThunderID leaves out any the user isn't allowed, without an error.</summary>
    public IList<string> Scopes { get; } = new List<string>();

    /// <summary>The type of token wanted, such as <see cref="ThunderIDTokenTypes.Jwt"/>. ThunderID's default is an access token.</summary>
    public string? RequestedTokenType { get; set; }

    /// <summary>A token for the party acting on the subject's behalf. Needs <see cref="ActorTokenType"/>.</summary>
    public string? ActorToken { get; set; }

    /// <summary>The actor token's type, such as <see cref="ThunderIDTokenTypes.AccessToken"/>. Required with <see cref="ActorToken"/>.</summary>
    public string? ActorTokenType { get; set; }
}

/// <summary>The token ThunderID issued for a token exchange. The session's own tokens are unchanged.</summary>
public sealed class ThunderIDTokenExchangeResult
{
    internal ThunderIDTokenExchangeResult(string accessToken, string? issuedTokenType, string? tokenType, TimeSpan? expiresIn, IReadOnlyList<string> scopes, string? refreshToken)
    {
        AccessToken = accessToken;
        IssuedTokenType = issuedTokenType;
        TokenType = tokenType;
        ExpiresIn = expiresIn;
        Scopes = scopes;
        RefreshToken = refreshToken;
    }

    /// <summary>The issued token. Its type is <see cref="IssuedTokenType"/>, despite the name RFC 8693 gives the field.</summary>
    public string AccessToken { get; }

    /// <summary>The issued token's type, from <c>issued_token_type</c>, such as <see cref="ThunderIDTokenTypes.AccessToken"/>.</summary>
    public string? IssuedTokenType { get; }

    /// <summary>How to send it, usually <c>Bearer</c>.</summary>
    public string? TokenType { get; }

    /// <summary>How long the issued token is valid, from <c>expires_in</c>. Null when ThunderID didn't say.</summary>
    public TimeSpan? ExpiresIn { get; }

    /// <summary>The scopes granted, which can be fewer than requested. Empty when ThunderID didn't say.</summary>
    public IReadOnlyList<string> Scopes { get; }

    /// <summary>A refresh token for the issued token, when ThunderID returns one.</summary>
    public string? RefreshToken { get; }
}
