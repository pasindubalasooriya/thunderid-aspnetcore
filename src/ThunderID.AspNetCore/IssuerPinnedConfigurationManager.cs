using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace ThunderID.AspNetCore;

/// <summary>
/// Refuses a discovery document whose <c>issuer</c> isn't exactly the configured authority, so none
/// of its endpoints or keys are used. ASP.NET Core otherwise trusts whatever issuer discovery reports.
/// </summary>
internal sealed class IssuerPinnedConfigurationManager(IConfigurationManager<OpenIdConnectConfiguration> inner, string authority)
    : IConfigurationManager<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        var configuration = await inner.GetConfigurationAsync(cancel);
        if (!string.Equals(configuration.Issuer, authority, StringComparison.Ordinal))
        {
            throw new DiscoveryIssuerMismatchException(configuration.Issuer, authority);
        }

        return configuration;
    }

    public void RequestRefresh() => inner.RequestRefresh();

    private static void Pin(Func<IConfigurationManager<OpenIdConnectConfiguration>?> get, Action<IConfigurationManager<OpenIdConnectConfiguration>> set, string? authority)
    {
        if (get() is { } manager and not IssuerPinnedConfigurationManager && !string.IsNullOrEmpty(authority))
        {
            set(new IssuerPinnedConfigurationManager(manager, authority));
        }
    }

    // Runs after ASP.NET Core's own post-configure, which is what creates the configuration manager.
    internal sealed class ForOpenIdConnect : IPostConfigureOptions<OpenIdConnectOptions>
    {
        public void PostConfigure(string? name, OpenIdConnectOptions options)
        {
            if (name == OpenIdConnectDefaults.AuthenticationScheme)
            {
                Pin(() => options.ConfigurationManager, m => options.ConfigurationManager = m, options.Authority);
            }
        }
    }

    internal sealed class ForJwtBearer : IPostConfigureOptions<JwtBearerOptions>
    {
        public void PostConfigure(string? name, JwtBearerOptions options)
        {
            if (name == JwtBearerDefaults.AuthenticationScheme)
            {
                Pin(() => options.ConfigurationManager, m => options.ConfigurationManager = m, options.Authority);
            }
        }
    }
}

/// <summary>The discovery document named an issuer other than the configured authority.</summary>
internal sealed class DiscoveryIssuerMismatchException(string? issuer, string authority)
    : InvalidOperationException($"The discovery document's issuer '{issuer}' doesn't match the configured Authority '{authority}'.");
