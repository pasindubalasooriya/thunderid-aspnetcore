using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ThunderID.AspNetCore.BackchannelLogout;

/// <summary>
/// Validates an OIDC back-channel logout token (OpenID Connect Back-Channel Logout 1.0, section 2.6).
/// None of the checks can be turned off: the typ, events and nonce checks are what stop an ID token
/// from being replayed as a logout token.
/// </summary>
public sealed class LogoutTokenValidator
{
    public const string BackchannelLogoutEvent = "http://schemas.openid.net/event/backchannel-logout";

    /// <summary>
    /// Clock tolerance for token lifetimes, and for the sessions a <c>sub</c>-only logout ends.
    /// </summary>
    public static readonly TimeSpan DefaultClockSkew = TimeSpan.FromMinutes(2);

    // A logout token is a few hundred bytes. Anything far larger is not one.
    internal const int MaxTokenLength = 8 * 1024;

    private static readonly string[] LogoutTokenTypes = ["logout+jwt", "application/logout+jwt"];

    private readonly IConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly string _clientId;
    private readonly TimeSpan _clockSkew;
    private readonly TimeProvider _timeProvider;
    private readonly JsonWebTokenHandler _handler = new();

    /// <param name="configurationManager">
    /// Source of the issuer and signing keys. A <see cref="ConfigurationManager{T}"/> bounds how
    /// often an unknown key identifier can trigger a JWKS re-fetch (its <c>RefreshInterval</c>).
    /// </param>
    public LogoutTokenValidator(
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager,
        string clientId,
        TimeSpan? clockSkew = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            throw new ThunderIDRuntimeException(ThunderIDErrorCodes.ValidatorHasNoClientId, "LogoutTokenValidator needs the application's client ID.");
        }

        _configurationManager = configurationManager;
        _clientId = clientId;
        _clockSkew = clockSkew ?? DefaultClockSkew;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<LogoutTokenValidationResult> ValidateAsync(string? logoutToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(logoutToken))
        {
            return LogoutTokenValidationResult.Invalid(ThunderIDErrorCodes.LogoutTokenMissing, "logout_token is missing");
        }

        if (logoutToken.Length > MaxTokenLength)
        {
            return LogoutTokenValidationResult.Invalid(ThunderIDErrorCodes.LogoutTokenTooLarge, "logout_token is too large");
        }

        TokenValidationResult result;
        try
        {
            var configuration = await _configurationManager.GetConfigurationAsync(cancellationToken);

            // The handler reports an algorithm outside ValidAlgorithms as an invalid signature
            // (IDX10511), so the header is checked first to name the failure correctly.
            if (configuration.IdTokenSigningAlgValuesSupported.Count > 0
                && _handler.CanReadToken(logoutToken)
                && !configuration.IdTokenSigningAlgValuesSupported.Contains(new JsonWebToken(logoutToken).Alg))
            {
                return LogoutTokenValidationResult.Invalid(ThunderIDErrorCodes.LogoutTokenAlgorithm, "signing algorithm is not accepted");
            }

            result = await _handler.ValidateTokenAsync(logoutToken, Parameters(configuration));

            if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
            {
                // The server may have rotated its keys. RequestRefresh is rate limited by the
                // configuration manager, so a stream of unknown kids cannot force a fetch per request.
                _configurationManager.RequestRefresh();
                configuration = await _configurationManager.GetConfigurationAsync(cancellationToken);
                result = await _handler.ValidateTokenAsync(logoutToken, Parameters(configuration));
            }
        }
        catch (ThunderIDException ex) when (ex.Code == ThunderIDErrorCodes.DiscoveryIssuerMismatch)
        {
            // Nothing in a discovery document that names another issuer can be trusted, keys included.
            return LogoutTokenValidationResult.Invalid(ThunderIDErrorCodes.DiscoveryIssuerMismatch, "discovery document was rejected: its issuer is not the configured authority");
        }

        if (!result.IsValid)
        {
            var (code, reason) = Describe(result.Exception);
            return LogoutTokenValidationResult.Invalid(code, reason);
        }

