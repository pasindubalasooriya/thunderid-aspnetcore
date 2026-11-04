using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using ThunderID.AspNetCore.BackchannelLogout;

namespace ThunderID.AspNetCore.Tests;

public sealed class BackchannelLogoutEndpointTests : IAsyncDisposable
{
    private readonly ListLoggerProvider _logs = new();
    private WebApplication? _app;

    private async Task<(HttpClient Client, ILogoutSessionStore Store)> StartAsync(ILogoutSessionStore? store = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(_logs);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);

        // The real wiring...
        builder.Services.AddThunderID(options =>
        {
            options.Authority = LogoutTokens.Issuer;
            options.ClientId = LogoutTokens.ClientId;
            options.ClientSecret = "unused";
        });

        // ...with a validator that uses the test key and a pinned clock, and no network.
        builder.Services.AddSingleton(new LogoutTokenValidator(
            new StaticConfigurationManager<OpenIdConnectConfiguration>(LogoutTokens.Configuration()),
            LogoutTokens.ClientId,
            timeProvider: new ManualTimeProvider(LogoutTokens.Now)));
        store ??= new InMemoryLogoutSessionStore(TimeSpan.FromDays(14), new ManualTimeProvider(LogoutTokens.Now));
        builder.Services.AddSingleton(store);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.MapThunderIDBackchannelLogout();

        // Test-only endpoints: create a cookie session like the OIDC handler would, and report who is signed in.
        _app.MapPost("/test/signin", async (HttpContext context, string sid, string sub, long authTime) =>
        {
            var identity = new ClaimsIdentity(
                [new Claim("sid", sid), new Claim("sub", sub), new Claim("auth_time", authTime.ToString())],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        });
        _app.MapGet("/test/whoami", (HttpContext context) =>
            context.User.Identity?.IsAuthenticated == true ? context.User.FindFirst("sid")!.Value : "anonymous");

        await _app.StartAsync();
        return (_app.GetTestClient(), store);
    }

    private static FormUrlEncodedContent Form(string token) => new([new("logout_token", token)]);

