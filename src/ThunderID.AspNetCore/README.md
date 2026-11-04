# ThunderID.AspNetCore

Sign users in to ASP.NET Core apps with [ThunderID](https://github.com/thunder-id/thunderid), the
open-source IAM stack, protect APIs with ThunderID access tokens, and end sessions when ThunderID
sends an OIDC back-channel logout.

Requires .NET 10.

```csharp
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
| `AddThunderID` | Cookie and OpenID Connect sign-in with the settings ThunderID needs, checked when the app starts |
| `MapThunderIDBackchannelLogout` | Receives ThunderID's logout token and refuses that session's cookie from the next request |
| `AddThunderIDApi` | JWT bearer for APIs, keeping claim names as they appear in the token |
| `AddScopePolicy` | Authorization policy for ThunderID's space-delimited `scope` claim |
| `ILogoutSessionStore` | Where ended sessions are kept; in memory by default, replace it for more than one instance |

Implements the ThunderID SDK specification v0.8. Versions are `0.x` for now, so a minor version
can change the API.

Quickstart, configuration, production notes and limits:
[github.com/pasindubalasooriya/thunderid-aspnetcore](https://github.com/pasindubalasooriya/thunderid-aspnetcore)
