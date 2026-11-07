namespace ThunderID.AspNetCore;

/// <summary>Settings for signing users in to a web app with ThunderID.</summary>
public sealed class ThunderIDOptions
{
    /// <summary>ThunderID's issuer URL, for example <c>https://localhost:8090</c>.</summary>
    public string Authority { get; set; } = "";

    /// <summary>The application's client ID in ThunderID. Required.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>
    /// The application's client secret, for a confidential client. Sent with the token, refresh,
    /// revocation and token exchange requests. Leave it null for a public client. Keep it out of
    /// source control, for example in user secrets or an environment variable.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>Scopes requested in addition to <c>openid</c>, <c>profile</c> and <c>email</c>.</summary>
    public IList<string> Scopes { get; } = new List<string>();

    /// <summary>
    /// RFC 8707 resource indicator. ThunderID uses it as the access token's audience, and only
    /// grants permission scopes defined on that resource server.
    /// </summary>
    public string? Resource { get; set; }

    /// <summary>
    /// How long before the access token expires the session's tokens are refreshed, on the next request
    /// with the session. Default 60 seconds. Refresh needs a refresh token, which ThunderID issues when
    /// the application's grant types include <c>refresh_token</c>.
    /// </summary>
    public TimeSpan RefreshBeforeExpiry { get; set; } = TokenRefreshSettings.DefaultRefreshBeforeExpiry;
}

/// <summary>Settings for an API that accepts ThunderID access tokens.</summary>
public sealed class ThunderIDApiOptions
{
    /// <summary>ThunderID's issuer URL, for example <c>https://localhost:8090</c>.</summary>
    public string Authority { get; set; } = "";

    /// <summary>The API's resource server identifier in ThunderID, which is the token's <c>aud</c>.</summary>
    public string Audience { get; set; } = "";
}
