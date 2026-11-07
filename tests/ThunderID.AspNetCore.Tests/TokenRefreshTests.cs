using System.Collections.Concurrent;
using System.Globalization;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ThunderID.AspNetCore.BackchannelLogout;

namespace ThunderID.AspNetCore.Tests;

// S12 and AC4.7: tokens are refreshed ahead of expiry, once per refresh token, and a refused refresh ends the session.
public sealed class TokenRefreshTests : IAsyncDisposable
{
    private const string Authority = LogoutTokens.Issuer;
    private const string AccessToken = "test-access-token-a0-7c1d";
    private const string RefreshToken = "test-refresh-token-r0-4e8b";
    private const string IdToken = "test-id-token-i0-2f6a";
    private const string SignedOut = "signed out";

    private readonly ListLoggerProvider _logs = new();
    private readonly FakeTime _time = new();
    private WebApplication? _app;

    private async Task<HttpClient> StartAsync(StubThunderID thunderID, TimeSpan? refreshBeforeExpiry = null, ILogoutSessionStore? store = null, bool useAuthentication = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(_logs);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);

        builder.Services.AddSingleton<TimeProvider>(_time);
        if (store is not null)
        {
            builder.Services.AddSingleton(store);
        }

        builder.Services.AddThunderID(options =>
        {
            options.Authority = Authority;
            options.ClientId = LogoutTokens.ClientId;
            options.ClientSecret = "test-client-secret";
            if (refreshBeforeExpiry is { } margin)
            {
                options.RefreshBeforeExpiry = margin;
            }
        });
        builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options => options.BackchannelHttpHandler = thunderID);

        _app = builder.Build();
        if (useAuthentication)
        {
            _app.UseAuthentication();
        }

        // Test-only: a cookie session holding the tokens the OIDC handler would save.
        _app.MapPost("/test/signin", async (HttpContext context, int expiresIn, bool withRefreshToken) =>
        {
            var identity = new ClaimsIdentity([new Claim("sub", "u-1"), new Claim("sid", "s-1")], CookieAuthenticationDefaults.AuthenticationScheme);
            var properties = new AuthenticationProperties();
            var tokens = new List<AuthenticationToken>
            {
                new() { Name = "access_token", Value = AccessToken },
                new() { Name = "id_token", Value = IdToken },
                new() { Name = "token_type", Value = "Bearer" },
                new() { Name = "expires_at", Value = _time.GetUtcNow().AddSeconds(expiresIn).ToString("o", CultureInfo.InvariantCulture) },
            };
            if (withRefreshToken)
            {
                tokens.Add(new() { Name = "refresh_token", Value = RefreshToken });
            }

            properties.StoreTokens(tokens);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), properties);
        });
        _app.MapGet("/test/tokens", async (HttpContext context) =>
        {
            var session = await context.AuthenticateAsync();
            return session.Succeeded
                ? string.Join('|', new[] { "access_token", "refresh_token", "id_token", "token_type" }.Select(session.Properties!.GetTokenValue))
                    + "|" + session.Properties!.GetTokenValue("expires_at")
                : SignedOut;
        });

        _app.MapGet("/test/access-token", async (HttpContext context) => await context.GetThunderIDAccessTokenAsync() ?? SignedOut);

        await _app.StartAsync();
        return _app.GetTestClient();
    }

    private static async Task<string> SignInAsync(HttpClient client, int expiresIn = 30, bool withRefreshToken = true)
    {
        var response = await client.PostAsync($"/test/signin?expiresIn={expiresIn}&withRefreshToken={withRefreshToken}", null);
        response.EnsureSuccessStatusCode();
        return CookieOf(response)!;
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/test/tokens");
        request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> GetAccessTokenAsync(HttpClient client, string? cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/test/access-token");
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return client.SendAsync(request);
    }

    private static string? CookieOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.Select(v => v.Split(';')[0]).SingleOrDefault(v => v.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal)) : null;

    private static void AssertCookieCleared(HttpResponseMessage response) =>
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Cookies=;") && c.Contains("expires=Thu, 01 Jan 1970"));

    private static string[] Tokens(string body) => body.Split('|');

    private void AssertNoTokenLogged(params string[] more)
    {
        var logs = string.Join('\n', _logs.Messages);
        foreach (var token in new[] { AccessToken, RefreshToken, IdToken, StubThunderID.NewAccessToken, StubThunderID.NewRefreshToken, StubThunderID.NewIdToken }.Concat(more))
        {
            Assert.DoesNotContain(token, logs);
        }
    }

    [Fact]
    public async Task Token_near_expiry_is_refreshed_once_and_the_cookie_carries_the_new_tokens()
    {
        var thunderID = new StubThunderID();
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client, expiresIn: 30);

        var response = await GetAsync(client, cookie);

        var tokens = Tokens(await response.Content.ReadAsStringAsync());
        Assert.Equal([StubThunderID.NewAccessToken, StubThunderID.NewRefreshToken, StubThunderID.NewIdToken, "Bearer"], tokens[..4]);
        Assert.Equal(_time.GetUtcNow().AddSeconds(3600), DateTimeOffset.Parse(tokens[4], CultureInfo.InvariantCulture));
        var form = Assert.Single(thunderID.TokenRequests);
        Assert.Equal(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = RefreshToken,
            ["client_id"] = LogoutTokens.ClientId,
            ["client_secret"] = "test-client-secret",
        }, form);

        var renewed = CookieOf(response);
        Assert.NotNull(renewed);
        var next = await GetAsync(client, renewed);
        Assert.Equal(StubThunderID.NewAccessToken, Tokens(await next.Content.ReadAsStringAsync())[0]);
        Assert.Null(CookieOf(next));
        Assert.Single(thunderID.TokenRequests);
        Assert.Contains("Refreshed the session's tokens at ThunderID", _logs.Messages);
        AssertNoTokenLogged();
    }

    [Fact]
    public async Task Token_not_near_expiry_is_not_refreshed()
    {
        var thunderID = new StubThunderID();
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client, expiresIn: 3600);

        var response = await GetAsync(client, cookie);

        Assert.Equal(AccessToken, Tokens(await response.Content.ReadAsStringAsync())[0]);
        Assert.Null(CookieOf(response));
        Assert.Empty(thunderID.TokenRequests);
    }

    [Theory]
    [InlineData(61, false)]
    [InlineData(59, true)]
    [InlineData(-5, true)]
    public async Task Refresh_is_due_within_the_default_60_seconds_of_expiry(int expiresIn, bool refreshed)
    {
        var thunderID = new StubThunderID();
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client, expiresIn);

        await GetAsync(client, cookie);

        Assert.Equal(refreshed ? 1 : 0, thunderID.TokenRequests.Count);
    }

    [Fact]
    public async Task RefreshBeforeExpiry_sets_when_refresh_is_due()
    {
        var thunderID = new StubThunderID();
        var client = await StartAsync(thunderID, refreshBeforeExpiry: TimeSpan.FromHours(2));
        var cookie = await SignInAsync(client, expiresIn: 3600);

        var response = await GetAsync(client, cookie);

        Assert.Equal(StubThunderID.NewAccessToken, Tokens(await response.Content.ReadAsStringAsync())[0]);
        Assert.Single(thunderID.TokenRequests);
    }

    [Fact]
    public async Task Negative_RefreshBeforeExpiry_fails_startup_with_the_settings_code()
    {
        var error = await Assert.ThrowsAsync<ThunderIDRuntimeException>(() => StartAsync(new StubThunderID(), refreshBeforeExpiry: TimeSpan.FromSeconds(-1)));

        Assert.Equal(ThunderIDErrorCodes.InvalidSettings, error.Code);
        Assert.Equal("The ThunderID settings are invalid: RefreshBeforeExpiry can't be negative.", error.Message);
    }

    [Fact]
    public async Task Session_without_a_refresh_token_is_kept_without_a_refresh()
    {
        var thunderID = new StubThunderID();
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client, expiresIn: -5, withRefreshToken: false);

        var response = await GetAsync(client, cookie);

        Assert.Equal(AccessToken, Tokens(await response.Content.ReadAsStringAsync())[0]);
        Assert.Empty(thunderID.TokenRequests);
    }

    [Fact]
    public async Task Concurrent_requests_share_one_refresh()
    {
        var thunderID = new StubThunderID { Delay = TimeSpan.FromMilliseconds(300) };
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client);

        var responses = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => GetAsync(client, cookie)));

        Assert.Single(thunderID.TokenRequests);
        foreach (var response in responses)
        {
            Assert.Equal(StubThunderID.NewAccessToken, Tokens(await response.Content.ReadAsStringAsync())[0]);
            Assert.NotNull(CookieOf(response));
        }
    }

    [Fact]
    public async Task Old_cookie_sent_after_the_refresh_gets_the_new_tokens_without_sending_the_refresh_token_again()
    {
        var thunderID = new StubThunderID();
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client);
        await GetAsync(client, cookie);
        _time.Advance(TimeSpan.FromSeconds(59));

        var late = await GetAsync(client, cookie);

        Assert.Equal(StubThunderID.NewAccessToken, Tokens(await late.Content.ReadAsStringAsync())[0]);
        Assert.NotNull(CookieOf(late));
        Assert.Single(thunderID.TokenRequests);
    }

    [Fact]
    public async Task Kept_result_expires_after_60_seconds()
    {
        var thunderID = new StubThunderID();
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client);
        await GetAsync(client, cookie);
        _time.Advance(TimeSpan.FromSeconds(61));

        await GetAsync(client, cookie);

        Assert.Equal(2, thunderID.TokenRequests.Count);
        Assert.Equal(1, _app!.Services.GetRequiredService<TokenRefresher>().AttemptCount);
    }

    [Fact]
    public async Task Refused_refresh_ends_the_session_and_logs_the_code_without_the_description()
    {
        var thunderID = new StubThunderID
        {
            Status = HttpStatusCode.BadRequest,
            Body = """{"error":"invalid_grant","error_description":"secret-description-81c2"}""",
        };
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client);

        var response = await GetAsync(client, cookie);

        Assert.Equal(SignedOut, await response.Content.ReadAsStringAsync());
        AssertCookieCleared(response);
        var warning = Assert.Single(_logs.Messages, m => m.Contains(ThunderIDErrorCodes.RefreshRejected));
        Assert.Equal("Refreshing the session's tokens failed (JS-AUTH_CORE-RAT2-HE04): ThunderID's token endpoint refused the refresh token (invalid_grant). The local session ends.", warning);
        AssertNoTokenLogged("secret-description-81c2");

        // A late request with the same cookie is refused too, without sending the refresh token again.
        var late = await GetAsync(client, cookie);
        Assert.Equal(SignedOut, await late.Content.ReadAsStringAsync());
        Assert.Single(thunderID.TokenRequests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Any_4xx_answer_ends_the_session(HttpStatusCode status)
    {
        var client = await StartAsync(new StubThunderID { Status = status, Body = "" });
        var cookie = await SignInAsync(client);

        var response = await GetAsync(client, cookie);

        Assert.Equal(SignedOut, await response.Content.ReadAsStringAsync());
        Assert.Contains(_logs.Messages, m => m.StartsWith("Refreshing the session's tokens failed (JS-AUTH_CORE-RAT2-HE04): ThunderID's token endpoint refused the refresh token."));
    }

    public static TheoryData<string, string, Action<StubThunderID>> KeptFailures => new()
    {
        { ThunderIDErrorCodes.RefreshServerError, "ThunderID's token endpoint answered the refresh with an error (temporarily_unavailable).", t => { t.Status = HttpStatusCode.ServiceUnavailable; t.Body = """{"error":"temporarily_unavailable"}"""; } },
        { ThunderIDErrorCodes.RefreshUnreachable, "ThunderID's token endpoint could not be reached to refresh the tokens.", t => t.Failure = new HttpRequestException("connection refused") },
        { ThunderIDErrorCodes.RefreshResponseInvalid, "ThunderID's token endpoint answered the refresh without an access token.", t => t.Body = """{"token_type":"Bearer"}""" },
        { ThunderIDErrorCodes.RefreshResponseInvalid, "ThunderID's token endpoint answered the refresh without an access token.", t => t.Body = "not json" },
        { ThunderIDErrorCodes.RefreshEndpointMissing, "ThunderID's discovery document has no token_endpoint.", t => t.AdvertiseTokenEndpoint = false },
    };

    [Theory]
    [MemberData(nameof(KeptFailures))]
    public async Task Other_failures_keep_the_session_and_the_next_request_tries_again(string code, string reason, Action<StubThunderID> fail)
    {
        var thunderID = new StubThunderID();
        fail(thunderID);
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client);

        var response = await GetAsync(client, cookie);

        Assert.Equal(AccessToken, Tokens(await response.Content.ReadAsStringAsync())[0]);
        Assert.Null(CookieOf(response));
        Assert.Contains($"Refreshing the session's tokens failed ({code}): {reason} The session is kept, and the next request tries again.", _logs.Messages);

        await GetAsync(client, cookie);
        Assert.Equal(thunderID.AdvertiseTokenEndpoint ? 2 : 0, thunderID.TokenRequests.Count);
        Assert.Equal(2, _logs.Messages.Count(m => m.Contains(code)));
        AssertNoTokenLogged();
    }

    [Fact]
    public async Task Refresh_that_gets_no_answer_times_out_after_10_seconds_and_keeps_the_session()
    {
        var thunderID = new StubThunderID { Hangs = true };
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client);

        var response = await GetAsync(client, cookie);

        Assert.Equal(AccessToken, Tokens(await response.Content.ReadAsStringAsync())[0]);
        Assert.Contains("Refreshing the session's tokens failed (ASPNETCORE-REFRESH-RAT-NE01): ThunderID's token endpoint did not answer the refresh within 10 seconds. The session is kept, and the next request tries again.", _logs.Messages);
    }

    [Fact]
    public async Task Refresh_token_is_not_sent_when_discovery_reports_another_issuer()
    {
        var thunderID = new StubThunderID { Issuer = "https://evil.example" };
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client);

        var response = await GetAsync(client, cookie);

        Assert.Equal(AccessToken, Tokens(await response.Content.ReadAsStringAsync())[0]);
        Assert.Empty(thunderID.TokenRequests);
        Assert.Contains(_logs.Messages, m => m.StartsWith($"Refreshing the session's tokens failed ({ThunderIDErrorCodes.DiscoveryIssuerMismatch}):"));
    }

    [Fact]
    public async Task Response_without_a_new_refresh_token_keeps_the_old_one()
    {
        var thunderID = new StubThunderID { Body = $$"""{"access_token":"{{StubThunderID.NewAccessToken}}","token_type":"Bearer","expires_in":600}""" };
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client);

        var tokens = Tokens(await (await GetAsync(client, cookie)).Content.ReadAsStringAsync());

        Assert.Equal([StubThunderID.NewAccessToken, RefreshToken, IdToken, "Bearer"], tokens[..4]);
        Assert.Equal(_time.GetUtcNow().AddSeconds(600), DateTimeOffset.Parse(tokens[4], CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Session_ended_by_backchannel_logout_is_refused_without_a_refresh()
    {
        var thunderID = new StubThunderID();
        var client = await StartAsync(thunderID, store: new EndedStore());
        var cookie = await SignInAsync(client);

        var response = await GetAsync(client, cookie);

        Assert.Equal(SignedOut, await response.Content.ReadAsStringAsync());
        Assert.Empty(thunderID.TokenRequests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetThunderIDAccessTokenAsync_returns_the_refreshed_token_and_the_cookie_is_rewritten(bool useAuthentication)
    {
        var thunderID = new StubThunderID();
        var client = await StartAsync(thunderID, useAuthentication: useAuthentication);
        var cookie = await SignInAsync(client);

        var response = await GetAccessTokenAsync(client, cookie);

        Assert.Equal(StubThunderID.NewAccessToken, await response.Content.ReadAsStringAsync());
        var renewed = CookieOf(response);
        Assert.NotNull(renewed);
        Assert.Equal(StubThunderID.NewAccessToken, await (await GetAccessTokenAsync(client, renewed)).Content.ReadAsStringAsync());
        Assert.Single(thunderID.TokenRequests);
    }

    [Fact]
    public async Task GetThunderIDAccessTokenAsync_returns_the_current_token_when_no_refresh_is_due()
    {
        var thunderID = new StubThunderID();
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client, expiresIn: 3600);

        Assert.Equal(AccessToken, await (await GetAccessTokenAsync(client, cookie)).Content.ReadAsStringAsync());
        Assert.Empty(thunderID.TokenRequests);
    }

    [Fact]
    public async Task GetThunderIDAccessTokenAsync_returns_null_without_a_session_or_after_a_refused_refresh()
    {
        var client = await StartAsync(new StubThunderID { Status = HttpStatusCode.BadRequest, Body = """{"error":"invalid_grant"}""" });
        var cookie = await SignInAsync(client);

        Assert.Equal(SignedOut, await (await GetAccessTokenAsync(client, null)).Content.ReadAsStringAsync());
        Assert.Equal(SignedOut, await (await GetAccessTokenAsync(client, cookie)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetThunderIDAccessTokenAsync_returns_the_current_token_when_ThunderID_cannot_be_reached()
    {
        var client = await StartAsync(new StubThunderID { Failure = new HttpRequestException("connection refused") });
        var cookie = await SignInAsync(client);

        Assert.Equal(AccessToken, await (await GetAccessTokenAsync(client, cookie)).Content.ReadAsStringAsync());
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class EndedStore : ILogoutSessionStore
    {
        public Task<bool> TryRecordLogoutAsync(LogoutToken token, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> IsSessionEndedAsync(string? sessionId, string? subject, DateTimeOffset? authTime, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    public sealed class StubThunderID : HttpMessageHandler
    {
        public const string NewAccessToken = "test-access-token-a1-9d3f";
        public const string NewRefreshToken = "test-refresh-token-r1-6b0e";
        public const string NewIdToken = "test-id-token-i1-1c5a";

        public string Issuer { get; init; } = Authority;

        public bool AdvertiseTokenEndpoint { get; set; } = true;

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public string Body { get; set; } = $$"""{"access_token":"{{NewAccessToken}}","refresh_token":"{{NewRefreshToken}}","id_token":"{{NewIdToken}}","token_type":"Bearer","expires_in":3600,"scope":"openid profile email"}""";

        public TimeSpan Delay { get; init; }

        public bool Hangs { get; init; }

        public Exception? Failure { get; set; }

        public ConcurrentQueue<Dictionary<string, string>> TokenRequests { get; } = new();

        /// <summary>Every path requested, discovery included.</summary>
        public ConcurrentQueue<string> Paths { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Enqueue(request.RequestUri!.AbsolutePath);
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/.well-known/openid-configuration":
                    var tokenEndpoint = AdvertiseTokenEndpoint ? $"""
                        "token_endpoint":"{Authority}/oauth2/token",
                        """ : "";
                    return Json($$"""{"issuer":"{{Issuer}}","authorization_endpoint":"{{Authority}}/oauth2/authorize",{{tokenEndpoint}}"jwks_uri":"{{Authority}}/oauth2/jwks"}""");
                case "/oauth2/jwks":
                    return Json(LogoutTokens.Jwks(LogoutTokens.SigningKey, LogoutTokens.KeyId));
                case "/oauth2/token":
                    var form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&')
                        .Select(p => p.Split('=', 2)).ToDictionary(p => Decode(p[0]), p => Decode(p[1]));
                    TokenRequests.Enqueue(form);
                    if (Hangs)
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }

                    await Task.Delay(Delay, cancellationToken);
                    if (Failure is not null)
                    {
                        throw Failure;
                    }

                    return new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        // application/x-www-form-urlencoded writes a space as '+'.
        private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
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
                messages.Enqueue(formatter(state, exception));
        }
    }
}
