using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ThunderID.AspNetCore;

/// <summary>Signs the user out of the app and of ThunderID.</summary>
public static class ThunderIDSignOutExtensions
{
    // Sign-out waits on revocation, so an unreachable ThunderID must not hold it for the
    // OIDC handler's 60-second back-channel timeout.
    internal static readonly TimeSpan RevocationTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Signs the user out: revokes the session's refresh token at ThunderID (or its access token,
    /// when there is no refresh token), clears the cookie session, then redirects to ThunderID's
    /// end-session endpoint, which returns to <paramref name="redirectUri"/>. The cookie is cleared
    /// even when revocation fails; the result reports the failure. With no session, nothing is
    /// revoked and the response redirects straight to <paramref name="redirectUri"/>.
    /// </summary>
    public static async Task<ThunderIDSignOutResult> SignOutOfThunderIDAsync(this HttpContext context, string? redirectUri = null)
    {
        ThunderIDAuthenticationExtensions.EnsureRegistered(context.RequestServices, nameof(SignOutOfThunderIDAsync));

        var session = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!session.Succeeded)
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            context.Response.Redirect(redirectUri ?? "/");
            return ThunderIDSignOutResult.Success;
        }

        var error = await RevokeAsync(context, session.Properties);

        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        try
        {
            await context.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties { RedirectUri = redirectUri });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without discovery there is no end-session endpoint. Letting this throw would make the
            // exception handler discard the cookie deletion, so the local sign-out must stand alone.
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ThunderIDSignOutExtensions))
                .LogWarning(ex, "Sign-out could not redirect to ThunderID's end-session endpoint; the local session is cleared");
            context.Response.Redirect(redirectUri ?? "/");
        }

        return error is null ? ThunderIDSignOutResult.Success : new ThunderIDSignOutResult(error);
    }

    // Returns null on success, or why revocation failed. Never logs or returns the token.
    private static async Task<ThunderIDException?> RevokeAsync(HttpContext context, AuthenticationProperties? properties)
    {
        var (token, hint) = properties?.GetTokenValue("refresh_token") is { } refreshToken
            ? (refreshToken, "refresh_token")
            : (properties?.GetTokenValue("access_token"), "access_token");
        if (token is null)
        {
            return null;
        }

        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ThunderIDSignOutExtensions));
        var oidc = context.RequestServices.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);

        ThunderIDException? error;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(RevocationTimeout);

            // The issuer-pinned configuration manager, so the token goes only to the configured ThunderID.
            var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(timeout.Token);
            if (string.IsNullOrEmpty(configuration.RevocationEndpoint))
            {
                error = new ThunderIDRuntimeException(ThunderIDErrorCodes.RevocationEndpointMissing, "ThunderID's discovery document has no revocation_endpoint.");
            }
            else
            {
                var form = new Dictionary<string, string>
                {
                    ["token"] = token,
                    ["token_type_hint"] = hint,
                    ["client_id"] = oidc.ClientId!,
                };
                if (!string.IsNullOrEmpty(oidc.ClientSecret))
                {
                    form["client_secret"] = oidc.ClientSecret;
                }

                using var response = await oidc.Backchannel.PostAsync(configuration.RevocationEndpoint, new FormUrlEncodedContent(form), timeout.Token);
                error = response.IsSuccessStatusCode
                    ? null
                    : new ThunderIDApiException(ThunderIDErrorCodes.RevocationRejected, $"ThunderID's revocation endpoint refused to revoke the {hint}.",
                        (int)response.StatusCode, response.ReasonPhrase);
            }
        }
        catch (OperationCanceledException ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            error = new ThunderIDRuntimeException(ThunderIDErrorCodes.RevocationTimedOut,
                $"ThunderID's revocation endpoint did not answer within {RevocationTimeout.TotalSeconds} seconds.", ex);
        }
        catch (ThunderIDException ex)
        {
            // Discovery failed or named another issuer, so the token was never sent.
            error = ex;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = new ThunderIDRuntimeException(ThunderIDErrorCodes.RevocationUnreachable, "ThunderID's revocation endpoint could not be reached.", ex);
        }

        if (error is null)
        {
            logger.LogInformation("Sign-out revoked the {TokenType} at ThunderID", hint);
        }
        else
        {
            logger.LogWarning(error.InnerException, "Sign-out could not revoke the {TokenType} at ThunderID ({Code}): {Reason}", hint, error.Code, error.Message);
        }

        return error;
    }
}

/// <summary>The outcome of <see cref="ThunderIDSignOutExtensions.SignOutOfThunderIDAsync"/>.</summary>
public sealed class ThunderIDSignOutResult
{
    internal static readonly ThunderIDSignOutResult Success = new(null);

    internal ThunderIDSignOutResult(ThunderIDException? revocationError) => RevocationError = revocationError;

    /// <summary>
    /// True when ThunderID couldn't be told to revoke the session's token. The local session is
    /// cleared regardless, but the token stays usable at ThunderID until it expires.
    /// </summary>
    public bool RevocationFailed => RevocationError is not null;

    /// <summary>
    /// Why revocation failed, or null when it succeeded or there was nothing to revoke. A
    /// <see cref="ThunderIDApiException"/> when ThunderID answered with an error (its status is on the
    /// exception); a <see cref="ThunderIDRuntimeException"/> when ThunderID couldn't be reached, didn't
    /// answer in time, or its discovery document was refused.
    /// </summary>
    public ThunderIDException? RevocationError { get; }
}
