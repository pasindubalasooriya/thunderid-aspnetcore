using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ThunderID.AspNetCore;

// The SDK sets these authorize parameters itself. A caller's value in AdditionalAuthorizationParameters
// is dropped, so the SDK's is sent; left in, ASP.NET Core throws on the duplicate key at sign-in.
internal sealed class OwnedAuthorizationParameters(ILogger<OwnedAuthorizationParameters> logger) : IPostConfigureOptions<OpenIdConnectOptions>
{
    internal static readonly string[] Keys = ["client_id", "redirect_uri", "code_challenge", "code_challenge_method", "state"];

    public void PostConfigure(string? name, OpenIdConnectOptions options)
    {
        if (name != OpenIdConnectDefaults.AuthenticationScheme)
        {
            return;
        }

        var removed = Keys.Where(options.AdditionalAuthorizationParameters.Remove).ToList();
        if (removed.Count > 0)
        {
            // Key names only: a caller's value may be sensitive.
            logger.LogWarning("ThunderID ignores these sign-in parameters because it sets them itself: {Keys}. Remove them from AdditionalAuthorizationParameters.",
                string.Join(", ", removed));
        }
    }
}
