using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThunderID.AspNetCore.BackchannelLogout;

namespace ThunderID.AspNetCore;

public static class ThunderIDAuthenticationExtensions
{
    /// <summary>
    /// Signs users in with ThunderID: a cookie session plus OpenID Connect, with the settings
    /// ThunderID needs that differ from ASP.NET Core's defaults.
    /// </summary>
    public static AuthenticationBuilder AddThunderID(this IServiceCollection services, Action<ThunderIDOptions> configure)
    {
        var settings = new ThunderIDOptions();
        configure(settings);
        return AddThunderID(services, () => settings);
    }

    /// <summary>
    /// Signs users in with ThunderID, with settings read from a configuration section such as
    /// <c>builder.Configuration.GetSection("ThunderID")</c>. When the configuration reloads,
    /// <see cref="ThunderIDOptions.ClientSecret"/>, <see cref="ThunderIDOptions.Scopes"/>,
    /// <see cref="ThunderIDOptions.Resource"/> and <see cref="ThunderIDOptions.RefreshBeforeExpiry"/>
    /// apply from the next request, without a restart. A reload that changes
    /// <see cref="ThunderIDOptions.Authority"/> or <see cref="ThunderIDOptions.ClientId"/>, or has an
    /// invalid value, isn't applied at all: the previous settings stay, and a warning with
    /// <see cref="ThunderIDErrorCodes.ReloadImmutableSetting"/> or <see cref="ThunderIDErrorCodes.InvalidSettings"/>
    /// is logged.
    /// </summary>
    public static AuthenticationBuilder AddThunderID(this IServiceCollection services, IConfiguration configuration)
    {
        var initial = ReloadableSettings.Bind(configuration, out var failure);
        if (failure is not null)
        {
            throw new ThunderIDRuntimeException(ThunderIDErrorCodes.InvalidSettings, SettingsChecks.Prefix + failure);
        }

        var settings = new ReloadableSettings(configuration, initial);
        services.TryAddSingleton(sp => new SettingsReloadWatcher(settings, sp.GetRequiredService<ILogger<SettingsReloadWatcher>>()));
        services.AddSingleton<IOptionsChangeTokenSource<OpenIdConnectOptions>>(sp =>
            new SettingsChangeTokenSource<OpenIdConnectOptions>(sp.GetRequiredService<SettingsReloadWatcher>(), OpenIdConnectDefaults.AuthenticationScheme));
        services.AddSingleton<IOptionsChangeTokenSource<TokenRefreshSettings>>(sp =>
            new SettingsChangeTokenSource<TokenRefreshSettings>(sp.GetRequiredService<SettingsReloadWatcher>(), Options.DefaultName));
        return AddThunderID(services, () => settings.Current);
    }

