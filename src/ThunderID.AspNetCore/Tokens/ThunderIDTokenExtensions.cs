using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ThunderID.AspNetCore;

/// <summary>The signed-in user's tokens: reading the access token and exchanging it.</summary>
public static class ThunderIDTokenExtensions
{
    internal static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Returns the signed-in user's access token, or null when there's no session. The token was
    /// refreshed when this request was authenticated if it was within <see cref="ThunderIDOptions.RefreshBeforeExpiry"/>
    /// of expiring. When that refresh couldn't reach ThunderID, the current token is returned and may be
    /// close to expiry; the failure is logged with its code. When ThunderID refused it, the session has
    /// ended and this returns null.
    /// </summary>
    public static async Task<string?> GetThunderIDAccessTokenAsync(this HttpContext context)
    {
        ThunderIDAuthenticationExtensions.EnsureRegistered(context.RequestServices, nameof(GetThunderIDAccessTokenAsync));

        // The cookie handler refreshes here, or returns the result it already refreshed for this request.
        var session = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return session.Succeeded ? session.Properties?.GetTokenValue("access_token") : null;
    }

    /// <summary>
    /// Exchanges a token at ThunderID's token endpoint (RFC 8693), by default the signed-in user's access
    /// token for one whose audience is <see cref="ThunderIDTokenExchangeRequest.Resource"/>. The session's
    /// own tokens are never changed. The app's grant types in ThunderID must include
    /// <c>urn:ietf:params:oauth:grant-type:token-exchange</c>. Throws a <see cref="ThunderIDException"/>:
    /// <see cref="ThunderIDErrorCodes.TokenExchangeInvalid"/> before any network call when the request is
    /// invalid, and a <see cref="ThunderIDApiException"/> with ThunderID's status when it refuses.
    /// </summary>
    public static async Task<ThunderIDTokenExchangeResult> ExchangeThunderIDTokenAsync(this HttpContext context, ThunderIDTokenExchangeRequest request)
    {
        ThunderIDAuthenticationExtensions.EnsureRegistered(context.RequestServices, nameof(ExchangeThunderIDTokenAsync));
        Validate(request);

        var subjectToken = request.SubjectToken ?? await context.GetThunderIDAccessTokenAsync()
            ?? throw new ThunderIDRuntimeException(ThunderIDErrorCodes.TokenExchangeNoSubject,
                "There's no subject token to exchange: none was given and no user is signed in.");

        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ThunderIDTokenExtensions));
        var oidc = context.RequestServices.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(ExchangeTimeout);
        try
        {
            // The issuer-pinned configuration manager, so the token goes only to the configured ThunderID.
            var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(timeout.Token);
            if (string.IsNullOrEmpty(configuration.TokenEndpoint))
            {
                throw new ThunderIDRuntimeException(ThunderIDErrorCodes.TokenExchangeEndpointMissing, "ThunderID's discovery document has no token_endpoint.");
            }

            using var response = await oidc.Backchannel.PostAsync(configuration.TokenEndpoint, new FormUrlEncodedContent(Form(request, subjectToken, oidc)), timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new ThunderIDApiException(ThunderIDErrorCodes.TokenExchangeRejected,
                    $"ThunderID's token endpoint refused the token exchange{TokenRefresher.ErrorCode(body)}.", (int)response.StatusCode, response.ReasonPhrase);
            }

            var result = Parse(body);
            logger.LogInformation("Exchanged a token at ThunderID for a {TokenType}", result.IssuedTokenType ?? "token");
            return result;
        }
        catch (ThunderIDException ex)
        {
            throw Logged(logger, ex);
        }
        catch (OperationCanceledException ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            throw Logged(logger, new ThunderIDRuntimeException(ThunderIDErrorCodes.TokenExchangeTimedOut,
                $"ThunderID's token endpoint did not answer the token exchange within {ExchangeTimeout.TotalSeconds} seconds.", ex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Logged(logger, new ThunderIDRuntimeException(ThunderIDErrorCodes.TokenExchangeUnreachable,
                "ThunderID's token endpoint could not be reached for the token exchange.", ex));
        }
    }

    // Never logs a token: the messages are fixed text plus ThunderID's OAuth error code.
    private static ThunderIDException Logged(ILogger logger, ThunderIDException error)
    {
        logger.LogWarning(error.InnerException, "Token exchange failed ({Code}): {Reason}", error.Code, error.Message);
        return error;
    }

    // Every problem at once, naming the setting and never its value.
    private static void Validate(ThunderIDTokenExchangeRequest? request)
    {
        if (request is null)
        {
            throw Invalid(["A request is required."]);
        }

        var failures = new List<string>();
        if (request.SubjectToken is { } subject && string.IsNullOrWhiteSpace(subject))
        {
            failures.Add("SubjectToken is empty; leave it null to use the signed-in user's access token.");
        }

        AbsoluteUri(failures, nameof(request.SubjectTokenType), request.SubjectTokenType, required: true);
        AbsoluteUri(failures, nameof(request.RequestedTokenType), request.RequestedTokenType, required: false);
        if (request.Resource is not null && (!Uri.TryCreate(request.Resource, UriKind.Absolute, out var resource) || resource.Fragment.Length > 0))
        {
            failures.Add("Resource must be an absolute URI without a fragment.");
        }

        if (request.Audience is { } audience && string.IsNullOrWhiteSpace(audience))
        {
            failures.Add("Audience is empty.");
        }

        // RFC 6749 scope-token: visible ASCII except space, double quote and backslash.
        if (request.Scopes.Any(scope => string.IsNullOrEmpty(scope) || scope.Any(c => c < '!' || c > '~' || c is '"' or '\\')))
        {
            failures.Add("Each scope must be non-empty, with no spaces, quotes or backslashes.");
        }

        if (request.ActorToken is null != request.ActorTokenType is null)
        {
            failures.Add("ActorToken and ActorTokenType must be set together.");
        }
        else if (request.ActorToken is { } actor && string.IsNullOrWhiteSpace(actor))
        {
            failures.Add("ActorToken is empty.");
        }

        AbsoluteUri(failures, nameof(request.ActorTokenType), request.ActorTokenType, required: false);
        if (failures.Count > 0)
        {
            throw Invalid(failures);
        }
    }

    private static void AbsoluteUri(List<string> failures, string name, string? value, bool required)
    {
        if (value is null ? required : !Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            failures.Add($"{name} must be an absolute URI, such as a ThunderIDTokenTypes value.");
        }
    }

    private static ThunderIDRuntimeException Invalid(IEnumerable<string> failures) =>
        new(ThunderIDErrorCodes.TokenExchangeInvalid, "The token exchange request is invalid, so it wasn't sent: " + string.Join(" ", failures));

    private static Dictionary<string, string> Form(ThunderIDTokenExchangeRequest request, string subjectToken, OpenIdConnectOptions oidc)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = subjectToken,
            ["subject_token_type"] = request.SubjectTokenType,
            ["client_id"] = oidc.ClientId!,
        };
        if (!string.IsNullOrEmpty(oidc.ClientSecret))
        {
            form["client_secret"] = oidc.ClientSecret;
        }

        Add(form, "resource", request.Resource);
        Add(form, "audience", request.Audience);
        Add(form, "scope", request.Scopes.Count > 0 ? string.Join(' ', request.Scopes) : null);
        Add(form, "requested_token_type", request.RequestedTokenType);
        Add(form, "actor_token", request.ActorToken);
        Add(form, "actor_token_type", request.ActorTokenType);
        return form;
    }

    private static void Add(Dictionary<string, string> form, string name, string? value)
    {
        if (value is not null)
        {
            form[name] = value;
        }
    }

    private static ThunderIDTokenExchangeResult Parse(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object && String(root, "access_token") is { } accessToken)
            {
                TimeSpan? expiresIn = root.TryGetProperty("expires_in", out var seconds) && seconds.TryGetInt64(out var s) && s > 0 ? TimeSpan.FromSeconds(s) : null;
                var scopes = String(root, "scope")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
                return new ThunderIDTokenExchangeResult(accessToken, String(root, "issued_token_type"), String(root, "token_type"), expiresIn, scopes, String(root, "refresh_token"));
            }
        }
        catch (JsonException)
        {
            // Reported below without the parser's message, which can quote the body.
        }

        throw new ThunderIDRuntimeException(ThunderIDErrorCodes.TokenExchangeResponseInvalid, "ThunderID's token endpoint answered the token exchange without an access token.");
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s ? s : null;
}
