using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ThunderID.AspNetCore.Tests;

public sealed class SignOutTests : IAsyncDisposable
{
    private const string Authority = LogoutTokens.Issuer;
    private const string RefreshToken = "test-refresh-token-5f1c";
    private const string AccessToken = "test-access-token-9b2e";
    private const string IdToken = "test-id-token-3a7d";

    private readonly ConcurrentQueue<string> _events = new();
    private readonly ListLoggerProvider _logs = new();
    private WebApplication? _app;
    private ThunderIDSignOutResult? _result;

    private async Task<HttpClient> StartAsync(StubThunderID thunderID)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(_logs);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);

        builder.Services.AddThunderID(options =>
        {
            options.Authority = Authority;
            options.ClientId = LogoutTokens.ClientId;
            options.ClientSecret = "test-client-secret";
        });
        builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            options.BackchannelHttpHandler = thunderID;
            options.Events.OnRedirectToIdentityProviderForSignOut = context =>
            {
                _events.Enqueue("end-session redirect");
                return Task.CompletedTask;
            };
        });
        builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            options.Events.OnSigningOut = context =>
            {
                _events.Enqueue("cookie cleared");
                return Task.CompletedTask;
            });

        _app = builder.Build();
        _app.UseAuthentication();

        // Test-only: a cookie session holding the tokens the OIDC handler would save.
        _app.MapPost("/test/signin", async (HttpContext context, bool withRefreshToken) =>
        {
            var identity = new ClaimsIdentity([new Claim("sub", "01a182da-b10c-764b-bcc3-4bf3f93282de")], CookieAuthenticationDefaults.AuthenticationScheme);
            var properties = new AuthenticationProperties();
            var tokens = new List<AuthenticationToken> { new() { Name = "access_token", Value = AccessToken }, new() { Name = "id_token", Value = IdToken } };
            if (withRefreshToken)
            {
                tokens.Add(new() { Name = "refresh_token", Value = RefreshToken });
            }

            properties.StoreTokens(tokens);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), properties);
        });
        _app.MapPost("/signout", async (HttpContext context) =>
        {
            _result = await context.SignOutOfThunderIDAsync("/signed-out");
            return Results.Empty;
        });

        await _app.StartAsync();
        return _app.GetTestClient();
    }

    private static async Task<string> SignInAsync(HttpClient client, bool withRefreshToken = true)
    {
        var response = await client.PostAsync($"/test/signin?withRefreshToken={withRefreshToken}", null);
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    private static Task<HttpResponseMessage> SignOutAsync(HttpClient client, string? cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/signout");
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return client.SendAsync(request);
    }

    private static void AssertCookieCleared(HttpResponseMessage response) =>
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Cookies=;") && c.Contains("expires=Thu, 01 Jan 1970"));

    private void AssertNoTokenLogged()
    {
        var logs = string.Join('\n', _logs.Messages);
        Assert.DoesNotContain(RefreshToken, logs);
        Assert.DoesNotContain(AccessToken, logs);
    }

    [Fact]
    public async Task Revokes_the_refresh_token_before_clearing_the_cookie_then_redirects_to_end_session()
    {
        var thunderID = new StubThunderID(this);
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client);

        var response = await SignOutAsync(client, cookie);

        Assert.Equal(["revocation", "cookie cleared", "end-session redirect"], _events);
        var revocation = Assert.Single(thunderID.Revocations);
        Assert.Equal(RefreshToken, revocation["token"]);
        Assert.Equal("refresh_token", revocation["token_type_hint"]);
        Assert.Equal(LogoutTokens.ClientId, revocation["client_id"]);
        Assert.Equal("test-client-secret", revocation["client_secret"]);
        Assert.False(_result!.RevocationFailed);
        Assert.Null(_result.RevocationError);
        AssertCookieCleared(response);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(Authority + "/oauth2/logout?", response.Headers.Location!.ToString());
        Assert.Equal(IdToken, QueryHelpers.ParseQuery(response.Headers.Location.Query)["id_token_hint"]);
    }

    [Fact]
    public async Task Revokes_the_access_token_when_the_session_has_no_refresh_token()
    {
        var thunderID = new StubThunderID(this);
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client, withRefreshToken: false);

        await SignOutAsync(client, cookie);

        var revocation = Assert.Single(thunderID.Revocations);
        Assert.Equal(AccessToken, revocation["token"]);
        Assert.Equal("access_token", revocation["token_type_hint"]);
        Assert.False(_result!.RevocationFailed);
    }

    [Fact]
    public async Task Unreachable_revocation_still_clears_the_cookie_and_reports_the_failure()
    {
        var client = await StartAsync(new StubThunderID(this) { RevocationFailure = new HttpRequestException("Connection refused") });
        var cookie = await SignInAsync(client);

        var response = await SignOutAsync(client, cookie);

        Assert.True(_result!.RevocationFailed);
        var error = Assert.IsType<ThunderIDRuntimeException>(_result.RevocationError);
        Assert.Equal(ThunderIDErrorCodes.RevocationUnreachable, error.Code);
        Assert.Equal("ThunderID's revocation endpoint could not be reached.", error.Message);
        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Equal(["revocation", "cookie cleared", "end-session redirect"], _events);
        AssertCookieCleared(response);
        Assert.StartsWith(Authority + "/oauth2/logout?", response.Headers.Location!.ToString());
        Assert.Contains(_logs.Messages, m => m.StartsWith("Sign-out could not revoke the refresh_token at ThunderID"));
        AssertNoTokenLogged();
    }

    [Fact]
    public async Task Revocation_refused_by_thunderid_is_reported()
    {
        var client = await StartAsync(new StubThunderID(this) { RevocationStatus = HttpStatusCode.Unauthorized });
        var cookie = await SignInAsync(client);

        var response = await SignOutAsync(client, cookie);

        Assert.True(_result!.RevocationFailed);
        var error = Assert.IsType<ThunderIDApiException>(_result.RevocationError);
        Assert.Equal(ThunderIDErrorCodes.RevocationRejected, error.Code);
        Assert.Equal((401, "Unauthorized"), (error.StatusCode, error.StatusText));
        Assert.Equal("ThunderID's revocation endpoint refused to revoke the refresh_token.", error.Message);
        AssertCookieCleared(response);
    }

    [Fact]
    public async Task Revocation_that_times_out_is_reported()
    {
        var client = await StartAsync(new StubThunderID(this) { RevocationHangs = true });
        var cookie = await SignInAsync(client);

        var response = await SignOutAsync(client, cookie);

        Assert.True(_result!.RevocationFailed);
        var error = Assert.IsType<ThunderIDRuntimeException>(_result.RevocationError);
        Assert.Equal(ThunderIDErrorCodes.RevocationTimedOut, error.Code);
        Assert.Equal("ThunderID's revocation endpoint did not answer within 10 seconds.", error.Message);
        AssertCookieCleared(response);
    }

    [Fact]
    public async Task Missing_revocation_endpoint_is_reported()
    {
        var client = await StartAsync(new StubThunderID(this) { AdvertiseRevocation = false });
        var cookie = await SignInAsync(client);

        var response = await SignOutAsync(client, cookie);

        var error = Assert.IsType<ThunderIDRuntimeException>(_result!.RevocationError);
        Assert.Equal(ThunderIDErrorCodes.RevocationEndpointMissing, error.Code);
        AssertCookieCleared(response);
    }

    [Fact]
    public async Task Token_is_not_sent_when_discovery_reports_another_issuer()
    {
        var thunderID = new StubThunderID(this) { Issuer = "https://evil.example" };
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client);

        var response = await SignOutAsync(client, cookie);

        Assert.Empty(thunderID.Revocations);
        var error = Assert.IsType<ThunderIDRuntimeException>(_result!.RevocationError);
        Assert.Equal(ThunderIDErrorCodes.DiscoveryIssuerMismatch, error.Code);
        Assert.Contains("doesn't match the configured Authority", error.Message);
        AssertCookieCleared(response);
        Assert.Equal("/signed-out", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Unreachable_discovery_still_clears_the_cookie_and_redirects_locally()
    {
        var client = await StartAsync(new StubThunderID(this) { DiscoveryFailure = new HttpRequestException("Connection refused") });
        var cookie = await SignInAsync(client);

        var response = await SignOutAsync(client, cookie);

        Assert.True(_result!.RevocationFailed);
        var error = Assert.IsType<ThunderIDRuntimeException>(_result.RevocationError);
        Assert.Equal(ThunderIDErrorCodes.DiscoveryUnavailable, error.Code);
        Assert.NotNull(error.InnerException);
        AssertCookieCleared(response);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/signed-out", response.Headers.Location!.ToString());
        AssertNoTokenLogged();
    }

    [Fact]
    public async Task Discovery_answered_with_an_error_is_reported_with_its_status()
    {
        var client = await StartAsync(new StubThunderID(this) { DiscoveryStatus = HttpStatusCode.ServiceUnavailable });
        var cookie = await SignInAsync(client);

        var response = await SignOutAsync(client, cookie);

        var error = Assert.IsType<ThunderIDApiException>(_result!.RevocationError);
        Assert.Equal(ThunderIDErrorCodes.DiscoveryRejected, error.Code);
        Assert.Equal((503, "Service Unavailable"), (error.StatusCode, error.StatusText));
        AssertCookieCleared(response);
        Assert.Equal("/signed-out", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Without_a_session_sign_out_succeeds_silently_without_contacting_thunderid()
    {
        var thunderID = new StubThunderID(this);
        var client = await StartAsync(thunderID);

        var response = await SignOutAsync(client, cookie: null);

        Assert.Empty(thunderID.Requests);
        Assert.False(_result!.RevocationFailed);
        Assert.DoesNotContain("end-session redirect", _events);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/signed-out", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Successful_revocation_logs_without_the_token()
    {
        var client = await StartAsync(new StubThunderID(this));
        var cookie = await SignInAsync(client);

        await SignOutAsync(client, cookie);

        Assert.Contains("Sign-out revoked the refresh_token at ThunderID", _logs.Messages);
        AssertNoTokenLogged();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    // Serves discovery and the revocation endpoint, recording each revocation request.
    private sealed class StubThunderID(SignOutTests test) : HttpMessageHandler
    {
        public string Issuer { get; init; } = Authority;
        public bool AdvertiseRevocation { get; init; } = true;
        public HttpStatusCode RevocationStatus { get; init; } = HttpStatusCode.OK;
        public Exception? RevocationFailure { get; init; }
        public bool RevocationHangs { get; init; }
        public Exception? DiscoveryFailure { get; init; }
        public HttpStatusCode DiscoveryStatus { get; init; } = HttpStatusCode.OK;
        public ConcurrentQueue<string> Requests { get; } = new();
        public ConcurrentQueue<Dictionary<string, string>> Revocations { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!.AbsolutePath);
            if (DiscoveryFailure is not null)
            {
                throw DiscoveryFailure;
            }

            switch (request.RequestUri.AbsolutePath)
            {
                case "/.well-known/openid-configuration" when DiscoveryStatus != HttpStatusCode.OK:
                    return new HttpResponseMessage(DiscoveryStatus);
                case "/.well-known/openid-configuration":
                    var revocation = AdvertiseRevocation ? $"\"revocation_endpoint\":\"{Authority}/oauth2/revoke\"," : "";
                    return Json($$"""
                        {"issuer":"{{Issuer}}",{{revocation}}"authorization_endpoint":"{{Authority}}/oauth2/authorize","token_endpoint":"{{Authority}}/oauth2/token","jwks_uri":"{{Authority}}/oauth2/jwks","end_session_endpoint":"{{Authority}}/oauth2/logout"}
                        """);
                case "/oauth2/jwks":
                    return Json(LogoutTokens.Jwks(LogoutTokens.SigningKey, LogoutTokens.KeyId));
                case "/oauth2/revoke":
                    test._events.Enqueue("revocation");
                    var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
                    Revocations.Enqueue(form.ToDictionary(f => f.Key, f => f.Value.ToString()));
                    if (RevocationHangs)
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }

                    if (RevocationFailure is not null)
                    {
                        throw RevocationFailure;
                    }

                    return new HttpResponseMessage(RevocationStatus);
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class ListLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new ListLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class ListLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception) + exception);
        }
    }
}