    // The settings are read each time the options are built, so a reload applies once the cache is cleared.
    private static AuthenticationBuilder AddThunderID(IServiceCollection services, Func<ThunderIDOptions> current)
    {
        services.TryAddSingleton<ThunderIDRegistration>();
        // Before AddOpenIdConnect, so these run ahead of the handler's own post-configure: that one
        // would otherwise fail first on a missing or http Authority with a framework error, and it
        // builds the backchannel the token status capture has to wrap.
        services.AddSingleton<IPostConfigureOptions<OpenIdConnectOptions>, OpenIdConnectRequiredSettings>();
        services.AddHttpContextAccessor();
        services.AddSingleton<IPostConfigureOptions<OpenIdConnectOptions>, SignInFailures.CaptureTokenStatus>();

        // Back-channel logout: a replaceable store (in memory by default, kept as long as a cookie
        // can stay valid) and a validator that shares the OIDC handler's discovery and JWKS cache.
        services.TryAddSingleton<ILogoutSessionStore>(sp => new InMemoryLogoutSessionStore(
            sp.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(CookieAuthenticationDefaults.AuthenticationScheme).ExpireTimeSpan));
        services.TryAddSingleton(sp =>
        {
            var oidc = sp.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
            return new LogoutTokenValidator(oidc.ConfigurationManager!, oidc.ClientId!);
        });

        services.Configure<TokenRefreshSettings>(refresh => refresh.RefreshBeforeExpiry = current().RefreshBeforeExpiry);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<TokenRefresher>();

        var builder = services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(options => options.Events.OnValidatePrincipal = ValidateSessionAsync)
            .AddOpenIdConnect(options =>
            {
                var settings = current();
                options.Authority = settings.Authority;
                options.ClientId = settings.ClientId;
                options.ClientSecret = settings.ClientSecret;
                options.ResponseType = "code";
                options.UsePkce = true;
                options.SaveTokens = true;

                // ThunderID accepts only response_mode=query; ASP.NET Core defaults to form_post.
                options.ResponseMode = "query";
                // ThunderID caps nonce at 64 characters.
                options.ProtocolValidator = new ShortNonceProtocolValidator();

                // Keep JWT claim names (sub, email, sid) instead of the WS-Federation URIs.
                options.MapInboundClaims = false;
                options.TokenValidationParameters.NameClaimType = "name";
                options.TokenValidationParameters.RoleClaimType = "roles";
                options.TokenValidationParameters.ValidIssuer = settings.Authority;

                options.Scope.Add("email");
                foreach (var scope in settings.Scopes)
                {
                    options.Scope.Add(scope);
                }

                if (!string.IsNullOrEmpty(settings.Resource))
                {
                    options.Resource = settings.Resource;
                }

                options.Events.OnTokenValidated = context =>
                {
                    if (context.Principal?.Identity is ClaimsIdentity identity)
                    {
                        AddNameFallback(identity);
                    }

                    return Task.CompletedTask;
                };
            });

        services.AddSingleton<IPostConfigureOptions<OpenIdConnectOptions>, IssuerPinnedConfigurationManager.ForOpenIdConnect>();
        services.AddSingleton<IPostConfigureOptions<OpenIdConnectOptions>, SignInFailures.WrapRemoteFailure>();
        services.AddSingleton<IPostConfigureOptions<OpenIdConnectOptions>, OwnedAuthorizationParameters>();
        services.AddSingleton<IValidateOptions<OpenIdConnectOptions>, OpenIdConnectSettingsValidator>();
        services.AddHostedService(sp => new SettingsStartupCheck<OpenIdConnectOptions>(
            sp.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>(), OpenIdConnectDefaults.AuthenticationScheme));
        return builder;
    }

    /// <summary>
    /// Validates ThunderID access tokens for an API, keeping JWT claim names so <c>scope</c>,
    /// <c>sub</c> and <c>client_id</c> read as they appear in the token.
    /// </summary>
    public static AuthenticationBuilder AddThunderIDApi(this IServiceCollection services, Action<ThunderIDApiOptions> configure)
    {
        var settings = new ThunderIDApiOptions();
        configure(settings);

        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, JwtBearerRequiredSettings>();

        var builder = services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = settings.Authority;
                options.Audience = settings.Audience;
                options.MapInboundClaims = false;
                options.TokenValidationParameters.NameClaimType = "sub";
                options.TokenValidationParameters.RoleClaimType = "roles";
                options.TokenValidationParameters.ValidIssuer = settings.Authority;
            });

        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, IssuerPinnedConfigurationManager.ForJwtBearer>();
        services.AddSingleton<IValidateOptions<JwtBearerOptions>, JwtBearerSettingsValidator>();
        services.AddHostedService(sp => new SettingsStartupCheck<JwtBearerOptions>(
            sp.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>(), JwtBearerDefaults.AuthenticationScheme));
        return builder;
    }

    // A session ThunderID ended is refused before anything else, so its tokens are never refreshed.
    private static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        await BackchannelLogoutCookieValidator.ValidatePrincipalAsync(context);
        if (context.Principal is not null)
        {
            await context.HttpContext.RequestServices.GetRequiredService<TokenRefresher>().RefreshIfDueAsync(context);
        }
    }

    // Present only when AddThunderID ran, so an operation used without it can fail with a ThunderID error.
    internal sealed class ThunderIDRegistration;

    internal static void EnsureRegistered(IServiceProvider services, string operation)
    {
        if (services.GetService<ThunderIDRegistration>() is null)
        {
            throw new ThunderIDRuntimeException(ThunderIDErrorCodes.NotRegistered,
                $"{operation} needs AddThunderID. Call builder.Services.AddThunderID(...) before using it.");
        }
    }

    // ThunderID releases only the attributes a user has. Without a "name" claim,
    // User.Identity.Name is null, so build one from given_name and family_name.
    internal static void AddNameFallback(ClaimsIdentity identity)
    {
        if (identity.FindFirst("name") is not null)
        {
            return;
        }

        var parts = new[] { identity.FindFirst("given_name")?.Value, identity.FindFirst("family_name")?.Value }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        var name = string.Join(' ', parts);
        if (name.Length > 0)
        {
            identity.AddClaim(new Claim("name", name));
        }
    }
}
