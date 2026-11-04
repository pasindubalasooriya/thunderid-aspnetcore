using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ThunderID.AspNetCore.BackchannelLogout;

namespace ThunderID.AspNetCore.Tests;

// A discovery document must report exactly the configured authority as its issuer, or nothing in it
// is used. Each attack case pairs a discovery issuer with a token issued under that same issuer, so
// the token would pass if the discovery document were trusted.
public class DiscoveryIssuerTests
{
    private const string Authority = LogoutTokens.Issuer;
    private const string Attacker = "https://evil.example";
    private const string ApiAudience = "https://localhost:7102";

    [Fact]
    public async Task Sign_in_is_refused_when_discovery_reports_another_issuer()
    {
        await using var app = await WebAppAsync(Attacker);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => app.GetTestClient().GetAsync("/protected"));

        Assert.Contains("doesn't match the configured Authority", Messages(ex));
    }

    [Fact]
    public async Task Sign_in_redirects_to_thunderid_when_the_issuer_matches()
    {
        await using var app = await WebAppAsync(Authority);

        var response = await app.GetTestClient().GetAsync("/protected");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(Authority + "/oauth2/authorize", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Logout_token_is_refused_when_discovery_reports_another_issuer()
    {
        await using var app = await WebAppAsync(Attacker);

        var response = await app.GetTestClient().PostAsync("/backchannel-logout", Form(FreshLogoutToken(Attacker)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("discovery document was rejected", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Logout_token_is_accepted_when_the_issuer_matches()
    {
        await using var app = await WebAppAsync(Authority);

        var response = await app.GetTestClient().PostAsync("/backchannel-logout", Form(FreshLogoutToken(Authority)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Api_token_is_refused_when_discovery_reports_another_issuer()
    {
        await using var app = await ApiAsync(Attacker);

        // JWT bearer rethrows configuration failures rather than answering 401, as it does when
        // discovery is unreachable. Either way the token is never accepted.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => CallApiAsync(app, AccessToken(Attacker)));

        Assert.Contains("doesn't match the configured Authority", Messages(ex));
    }

    [Fact]
    public async Task Api_token_is_accepted_when_the_issuer_matches()
    {
        await using var app = await ApiAsync(Authority);

        var response = await CallApiAsync(app, AccessToken(Authority));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Issuer_comparison_is_exact()
    {
        await using var app = await WebAppAsync(Authority + "/");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => app.GetTestClient().GetAsync("/protected"));

        Assert.Contains("doesn't match the configured Authority", Messages(ex));
    }

    private static async Task<WebApplication> WebAppAsync(string discoveryIssuer)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddThunderID(options =>
        {
            options.Authority = Authority;
            options.ClientId = LogoutTokens.ClientId;
            options.ClientSecret = "unused";
        });
        builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme,
            options => options.BackchannelHttpHandler = new StubDiscovery(discoveryIssuer));
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/protected", () => "signed in").RequireAuthorization();
        app.MapThunderIDBackchannelLogout();
        await app.StartAsync();
        return app;
    }

    private static async Task<WebApplication> ApiAsync(string discoveryIssuer)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddThunderIDApi(options =>
        {
            options.Authority = Authority;
            options.Audience = ApiAudience;
        });
        builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
            options => options.BackchannelHttpHandler = new StubDiscovery(discoveryIssuer));
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/me", () => "authorized").RequireAuthorization();
        await app.StartAsync();
        return app;
    }

    private static Task<HttpResponseMessage> CallApiAsync(WebApplication app, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return app.GetTestClient().SendAsync(request);
    }

    private static string FreshLogoutToken(string issuer) => LogoutTokens.Create((_, p) =>
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        p["iss"] = issuer;
        p["iat"] = now;
        p["nbf"] = now;
        p["exp"] = now + 120;
    });

    private static string AccessToken(string issuer)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return LogoutTokens.Sign(
            new JsonObject { ["alg"] = "RS256", ["kid"] = LogoutTokens.KeyId, ["typ"] = "at+jwt" },
            new JsonObject
            {
                ["iss"] = issuer,
                ["aud"] = ApiAudience,
                ["sub"] = "01a182da-b10c-764b-bcc3-4bf3f93282de",
                ["iat"] = now,
                ["nbf"] = now,
                ["exp"] = now + 300,
            },
            LogoutTokens.SigningKey);
    }

    private static FormUrlEncodedContent Form(string logoutToken) => new([new("logout_token", logoutToken)]);

    private static string Messages(Exception ex)
    {
        var messages = new StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            messages.AppendLine(e.Message);
        }

        return messages.ToString();
    }

    // Serves a discovery document with the given issuer, and the test signing key as the JWKS.
    private sealed class StubDiscovery(string issuer) : HttpMessageHandler
    {
        public ConcurrentQueue<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!.AbsolutePath);
            var body = request.RequestUri.AbsolutePath switch
            {
                "/.well-known/openid-configuration" => $$"""
                    {"issuer":"{{issuer}}","authorization_endpoint":"{{Authority}}/oauth2/authorize","token_endpoint":"{{Authority}}/oauth2/token","jwks_uri":"{{Authority}}/oauth2/jwks","end_session_endpoint":"{{Authority}}/oauth2/logout","id_token_signing_alg_values_supported":["RS256"]}
                    """,
                "/oauth2/jwks" => LogoutTokens.Jwks(LogoutTokens.SigningKey, LogoutTokens.KeyId),
                _ => null,
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
