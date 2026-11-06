namespace ThunderID.AspNetCore;

/// <summary>
/// Every code ThunderID.AspNetCore raises. Where ThunderID's JavaScript SDKs have a code for the same
/// condition, it is reused unchanged (the <c>JS-</c> and <c>NODE-</c> codes); the rest start with
/// <c>ASPNETCORE-</c>. The kind before the number: <c>IV</c> invalid, <c>NF</c> not found, <c>HE</c> ThunderID
/// answered with an error, <c>NE</c> network failure, and <c>NR</c> a request that failed, kept from a reused
/// JavaScript code.
/// </summary>
public static class ThunderIDErrorCodes
{
    // Configuration and registration

    /// <summary>A ThunderID setting is missing or invalid. The message names each one, never its value.</summary>
    public const string InvalidSettings = "ASPNETCORE-CONFIG-VAL-IV01";

    /// <summary>An operation was used without <c>AddThunderID</c>.</summary>
    public const string NotRegistered = "ASPNETCORE-CONFIG-REG-NF01";

    /// <summary><c>AddScopePolicy</c> was given no scopes.</summary>
    public const string ScopePolicyHasNoScopes = "ASPNETCORE-SCOPE_POLICY-ASP-IV01";

    /// <summary><c>LogoutTokenValidator</c> was given no client ID.</summary>
    public const string ValidatorHasNoClientId = "ASPNETCORE-LOGOUT_TOKEN-CTOR-IV01";

    /// <summary><c>InMemoryLogoutSessionStore</c> was given a retention that isn't positive.</summary>
    public const string SessionStoreRetentionInvalid = "ASPNETCORE-SESSION_STORE-CTOR-IV01";

    // Discovery

    /// <summary>The discovery document names an issuer other than the configured <c>Authority</c>, so none of it is used.</summary>
    public const string DiscoveryIssuerMismatch = "ASPNETCORE-DISCOVERY-GC-IV01";

    /// <summary>ThunderID answered the request for its discovery document or signing keys with an error.</summary>
    public const string DiscoveryRejected = "ASPNETCORE-DISCOVERY-GC-HE01";

    /// <summary>The discovery document or the signing keys couldn't be fetched.</summary>
    public const string DiscoveryUnavailable = "ASPNETCORE-DISCOVERY-GC-NE01";

    // Sign-in callback

    /// <summary>ThunderID's token endpoint refused the authorization code.</summary>
    public const string SignInTokenRequestRejected = "ASPNETCORE-SIGN_IN-CB-HE01";

    /// <summary>ThunderID sent an error to the callback instead of an authorization code, for example <c>access_denied</c>.</summary>
    public const string SignInErrorResponse = "ASPNETCORE-SIGN_IN-CB-IV01";

    /// <summary>The callback failed a check: state, correlation, nonce or ID token validation.</summary>
    public const string SignInCallbackInvalid = "ASPNETCORE-SIGN_IN-CB-IV02";

    // Sign-out

    /// <summary>ThunderID's revocation endpoint answered with an error.</summary>
    public const string RevocationRejected = "ASPNETCORE-SIGN_OUT-REV-HE01";

    /// <summary>ThunderID's revocation endpoint couldn't be reached.</summary>
    public const string RevocationUnreachable = "ASPNETCORE-SIGN_OUT-REV-NE01";

    /// <summary>ThunderID's revocation endpoint didn't answer in time.</summary>
    public const string RevocationTimedOut = "ASPNETCORE-SIGN_OUT-REV-NE02";

    /// <summary>ThunderID's discovery document has no <c>revocation_endpoint</c>.</summary>
    public const string RevocationEndpointMissing = "ASPNETCORE-SIGN_OUT-REV-NF01";

    // Token refresh

    /// <summary>ThunderID's token endpoint refused the refresh token (a 4xx answer), so the local session ends.</summary>
    public const string RefreshRejected = "JS-AUTH_CORE-RAT2-HE04";

    /// <summary>ThunderID's token endpoint answered the refresh with a server error (5xx). The session is kept.</summary>
    public const string RefreshServerError = "ASPNETCORE-REFRESH-RAT-HE01";

    /// <summary>ThunderID's token endpoint answered the refresh with something that isn't a token response. The session is kept.</summary>
    public const string RefreshResponseInvalid = "ASPNETCORE-REFRESH-RAT-IV01";

    /// <summary>ThunderID's token endpoint couldn't be reached to refresh the tokens. The session is kept.</summary>
    public const string RefreshUnreachable = "JS-AUTH_CORE-RAT2-NR03";

    /// <summary>ThunderID's token endpoint didn't answer the refresh in time. The session is kept.</summary>
    public const string RefreshTimedOut = "ASPNETCORE-REFRESH-RAT-NE01";

    /// <summary>ThunderID's discovery document has no <c>token_endpoint</c> to refresh with.</summary>
    public const string RefreshEndpointMissing = "JS-AUTH_CORE-RAT2-NF02";

    // Token exchange

    /// <summary>The token exchange request is invalid, so it wasn't sent. The message names the field.</summary>
    public const string TokenExchangeInvalid = "ASPNETCORE-TOKEN_EXCHANGE-EXC-IV01";

    /// <summary>No subject token was given and there's no signed-in session to take one from.</summary>
    public const string TokenExchangeNoSubject = "ASPNETCORE-TOKEN_EXCHANGE-EXC-NF01";

