using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StubThunderID = ThunderID.AspNetCore.Tests.TokenRefreshTests.StubThunderID;

namespace ThunderID.AspNetCore.Tests;

// exchangeToken (RFC 8693): input checked before any network call, the session never changed, errors coded.
public sealed class TokenExchangeTests : IAsyncDisposable
{
    private const string Authority = LogoutTokens.Issuer;
    private const string AccessToken = "test-access-token-x0-5a9c";
    private const string RefreshToken = "test-refresh-token-x0-3d7e";
    private const string ExchangedToken = "test-exchanged-token-8f2b";
    private const string ExchangeBody = $$"""{"access_token":"{{ExchangedToken}}","issued_token_type":"urn:ietf:params:oauth:token-type:jwt","token_type":"Bearer","expires_in":300,"scope":"invoice:read openid"}""";

    private readonly ConcurrentQueue<string> _logs = new();
    private WebApplication? _app;
    private ThunderIDTokenExchangeRequest? _request;
    private ThunderIDTokenExchangeResult? _result;
    private ThunderIDException? _error;

    private async Task<HttpClient> StartAsync(StubThunderID thunderID)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ListLoggerProvider(_logs));
        builder.Services.AddThunderID(options =>
        {
            options.Authority = Authority;
            options.ClientId = LogoutTokens.ClientId;
            options.ClientSecret = "test-client-secret";
        });
        builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options => options.BackchannelHttpHandler = thunderID);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.MapPost("/test/signin", async (HttpContext context, int expiresIn) =>
        {
            var properties = new AuthenticationProperties();
            properties.StoreTokens([
                new() { Name = "access_token", Value = AccessToken },
                new() { Name = "refresh_token", Value = RefreshToken },
                new() { Name = "expires_at", Value = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToString("o", CultureInfo.InvariantCulture) },
            ]);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u-1")], CookieAuthenticationDefaults.AuthenticationScheme)), properties);
        });
        _app.MapPost("/test/exchange", async (HttpContext context) =>
        {
            try
            {
                _result = await context.ExchangeThunderIDTokenAsync(_request!);
            }
            catch (ThunderIDException ex)
            {
                _error = ex;
            }
        });
        _app.MapGet("/test/token", async (HttpContext context) => await context.GetThunderIDAccessTokenAsync() ?? "signed out");

        await _app.StartAsync();
        return _app.GetTestClient();
    }

    private static async Task<string> SignInAsync(HttpClient client, int expiresIn = 3600)
    {
        var response = await client.PostAsync($"/test/signin?expiresIn={expiresIn}", null);
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    private async Task<HttpResponseMessage> ExchangeAsync(HttpClient client, string? cookie, ThunderIDTokenExchangeRequest? request)
    {
        _request = request;
        var message = new HttpRequestMessage(HttpMethod.Post, "/test/exchange");
        if (cookie is not null)
        {
            message.Headers.Add("Cookie", cookie);
        }

        return await client.SendAsync(message);
    }

    private void AssertNoTokenLogged()
    {
        var logs = string.Join('\n', _logs);
        foreach (var token in new[] { AccessToken, RefreshToken, ExchangedToken, StubThunderID.NewAccessToken, "secret-actor-token-77" })
        {
            Assert.DoesNotContain(token, logs);
        }
    }

    [Fact]
    public async Task Exchanges_the_session_access_token_with_every_field_and_reads_the_result()
    {
        var thunderID = new StubThunderID { Body = ExchangeBody };
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client);
        var request = new ThunderIDTokenExchangeRequest
        {
            Resource = "https://billing.example",
            Audience = "billing",
            RequestedTokenType = ThunderIDTokenTypes.Jwt,
            ActorToken = "secret-actor-token-77",
            ActorTokenType = ThunderIDTokenTypes.AccessToken,
        };
        request.Scopes.Add("invoice:read");
        request.Scopes.Add("openid");

        await ExchangeAsync(client, cookie, request);

        Assert.Null(_error);
        Assert.Equal(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = AccessToken,
            ["subject_token_type"] = ThunderIDTokenTypes.AccessToken,
            ["client_id"] = LogoutTokens.ClientId,
            ["client_secret"] = "test-client-secret",
            ["resource"] = "https://billing.example",
            ["audience"] = "billing",
            ["scope"] = "invoice:read openid",
            ["requested_token_type"] = ThunderIDTokenTypes.Jwt,
            ["actor_token"] = "secret-actor-token-77",
            ["actor_token_type"] = ThunderIDTokenTypes.AccessToken,
        }, Assert.Single(thunderID.TokenRequests));
        Assert.Equal(ExchangedToken, _result!.AccessToken);
        Assert.Equal(ThunderIDTokenTypes.Jwt, _result.IssuedTokenType);
        Assert.Equal("Bearer", _result.TokenType);
        Assert.Equal(TimeSpan.FromSeconds(300), _result.ExpiresIn);
        Assert.Equal(["invoice:read", "openid"], _result.Scopes);
        Assert.Null(_result.RefreshToken);
        Assert.Contains("Exchanged a token at ThunderID for a urn:ietf:params:oauth:token-type:jwt", _logs);
        AssertNoTokenLogged();
    }

    [Fact]
    public async Task Minimal_request_sends_only_the_required_fields_and_leaves_the_session_unchanged()
    {
        var thunderID = new StubThunderID { Body = ExchangeBody };
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client);

        var response = await ExchangeAsync(client, cookie, new ThunderIDTokenExchangeRequest());

        Assert.Equal(["grant_type", "subject_token", "subject_token_type", "client_id", "client_secret"], Assert.Single(thunderID.TokenRequests).Keys);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var token = new HttpRequestMessage(HttpMethod.Get, "/test/token");
        token.Headers.Add("Cookie", cookie);
        Assert.Equal(AccessToken, await (await client.SendAsync(token)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Given_subject_token_is_exchanged_without_a_session()
    {
        var thunderID = new StubThunderID { Body = ExchangeBody };
        var client = await StartAsync(thunderID);

        await ExchangeAsync(client, null, new ThunderIDTokenExchangeRequest { SubjectToken = "given-subject-token", SubjectTokenType = ThunderIDTokenTypes.IdToken });

        var form = Assert.Single(thunderID.TokenRequests);
        Assert.Equal("given-subject-token", form["subject_token"]);
        Assert.Equal(ThunderIDTokenTypes.IdToken, form["subject_token_type"]);
        Assert.Equal(ExchangedToken, _result!.AccessToken);
    }

    [Fact]
    public async Task Session_token_due_for_refresh_is_refreshed_before_it_is_exchanged()
    {
        var thunderID = new StubThunderID();
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client, expiresIn: 30);

        await ExchangeAsync(client, cookie, new ThunderIDTokenExchangeRequest());

        Assert.Equal(["refresh_token", "urn:ietf:params:oauth:grant-type:token-exchange"], thunderID.TokenRequests.Select(f => f["grant_type"]));
        Assert.Equal(StubThunderID.NewAccessToken, thunderID.TokenRequests.Last()["subject_token"]);
    }

    public static TheoryData<Action<ThunderIDTokenExchangeRequest>, string> InvalidRequests => new()
    {
        { r => r.SubjectToken = " ", "SubjectToken is empty; leave it null to use the signed-in user's access token." },
        { r => r.SubjectTokenType = "access_token", "SubjectTokenType must be an absolute URI, such as a ThunderIDTokenTypes value." },
        { r => r.SubjectTokenType = null!, "SubjectTokenType must be an absolute URI, such as a ThunderIDTokenTypes value." },
        { r => r.RequestedTokenType = "jwt", "RequestedTokenType must be an absolute URI, such as a ThunderIDTokenTypes value." },
        { r => r.Resource = "billing", "Resource must be an absolute URI without a fragment." },
        { r => r.Resource = "https://billing.example/#part", "Resource must be an absolute URI without a fragment." },
        { r => r.Audience = "", "Audience is empty." },
        { r => r.Scopes.Add("invoice:read openid"), "Each scope must be non-empty, with no spaces, quotes or backslashes." },
        { r => r.Scopes.Add(""), "Each scope must be non-empty, with no spaces, quotes or backslashes." },
        { r => r.Scopes.Add("a\"b"), "Each scope must be non-empty, with no spaces, quotes or backslashes." },
        { r => r.ActorToken = "secret-actor-token-77", "ActorToken and ActorTokenType must be set together." },
        { r => r.ActorTokenType = ThunderIDTokenTypes.AccessToken, "ActorToken and ActorTokenType must be set together." },
        { r => { r.ActorToken = "secret-actor-token-77"; r.ActorTokenType = "token"; }, "ActorTokenType must be an absolute URI, such as a ThunderIDTokenTypes value." },
        { r => { r.Resource = "billing"; r.Audience = ""; }, "Resource must be an absolute URI without a fragment. Audience is empty." },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Invalid_request_fails_before_any_network_call(Action<ThunderIDTokenExchangeRequest> change, string reason)
    {
        var thunderID = new StubThunderID { Body = ExchangeBody };
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client);
        var request = new ThunderIDTokenExchangeRequest();
        change(request);

        await ExchangeAsync(client, cookie, request);

        var error = Assert.IsType<ThunderIDRuntimeException>(_error);
        Assert.Equal(ThunderIDErrorCodes.TokenExchangeInvalid, error.Code);
        Assert.Equal("The token exchange request is invalid, so it wasn't sent: " + reason, error.Message);
        Assert.Empty(thunderID.Paths);
    }

    [Fact]
    public async Task Missing_request_is_an_invalid_request()
    {
        var thunderID = new StubThunderID();
        var client = await StartAsync(thunderID);

        await ExchangeAsync(client, null, null);

        Assert.Equal(ThunderIDErrorCodes.TokenExchangeInvalid, _error!.Code);
        Assert.Empty(thunderID.Paths);
    }

    [Fact]
    public async Task No_subject_and_no_session_fails_before_any_network_call()
    {
        var thunderID = new StubThunderID();
        var client = await StartAsync(thunderID);

        await ExchangeAsync(client, null, new ThunderIDTokenExchangeRequest());

        Assert.Equal(ThunderIDErrorCodes.TokenExchangeNoSubject, Assert.IsType<ThunderIDRuntimeException>(_error).Code);
        Assert.Empty(thunderID.Paths);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "Bad Request")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Service Unavailable")]
    public async Task Refusal_is_an_api_error_with_thunderids_status_and_leaves_the_session_alone(HttpStatusCode status, string text)
    {
        var thunderID = new StubThunderID { Status = status, Body = """{"error":"invalid_target","error_description":"secret-description-31d0"}""" };
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client);

        var response = await ExchangeAsync(client, cookie, new ThunderIDTokenExchangeRequest { Resource = "https://unknown.example" });

        var error = Assert.IsType<ThunderIDApiException>(_error);
        Assert.Equal(ThunderIDErrorCodes.TokenExchangeRejected, error.Code);
        Assert.Equal("ThunderID's token endpoint refused the token exchange (invalid_target).", error.Message);
        Assert.Equal((int)status, error.StatusCode);
        Assert.Equal(text, error.StatusText);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Contains("Token exchange failed (JS-AUTH_CORE-RCG-HE03): ThunderID's token endpoint refused the token exchange (invalid_target).", _logs);
        Assert.DoesNotContain("secret-description-31d0", string.Join('\n', _logs));
        AssertNoTokenLogged();
    }

    public static TheoryData<string, string, Action<StubThunderID>> RuntimeFailures => new()
    {
        { ThunderIDErrorCodes.TokenExchangeUnreachable, "ThunderID's token endpoint could not be reached for the token exchange.", t => t.Failure = new HttpRequestException("connection refused") },
        { ThunderIDErrorCodes.TokenExchangeResponseInvalid, "ThunderID's token endpoint answered the token exchange without an access token.", t => t.Body = """{"token_type":"Bearer"}""" },
        { ThunderIDErrorCodes.TokenExchangeResponseInvalid, "ThunderID's token endpoint answered the token exchange without an access token.", t => t.Body = "not json" },
        { ThunderIDErrorCodes.TokenExchangeEndpointMissing, "ThunderID's discovery document has no token_endpoint.", t => t.AdvertiseTokenEndpoint = false },
    };

    [Theory]
    [MemberData(nameof(RuntimeFailures))]
    public async Task Other_failures_are_runtime_errors_with_their_code(string code, string message, Action<StubThunderID> fail)
    {
        var thunderID = new StubThunderID();
        fail(thunderID);
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client);

        await ExchangeAsync(client, cookie, new ThunderIDTokenExchangeRequest());

        var error = Assert.IsType<ThunderIDRuntimeException>(_error);
        Assert.Equal(code, error.Code);
        Assert.Equal(message, error.Message);
        Assert.Contains($"Token exchange failed ({code}): {message}", _logs);
        AssertNoTokenLogged();
    }

    [Fact]
    public async Task Exchange_that_gets_no_answer_times_out_after_10_seconds()
    {
        var client = await StartAsync(new StubThunderID { Hangs = true });
        var cookie = await SignInAsync(client);

        await ExchangeAsync(client, cookie, new ThunderIDTokenExchangeRequest());

        Assert.Equal(ThunderIDErrorCodes.TokenExchangeTimedOut, _error!.Code);
        Assert.Equal("ThunderID's token endpoint did not answer the token exchange within 10 seconds.", _error.Message);
    }

    [Fact]
    public async Task Token_is_not_sent_when_discovery_reports_another_issuer()
    {
        var thunderID = new StubThunderID { Issuer = "https://evil.example" };
        var client = await StartAsync(thunderID);
        var cookie = await SignInAsync(client);

        await ExchangeAsync(client, cookie, new ThunderIDTokenExchangeRequest());

        Assert.Equal(ThunderIDErrorCodes.DiscoveryIssuerMismatch, _error!.Code);
        Assert.Empty(thunderID.TokenRequests);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private sealed class ListLoggerProvider(ConcurrentQueue<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(messages);

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
