# ThunderID.AspNetCore

Sign users in to ASP.NET Core apps with [ThunderID](https://github.com/thunder-id/thunderid), the
open-source IAM stack, keep their tokens fresh, protect APIs with ThunderID access tokens, and end
sessions when ThunderID sends an OIDC back-channel logout.

Requires .NET 10.

```csharp
using ThunderID.AspNetCore;
using ThunderID.AspNetCore.BackchannelLogout;

// Web app
builder.Services.AddThunderID(options =>
{
    options.Authority = builder.Configuration["ThunderID:Authority"]!;
    options.ClientId = builder.Configuration["ThunderID:ClientId"]!;
    options.ClientSecret = builder.Configuration["ThunderID:ClientSecret"];
});
// ...
app.MapThunderIDBackchannelLogout("/backchannel-logout");

// API
builder.Services.AddThunderIDApi(options =>
{
    options.Authority = builder.Configuration["ThunderID:Authority"]!;
    options.Audience = builder.Configuration["ThunderID:Audience"]!;
});
builder.Services.AddAuthorizationBuilder()
    .AddScopePolicy("billing:read", "billing:read");
```

| API | What it does |
|---|---|
| `AddThunderID` | Cookie and OpenID Connect sign-in with the settings ThunderID needs, checked when the app starts. Refreshes tokens ahead of expiry. Given a configuration section, applies reloaded settings without a restart |
| `GetThunderIDAccessTokenAsync` | The signed-in user's access token, refreshed first if it was close to expiry |
| `ExchangeThunderIDTokenAsync` | RFC 8693 token exchange for a token meant for another resource server |
| `ThunderIDJwt.DecodeUnverified` | Reads a JWT's claims without verifying them; never use it to authorize |
| `MapThunderIDBackchannelLogout` | Receives ThunderID's logout token and refuses that session's cookie from the next request |
| `AddThunderIDApi` | JWT bearer for APIs, keeping claim names as they appear in the token |
| `AddScopePolicy` | Authorization policy for ThunderID's space-delimited `scope` claim |
| `ILogoutSessionStore` | Where ended sessions are kept; in memory by default, replace it for more than one instance |

Implements the ThunderID SDK specification v0.9. Follows semantic versioning from 1.0.0: nothing
public is removed or changed incompatibly within 1.x, and error codes keep their meaning.

Quickstart, configuration, production notes and limits:
[github.com/pasindubalasooriya/thunderid-aspnetcore](https://github.com/pasindubalasooriya/thunderid-aspnetcore)
