using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace ThunderID.AspNetCore;

public static class ThunderIDTokenExtensions
{
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
}
