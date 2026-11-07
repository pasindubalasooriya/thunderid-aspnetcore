using System.Net;
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
        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = await inner.GetConfigurationAsync(cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The document retriever records ThunderID's HTTP status when it answered with an error.
            const string message = "ThunderID's discovery document or signing keys could not be fetched.";
            throw StatusCode(ex) is { } status
                ? new ThunderIDApiException(ThunderIDErrorCodes.DiscoveryRejected, message, (int)status, ReasonPhrase(status), ex)
                : new ThunderIDRuntimeException(ThunderIDErrorCodes.DiscoveryUnavailable, message, ex);
        }

        if (!string.Equals(configuration.Issuer, authority, StringComparison.Ordinal))
        {
            throw new ThunderIDRuntimeException(ThunderIDErrorCodes.DiscoveryIssuerMismatch,
                $"The discovery document's issuer '{configuration.Issuer}' doesn't match the configured Authority '{authority}'.");
        }

        return configuration;
    }

    private static HttpStatusCode? StatusCode(Exception exception)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            if (e.Data[HttpDocumentRetriever.StatusCode] is HttpStatusCode status)
            {
                return status;
            }
        }

        return null;
    }

    private static string? ReasonPhrase(HttpStatusCode status) => new HttpResponseMessage(status).ReasonPhrase;

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