        var jwt = (JsonWebToken)result.SecurityToken;
        using var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.EncodedPayload));
        var claims = payload.RootElement;

        if (!claims.TryGetProperty("iat", out var iatElement) || !iatElement.TryGetInt64(out var iat))
        {
            return LogoutTokenValidationResult.Invalid(ThunderIDErrorCodes.LogoutTokenIssuedAt, "iat is missing");
        }

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(iat);
        if (issuedAt > _timeProvider.GetUtcNow() + _clockSkew)
        {
            return LogoutTokenValidationResult.Invalid(ThunderIDErrorCodes.LogoutTokenIssuedAt, "iat is in the future");
        }

        if (!claims.TryGetProperty("events", out var events)
            || events.ValueKind != JsonValueKind.Object
            || !events.TryGetProperty(BackchannelLogoutEvent, out var logoutEvent)
            || logoutEvent.ValueKind != JsonValueKind.Object)
        {
            return LogoutTokenValidationResult.Invalid(ThunderIDErrorCodes.LogoutTokenEvent, "events has no back-channel logout member");
        }

        if (claims.TryGetProperty("nonce", out _))
        {
            return LogoutTokenValidationResult.Invalid(ThunderIDErrorCodes.LogoutTokenNonce, "nonce must not be present");
        }

        var sessionId = StringClaim(claims, "sid");
        var subject = StringClaim(claims, "sub");
        if (sessionId is null && subject is null)
        {
            return LogoutTokenValidationResult.Invalid(ThunderIDErrorCodes.LogoutTokenNoSessionOrSubject, "neither sid nor sub is present");
        }

        var jwtId = StringClaim(claims, "jti");
        if (jwtId is null)
        {
            return LogoutTokenValidationResult.Invalid(ThunderIDErrorCodes.LogoutTokenJwtIdMissing, "jti is missing");
        }

        return LogoutTokenValidationResult.Valid(new LogoutToken(
            jwt.Issuer, subject, sessionId, jwtId, issuedAt, new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero)));
    }

    private TokenValidationParameters Parameters(OpenIdConnectConfiguration configuration) => new()
    {
        ValidIssuer = configuration.Issuer,
        ValidAudience = _clientId,
        IssuerSigningKeys = configuration.SigningKeys,
        ValidTypes = LogoutTokenTypes,
        // Accept the algorithms the server uses for ID tokens, when it advertises them.
        ValidAlgorithms = configuration.IdTokenSigningAlgValuesSupported.Count > 0
            ? configuration.IdTokenSigningAlgValuesSupported
            : null,
        RequireSignedTokens = true,
        // The kid must name a key in the JWKS; an unknown kid triggers the bounded refresh above.
        TryAllIssuerSigningKeys = false,
        RequireExpirationTime = true,
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = _clockSkew,
        LifetimeValidator = (notBefore, expires, _, parameters) =>
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            return expires is not null
                && expires.Value + parameters.ClockSkew >= now
                && (notBefore is null || notBefore.Value - parameters.ClockSkew <= now);
        },
    };

    private static string? StringClaim(JsonElement claims, string name) =>
        claims.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s
            ? s
            : null;

    // Names the failed check without echoing any part of the token.
    private (string Code, string Reason) Describe(Exception? exception) => exception switch
    {
        SecurityTokenInvalidTypeException => (ThunderIDErrorCodes.LogoutTokenType, "typ is not logout+jwt"),
        SecurityTokenInvalidIssuerException => (ThunderIDErrorCodes.LogoutTokenIssuer, "issuer does not match"),
        SecurityTokenInvalidAudienceException => (ThunderIDErrorCodes.LogoutTokenAudience, "audience does not contain the client id"),
        SecurityTokenNoExpirationException or SecurityTokenInvalidLifetimeException { Expires: null } => (ThunderIDErrorCodes.LogoutTokenExpired, "exp is missing"),
        SecurityTokenNotYetValidException => (ThunderIDErrorCodes.LogoutTokenNotYetValid, "token is not valid yet"),
        // The lifetime validator above reports both directions as this one exception.
        SecurityTokenInvalidLifetimeException { NotBefore: var notBefore } when notBefore != DateTime.MinValue
            && notBefore - _clockSkew > _timeProvider.GetUtcNow().UtcDateTime => (ThunderIDErrorCodes.LogoutTokenNotYetValid, "token is not valid yet"),
        SecurityTokenExpiredException or SecurityTokenInvalidLifetimeException => (ThunderIDErrorCodes.LogoutTokenExpired, "token has expired"),
        SecurityTokenInvalidAlgorithmException => (ThunderIDErrorCodes.LogoutTokenAlgorithm, "signing algorithm is not accepted"),
        SecurityTokenSignatureKeyNotFoundException => (ThunderIDErrorCodes.LogoutTokenSigningKeyUnknown, "signing key is unknown"),
        SecurityTokenInvalidSignatureException => (ThunderIDErrorCodes.LogoutTokenSignature, "signature is invalid"),
        SecurityTokenMalformedException => (ThunderIDErrorCodes.LogoutTokenMalformed, "logout_token is not a JWT"),
        _ => (ThunderIDErrorCodes.LogoutTokenInvalid, "logout_token is invalid"),
    };
}