    /// <summary>ThunderID's discovery document has no <c>token_endpoint</c> to exchange at.</summary>
    public const string TokenExchangeEndpointMissing = "JS-AUTH_CORE-RCG-NF01";

    /// <summary>ThunderID's token endpoint refused the exchange.</summary>
    public const string TokenExchangeRejected = "JS-AUTH_CORE-RCG-HE03";

    /// <summary>ThunderID's token endpoint couldn't be reached for the exchange.</summary>
    public const string TokenExchangeUnreachable = "JS-AUTH_CORE-RCG-NE02";

    /// <summary>ThunderID's token endpoint didn't answer the exchange in time.</summary>
    public const string TokenExchangeTimedOut = "ASPNETCORE-TOKEN_EXCHANGE-EXC-NE01";

    // Decoding

    /// <summary>The value given to <c>ThunderIDJwt.DecodeUnverified</c> isn't a JWT with a JSON payload.</summary>
    public const string JwtMalformed = "JS-CRYPTO_UTIL-DIT-IV02";

    // Settings reload

    /// <summary>
    /// Reloaded settings change <c>ClientId</c> or <c>Authority</c>, which can't change while the app runs.
    /// The whole change is ignored and the previous settings stay.
    /// </summary>
    public const string ReloadImmutableSetting = "ASPNETCORE-CONFIG-RLD-IV01";

    // Back-channel logout request

    /// <summary>The request's content type isn't <c>application/x-www-form-urlencoded</c>.</summary>
    public const string BackchannelContentType = "ASPNETCORE-BCL_ENDPOINT-HBL-IV01";

    /// <summary>The request body is larger than the cap.</summary>
    public const string BackchannelBodyTooLarge = "ASPNETCORE-BCL_ENDPOINT-HBL-IV02";

    /// <summary>The request body doesn't carry exactly one <c>logout_token</c>.</summary>
    public const string BackchannelTokenCount = "ASPNETCORE-BCL_ENDPOINT-HBL-IV03";

    /// <summary>The logout token's <c>jti</c> was already used.</summary>
    public const string LogoutTokenReplayed = "NODE-AUTH_CLIENT-HBL-IV01";

    // Logout token

    /// <summary>The logout token is missing.</summary>
    public const string LogoutTokenMissing = "ASPNETCORE-LOGOUT_TOKEN-VLT-IV01";

    /// <summary>The logout token is too large to be one.</summary>
    public const string LogoutTokenTooLarge = "ASPNETCORE-LOGOUT_TOKEN-VLT-IV02";

    /// <summary>The logout token isn't a signed JWT.</summary>
    public const string LogoutTokenMalformed = "JS-AUTH_HELPER-VLT-IV01";

    /// <summary>The logout token's <c>typ</c> isn't <c>logout+jwt</c>.</summary>
    public const string LogoutTokenType = "JS-LOGOUT_TOKEN-VLTC-IV02";

    /// <summary>The logout token's signing algorithm isn't accepted.</summary>
    public const string LogoutTokenAlgorithm = "JS-LOGOUT_TOKEN-VLTC-IV03";

    /// <summary>The logout token carries a <c>nonce</c>.</summary>
    public const string LogoutTokenNonce = "JS-LOGOUT_TOKEN-VLTC-IV04";

    /// <summary>The logout token's <c>events</c> has no back-channel logout member.</summary>
    public const string LogoutTokenEvent = "JS-LOGOUT_TOKEN-VLTC-IV05";

    /// <summary>The logout token's issuer isn't ThunderID's.</summary>
    public const string LogoutTokenIssuer = "JS-LOGOUT_TOKEN-VLTC-IV06";

    /// <summary>The logout token's audience doesn't include the client ID.</summary>
    public const string LogoutTokenAudience = "JS-LOGOUT_TOKEN-VLTC-IV07";

    /// <summary>The logout token's <c>iat</c> is missing or in the future.</summary>
    public const string LogoutTokenIssuedAt = "JS-LOGOUT_TOKEN-VLTC-IV08";

    /// <summary>The logout token has expired, or has no <c>exp</c>.</summary>
    public const string LogoutTokenExpired = "JS-LOGOUT_TOKEN-VLTC-IV09";

    /// <summary>The logout token's <c>nbf</c> is in the future.</summary>
    public const string LogoutTokenNotYetValid = "ASPNETCORE-LOGOUT_TOKEN-VLT-IV03";

    /// <summary>The logout token names neither a session (<c>sid</c>) nor a subject (<c>sub</c>).</summary>
    public const string LogoutTokenNoSessionOrSubject = "JS-LOGOUT_TOKEN-VLTC-IV11";

    /// <summary>The logout token has no <c>jti</c>.</summary>
    public const string LogoutTokenJwtIdMissing = "JS-LOGOUT_TOKEN-VLTC-IV12";

    /// <summary>The logout token's signature is invalid.</summary>
    public const string LogoutTokenSignature = "JS-CRYPTO_HELPER-VLTS-IV01";

    /// <summary>The logout token is signed with a key ThunderID's JWKS doesn't have, even after one re-fetch.</summary>
    public const string LogoutTokenSigningKeyUnknown = "ASPNETCORE-LOGOUT_TOKEN-VLT-IV04";

    /// <summary>The logout token failed a validation check not listed above.</summary>
    public const string LogoutTokenInvalid = "ASPNETCORE-LOGOUT_TOKEN-VLT-IV05";
}
