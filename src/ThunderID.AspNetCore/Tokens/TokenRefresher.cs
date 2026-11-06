using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ThunderID.AspNetCore;

internal sealed class TokenRefreshSettings
{
    public static readonly TimeSpan DefaultRefreshBeforeExpiry = TimeSpan.FromSeconds(60);

    public TimeSpan RefreshBeforeExpiry { get; set; } = DefaultRefreshBeforeExpiry;
}

/// <summary>
/// Refreshes a cookie session's tokens shortly before the access token expires. One refresh token is
/// sent to ThunderID once: concurrent requests share the refresh, and its result is kept for a while,
/// because ThunderID treats a rotated refresh token sent again as a replay and revokes the whole
/// session, and a browser can still send the old cookie after another request replaced it. This holds
/// within one process only.
/// </summary>
internal sealed partial class TokenRefresher(
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    IOptionsMonitor<TokenRefreshSettings> settings,
    TimeProvider time,
    ILogger<TokenRefresher> logger)
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan ResultLifetime = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);
    private DateTimeOffset _nextSweep;

    internal int AttemptCount => _attempts.Count;

    private DateTimeOffset Now => time.GetUtcNow();

    public async Task RefreshIfDueAsync(CookieValidatePrincipalContext context)
    {
        var properties = context.Properties;
        var refreshToken = properties.GetTokenValue("refresh_token");
        if (string.IsNullOrEmpty(refreshToken)
            || !DateTimeOffset.TryParse(properties.GetTokenValue("expires_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt)
            || expiresAt - settings.CurrentValue.RefreshBeforeExpiry > time.GetUtcNow())
        {
            return;
        }

        var outcome = await RefreshAsync(refreshToken);
        if (outcome.Tokens is { } tokens)
        {
            Store(properties, tokens);
            context.ShouldRenew = true;
        }
        else if (outcome.EndsSession)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
        }
    }

    private Task<Outcome> RefreshAsync(string refreshToken)
    {
        var now = time.GetUtcNow();
        Sweep(now);

        // Keyed by a hash so the map never holds a refresh token that's still usable.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
        while (true)
        {
            var attempt = _attempts.GetOrAdd(key, _ => new Attempt(this, refreshToken));
            if (attempt.CompletedAt is { } completed && now - completed > ResultLifetime)
            {
                _attempts.TryRemove(new KeyValuePair<string, Attempt>(key, attempt));
                continue;
            }

            return attempt.Run(key);
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        if (now < _nextSweep)
        {
            return;
        }

        _nextSweep = now + ResultLifetime;
        foreach (var (key, attempt) in _attempts)
        {
            if (attempt.CompletedAt is { } completed && now - completed > ResultLifetime)
            {
                _attempts.TryRemove(new KeyValuePair<string, Attempt>(key, attempt));
            }
        }
    }

    private static void Store(AuthenticationProperties properties, TokenSet tokens)
    {
        var values = properties.GetTokens().ToDictionary(t => t.Name, t => t.Value, StringComparer.Ordinal);
        values["access_token"] = tokens.AccessToken;
        if (tokens.RefreshToken is not null)
        {
            values["refresh_token"] = tokens.RefreshToken;
        }

        if (tokens.IdToken is not null)
        {
            values["id_token"] = tokens.IdToken;
        }

        if (tokens.ExpiresAt is { } expiresAt)
        {
            values["expires_at"] = expiresAt.ToString("o", CultureInfo.InvariantCulture);
        }
        else
        {
            values.Remove("expires_at");
        }

        properties.StoreTokens(values.Select(v => new AuthenticationToken { Name = v.Key, Value = v.Value }));
    }

    // Never logs or returns a token.
    private async Task<Outcome> SendAsync(string refreshToken)
    {
        var oidc = oidcOptions.Get(OpenIdConnectDefaults.AuthenticationScheme);

        // Not tied to the request that started the refresh: other requests wait on the same result.
        using var timeout = new CancellationTokenSource(RequestTimeout, time);
        Outcome outcome;
        try
        {
            var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(timeout.Token);
            if (string.IsNullOrEmpty(configuration.TokenEndpoint))
            {
                outcome = Outcome.Kept(new ThunderIDRuntimeException(ThunderIDErrorCodes.RefreshEndpointMissing, "ThunderID's discovery document has no token_endpoint."));
            }
            else
            {
                var form = new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = oidc.ClientId!,
                };
                if (!string.IsNullOrEmpty(oidc.ClientSecret))
                {
                    form["client_secret"] = oidc.ClientSecret;
                }

                using var response = await oidc.Backchannel.PostAsync(configuration.TokenEndpoint, new FormUrlEncodedContent(form), timeout.Token);
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                var status = (int)response.StatusCode;
                outcome = status switch
                {
                    >= 200 and < 300 => Parse(body),
                    >= 400 and < 500 => Outcome.Ended(new ThunderIDApiException(ThunderIDErrorCodes.RefreshRejected,
                        $"ThunderID's token endpoint refused the refresh token{ErrorCode(body)}.", status, response.ReasonPhrase)),
                    _ => Outcome.Kept(new ThunderIDApiException(ThunderIDErrorCodes.RefreshServerError,
                        $"ThunderID's token endpoint answered the refresh with an error{ErrorCode(body)}.", status, response.ReasonPhrase)),
                };
            }
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
        {
            outcome = Outcome.Kept(new ThunderIDRuntimeException(ThunderIDErrorCodes.RefreshTimedOut,
                $"ThunderID's token endpoint did not answer the refresh within {RequestTimeout.TotalSeconds} seconds.", ex));
        }
        catch (ThunderIDException ex)
        {
            // Discovery failed or named another issuer, so the refresh token was never sent.
            outcome = Outcome.Kept(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            outcome = Outcome.Kept(new ThunderIDRuntimeException(ThunderIDErrorCodes.RefreshUnreachable, "ThunderID's token endpoint could not be reached to refresh the tokens.", ex));
        }

        if (outcome.Error is not { } error)
        {
            logger.LogInformation("Refreshed the session's tokens at ThunderID");
        }
        else if (outcome.EndsSession)
        {
            logger.LogWarning("Refreshing the session's tokens failed ({Code}): {Reason} The local session ends.", error.Code, error.Message);
        }
        else
        {
            logger.LogWarning(error.InnerException, "Refreshing the session's tokens failed ({Code}): {Reason} The session is kept, and the next request tries again.", error.Code, error.Message);
        }

        return outcome;
    }

    private Outcome Parse(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("access_token", out var accessToken) && accessToken.ValueKind == JsonValueKind.String && accessToken.GetString() is { Length: > 0 } access)
            {
                DateTimeOffset? expiresAt = root.TryGetProperty("expires_in", out var expiresIn) && expiresIn.TryGetInt64(out var seconds) && seconds > 0
                    ? time.GetUtcNow().AddSeconds(seconds)
                    : null;
                return Outcome.Refreshed(new TokenSet(access, String(root, "refresh_token"), String(root, "id_token"), expiresAt));
            }
        }
        catch (JsonException)
        {
            // Reported below without the parser's message, which can quote the body.
        }

        return Outcome.Kept(new ThunderIDRuntimeException(ThunderIDErrorCodes.RefreshResponseInvalid, "ThunderID's token endpoint answered the refresh without an access token."));
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s ? s : null;

    // ThunderID's OAuth error code, such as invalid_grant, when it's a plain code; never the description.
    internal static string ErrorCode(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                && error.GetString() is { } code && OAuthErrorCode().IsMatch(code)
                ? $" ({code})"
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    [GeneratedRegex("^[a-z_]{1,64}$")]
    private static partial Regex OAuthErrorCode();

    private sealed record TokenSet(string AccessToken, string? RefreshToken, string? IdToken, DateTimeOffset? ExpiresAt);

    private sealed record Outcome(TokenSet? Tokens, ThunderIDException? Error, bool EndsSession)
    {
        public static Outcome Refreshed(TokenSet tokens) => new(tokens, null, false);

        public static Outcome Ended(ThunderIDException error) => new(null, error, true);

        public static Outcome Kept(ThunderIDException error) => new(null, error, false);
    }

    private sealed class Attempt(TokenRefresher refresher, string refreshToken)
    {
        private readonly Lazy<Task<Outcome>> _outcome = new(() => refresher.SendAsync(refreshToken));

        public DateTimeOffset? CompletedAt { get; private set; }

        // Every caller waits on the one send; the first to see it finish records the result.
        public async Task<Outcome> Run(string key)
        {
            var outcome = await _outcome.Value;
            if (outcome.Tokens is null && !outcome.EndsSession)
            {
                // Nothing to keep: the next request tries again.
                refresher._attempts.TryRemove(new KeyValuePair<string, Attempt>(key, this));
            }
            else
            {
                CompletedAt ??= refresher.Now;
            }

            return outcome;
        }
    }
}