    private static async Task<string> SignInAsync(HttpClient client, string sid, long authTime, string sub = "01a182da-b10c-764b-bcc3-4bf3f93282de")
    {
        var response = await client.PostAsync($"/test/signin?sid={sid}&sub={sub}&authTime={authTime}", null);
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    private static async Task<string> WhoAmIAsync(HttpClient client, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/test/whoami");
        request.Headers.Add("Cookie", cookie);
        return await (await client.SendAsync(request)).Content.ReadAsStringAsync();
    }

    private static void AssertNoStore(HttpResponseMessage response) =>
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

    // Valid requests

    [Fact]
    public async Task Valid_token_returns_200_with_no_store_and_records_the_logout()
    {
        var (client, store) = await StartAsync();

        var response = await client.PostAsync("/backchannel-logout", Form(LogoutTokens.Create()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoStore(response);
        Assert.Null(response.Headers.Location);
        Assert.True(await store.IsSessionEndedAsync("01a18372-2364-7a2a-afe3-c0626b927850", null, null));
    }

    [Fact]
    public async Task Valid_token_for_a_session_the_app_does_not_hold_returns_200()
    {
        var (client, _) = await StartAsync();

        var response = await client.PostAsync("/backchannel-logout", Form(LogoutTokens.Create((_, p) => p["sid"] = "never-seen")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task No_cookie_or_credential_is_needed()
    {
        var (client, _) = await StartAsync();
        client.DefaultRequestHeaders.Clear();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/backchannel-logout", Form(LogoutTokens.Create()))).StatusCode);
    }

    // Request shape

    [Fact]
    public async Task Get_is_405()
    {
        var (client, _) = await StartAsync();

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/backchannel-logout")).StatusCode);
    }

    [Fact]
    public async Task Json_body_is_400()
    {
        var (client, store) = await StartAsync();
        var json = new StringContent($$"""{"logout_token":"{{LogoutTokens.Create()}}"}""", Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/backchannel-logout", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertNoStore(response);
        Assert.False(await store.IsSessionEndedAsync("01a18372-2364-7a2a-afe3-c0626b927850", null, null));
    }

    [Fact]
    public async Task Multipart_body_is_400()
    {
        var (client, _) = await StartAsync();
        var multipart = new MultipartFormDataContent { { new StringContent(LogoutTokens.Create()), "logout_token" } };

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/backchannel-logout", multipart)).StatusCode);
    }

    [Fact]
    public async Task Oversized_body_is_400()
    {
        var (client, _) = await StartAsync();

        var response = await client.PostAsync("/backchannel-logout", Form(new string('a', BackchannelLogoutEndpoint.MaxBodyBytes)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("too large", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Oversized_body_without_content_length_is_400()
    {
        var (client, _) = await StartAsync();
        var stream = new StreamContent(new MemoryStream(Encoding.ASCII.GetBytes("logout_token=" + new string('a', BackchannelLogoutEndpoint.MaxBodyBytes))));
        stream.Headers.ContentType = new("application/x-www-form-urlencoded");
        stream.Headers.ContentLength = null;

        var response = await client.PostAsync("/backchannel-logout", stream);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Missing_logout_token_is_400()
    {
        var (client, _) = await StartAsync();

        var response = await client.PostAsync("/backchannel-logout", new FormUrlEncodedContent([new("token", "x")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Two_logout_tokens_is_400()
    {
        var (client, _) = await StartAsync();
        var token = LogoutTokens.Create();

        var response = await client.PostAsync("/backchannel-logout", new FormUrlEncodedContent([new("logout_token", token), new("logout_token", token)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Token problems

    [Fact]
    public async Task Invalid_token_is_400_with_reason_and_no_logout()
    {
        var (client, store) = await StartAsync();

        var response = await client.PostAsync("/backchannel-logout", Form(LogoutTokens.Create((_, p) => p["nonce"] = "x")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertNoStore(response);
        Assert.Contains("nonce must not be present", await response.Content.ReadAsStringAsync());
        Assert.False(await store.IsSessionEndedAsync("01a18372-2364-7a2a-afe3-c0626b927850", null, null));
    }

    [Fact]
    public async Task Replayed_token_is_400()
    {
        var (client, _) = await StartAsync();
        var token = LogoutTokens.Create();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/backchannel-logout", Form(token))).StatusCode);
        var replay = await client.PostAsync("/backchannel-logout", Form(token));

        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains("jti was already used", await replay.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Store_failure_is_500_with_no_store_so_thunderid_retries()
    {
        var (client, _) = await StartAsync(new ThrowingStore());

        var response = await client.PostAsync("/backchannel-logout", Form(LogoutTokens.Create()));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertNoStore(response);
    }

    [Fact]
    public async Task Logs_never_contain_the_token()
    {
        var (client, _) = await StartAsync();
        var valid = LogoutTokens.Create();
        var invalid = LogoutTokens.Create((_, p) => p["iss"] = "https://evil.example");

        await client.PostAsync("/backchannel-logout", Form(valid));
        await client.PostAsync("/backchannel-logout", Form(invalid));

        Assert.NotEmpty(_logs.Messages);
        foreach (var token in new[] { valid, invalid })
        {
            Assert.DoesNotContain(_logs.Messages, m => m.Contains(token.Split('.')[1]) || m.Contains(token.Split('.')[2]));
        }
    }

    // Cookie enforcement

    [Fact]
    public async Task Cookie_for_the_ended_sid_is_refused_and_cleared()
    {
        var (client, _) = await StartAsync();
        var cookie = await SignInAsync(client, "01a18372-2364-7a2a-afe3-c0626b927850", LogoutTokens.IssuedAt - 600);
        Assert.Equal("01a18372-2364-7a2a-afe3-c0626b927850", await WhoAmIAsync(client, cookie));

        await client.PostAsync("/backchannel-logout", Form(LogoutTokens.Create()));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/test/whoami");
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request);
        Assert.Equal("anonymous", await response.Content.ReadAsStringAsync());
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cookie_for_another_sid_of_the_same_user_survives()
    {
        var (client, _) = await StartAsync();
        var other = await SignInAsync(client, "other-sid", LogoutTokens.IssuedAt - 600);

        await client.PostAsync("/backchannel-logout", Form(LogoutTokens.Create()));

        Assert.Equal("other-sid", await WhoAmIAsync(client, other));
    }

    [Fact]
    public async Task Sub_only_logout_refuses_earlier_sessions_and_keeps_a_later_sign_in()
    {
        var (client, _) = await StartAsync();
        var before = await SignInAsync(client, "sid-before", LogoutTokens.IssuedAt - 600);
        var withinTolerance = await SignInAsync(client, "sid-within", LogoutTokens.IssuedAt + 60);
        var after = await SignInAsync(client, "sid-after", LogoutTokens.IssuedAt + 121);

        await client.PostAsync("/backchannel-logout", Form(LogoutTokens.Create((_, p) => p.Remove("sid"))));

        Assert.Equal("anonymous", await WhoAmIAsync(client, before));
        Assert.Equal("anonymous", await WhoAmIAsync(client, withinTolerance));
        Assert.Equal("sid-after", await WhoAmIAsync(client, after));
    }

    [Fact]
    public async Task Invalid_token_leaves_the_cookie_session_alone()
    {
        var (client, _) = await StartAsync();
        var cookie = await SignInAsync(client, "01a18372-2364-7a2a-afe3-c0626b927850", LogoutTokens.IssuedAt - 600);

        await client.PostAsync("/backchannel-logout", Form(LogoutTokens.Create((h, _) => h["typ"] = "JWT")));

        Assert.Equal("01a18372-2364-7a2a-afe3-c0626b927850", await WhoAmIAsync(client, cookie));
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private sealed class ThrowingStore : ILogoutSessionStore
    {
        public Task<bool> TryRecordLogoutAsync(LogoutToken token, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("store unavailable");

        public Task<bool> IsSessionEndedAsync(string? sessionId, string? subject, DateTimeOffset? authTime, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
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
