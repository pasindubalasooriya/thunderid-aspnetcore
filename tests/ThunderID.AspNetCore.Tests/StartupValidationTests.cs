using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ThunderID.AspNetCore.Tests;

// Configuration is checked when the app starts, before any request and before any network call.
public class StartupValidationTests
{
    private const string Oidc = OpenIdConnectDefaults.AuthenticationScheme;
    private const string Bearer = JwtBearerDefaults.AuthenticationScheme;

    [Fact]
    public async Task Valid_web_app_configuration_starts_without_a_network_call()
    {
        var outbound = new CountingHandler();
        await using var app = WebApp(LogoutTokens.Issuer, LogoutTokens.ClientId, outbound);

        await app.StartAsync();

        Assert.Empty(outbound.Requests);
    }

    [Theory]
    [InlineData("", "client", "Authority")]
    [InlineData("http://idp.example.com", "client", "Authority")]
    [InlineData("not a url", "client", "Authority")]
    [InlineData("https://localhost:8090", "", "ClientId")]
    public async Task Invalid_web_app_configuration_fails_at_startup_without_a_network_call(string authority, string clientId, string setting)
    {
        var outbound = new CountingHandler();
        await using var app = WebApp(authority, clientId, outbound);

        var ex = await Assert.ThrowsAsync<ThunderIDRuntimeException>(() => app.StartAsync());
        Assert.Equal(ThunderIDErrorCodes.InvalidSettings, ex.Code);

        Assert.Contains(setting, ex.Message);
        Assert.Empty(outbound.Requests);
    }

    [Fact]
    public async Task Http_authority_is_refused_even_when_aspnet_core_allows_it()
    {
        await using var app = WebApp("http://localhost:8090", LogoutTokens.ClientId, new CountingHandler(), services =>
            services.Configure<OpenIdConnectOptions>(Oidc, options => options.RequireHttpsMetadata = false));

        var ex = await Assert.ThrowsAsync<ThunderIDRuntimeException>(() => app.StartAsync());
        Assert.Equal(ThunderIDErrorCodes.InvalidSettings, ex.Code);

        Assert.Equal("The ThunderID settings are invalid: Authority must be an absolute https URL.", ex.Message);
    }

    [Theory]
    [InlineData("UsePkce")]
    [InlineData("ResponseType")]
    [InlineData("RequireNonce")]
    [InlineData("ValidateIssuer")]
    [InlineData("ValidateAudience")]
    [InlineData("ValidateLifetime")]
    [InlineData("RequireSignedTokens")]
    public async Task Web_app_refuses_a_weakened_security_setting(string setting)
    {
        await using var app = WebApp(LogoutTokens.Issuer, LogoutTokens.ClientId, new CountingHandler(), services =>
            services.PostConfigure<OpenIdConnectOptions>(Oidc, options =>
            {
                var tokens = options.TokenValidationParameters;
                switch (setting)
                {
                    case "UsePkce": options.UsePkce = false; break;
                    case "ResponseType": options.ResponseType = "code id_token"; break;
                    case "RequireNonce": options.ProtocolValidator.RequireNonce = false; break;
                    case "ValidateIssuer": tokens.ValidateIssuer = false; break;
                    case "ValidateAudience": tokens.ValidateAudience = false; break;
                    case "ValidateLifetime": tokens.ValidateLifetime = false; break;
                    case "RequireSignedTokens": tokens.RequireSignedTokens = false; break;
                }
            }));

        var ex = await Assert.ThrowsAsync<ThunderIDRuntimeException>(() => app.StartAsync());
        Assert.Equal(ThunderIDErrorCodes.InvalidSettings, ex.Code);

        Assert.Contains(setting, ex.Message);
        Assert.IsType<Microsoft.Extensions.Options.OptionsValidationException>(ex.InnerException);
    }

    [Fact]
    public async Task Every_failed_setting_is_listed_without_its_value()
    {
        await using var app = WebApp("http://idp.example.com/secret-path", "", new CountingHandler());

        var ex = await Assert.ThrowsAsync<ThunderIDRuntimeException>(() => app.StartAsync());

        Assert.Equal("The ThunderID settings are invalid: Authority must be an absolute https URL. ClientId is required.", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task Invalid_settings_do_not_fail_a_host_that_is_built_but_not_started()
    {
        // Design-time tools such as dotnet ef build the host without starting it.
        await using var app = WebApp("", "", new CountingHandler());

        Assert.NotNull(app.Services);
    }

    [Fact]
    public async Task Valid_api_configuration_starts_without_a_network_call()
    {
        var outbound = new CountingHandler();
        await using var app = Api(LogoutTokens.Issuer, "https://localhost:7102", outbound);

        await app.StartAsync();

        Assert.Empty(outbound.Requests);
    }

    [Theory]
    [InlineData("", "https://localhost:7102", "Authority")]
    [InlineData("http://idp.example.com", "https://localhost:7102", "Authority")]
    [InlineData("https://localhost:8090", "", "Audience")]
    public async Task Invalid_api_configuration_fails_at_startup_without_a_network_call(string authority, string audience, string setting)
    {
        var outbound = new CountingHandler();
        await using var app = Api(authority, audience, outbound);

        var ex = await Assert.ThrowsAsync<ThunderIDRuntimeException>(() => app.StartAsync());
        Assert.Equal(ThunderIDErrorCodes.InvalidSettings, ex.Code);

        Assert.Contains(setting, ex.Message);
        Assert.Empty(outbound.Requests);
    }

    [Theory]
    [InlineData("ValidateIssuer")]
    [InlineData("ValidateAudience")]
    [InlineData("ValidateLifetime")]
    [InlineData("RequireSignedTokens")]
    public async Task Api_refuses_a_weakened_security_setting(string setting)
    {
        await using var app = Api(LogoutTokens.Issuer, "https://localhost:7102", new CountingHandler(), services =>
            services.PostConfigure<JwtBearerOptions>(Bearer, options =>
            {
                var tokens = options.TokenValidationParameters;
                switch (setting)
                {
                    case "ValidateIssuer": tokens.ValidateIssuer = false; break;
                    case "ValidateAudience": tokens.ValidateAudience = false; break;
                    case "ValidateLifetime": tokens.ValidateLifetime = false; break;
                    case "RequireSignedTokens": tokens.RequireSignedTokens = false; break;
                }
            }));

        var ex = await Assert.ThrowsAsync<ThunderIDRuntimeException>(() => app.StartAsync());
        Assert.Equal(ThunderIDErrorCodes.InvalidSettings, ex.Code);

        Assert.Contains(setting, ex.Message);
        Assert.IsType<Microsoft.Extensions.Options.OptionsValidationException>(ex.InnerException);
    }

    private static WebApplication WebApp(string authority, string clientId, CountingHandler outbound, Action<IServiceCollection>? extra = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddThunderID(options =>
        {
            options.Authority = authority;
            options.ClientId = clientId;
            options.ClientSecret = "unused";
        });
        builder.Services.Configure<OpenIdConnectOptions>(Oidc, options => options.BackchannelHttpHandler = outbound);
        extra?.Invoke(builder.Services);
        return builder.Build();
    }

    private static WebApplication Api(string authority, string audience, CountingHandler outbound, Action<IServiceCollection>? extra = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddThunderIDApi(options =>
        {
            options.Authority = authority;
            options.Audience = audience;
        });
        builder.Services.Configure<JwtBearerOptions>(Bearer, options => options.BackchannelHttpHandler = outbound);
        extra?.Invoke(builder.Services);
        return builder.Build();
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public ConcurrentQueue<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
