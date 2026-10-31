using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ThunderID.AspNetCore.BackchannelLogout;

/// <summary>
/// The server can't delete a browser's cookie, so every request's cookie is checked against the
/// logout store, and a session ThunderID has ended is refused and its cookie removed.
/// </summary>
internal static class BackchannelLogoutCookieValidator
{
    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        if (principal is null)
        {
            return;
        }

        var sessionId = principal.FindFirst("sid")?.Value;
        var subject = principal.FindFirst("sub")?.Value;
        if (sessionId is null && subject is null)
        {
            return;
        }

        // auth_time is when the user authenticated at ThunderID; fall back to when the cookie was issued.
        DateTimeOffset? authTime = long.TryParse(principal.FindFirst("auth_time")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : context.Properties.IssuedUtc;

        var store = context.HttpContext.RequestServices.GetRequiredService<ILogoutSessionStore>();
        if (await store.IsSessionEndedAsync(sessionId, subject, authTime, context.HttpContext.RequestAborted))
        {
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(BackchannelLogoutCookieValidator))
                .LogInformation("Refused a cookie session ended by back-channel logout: sid {SessionId}", sessionId);

            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
        }
    }
}
