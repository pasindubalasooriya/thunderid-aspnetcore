using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ThunderID.AspNetCore.Tests;

// AC4.1b: an authorize parameter the SDK sets is ignored when the caller supplies it.
public sealed class AuthorizeParameterTests : IAsyncDisposable
{
    private const string Authority = LogoutTokens.Issuer;
    private const string CallerValue = "caller-value-5d2e";

    private readonly ConcurrentQueue<string> _warnings = new();
    private WebApplication? _app;

    private async Task<HttpResponseMessage> ChallengeAsync(IDictionary<string, string> parameters)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new WarningProvider(_warnings));
        builder.Services.AddThunderID(options =>
        {
            options.Authority = Authority;
            options.ClientId = LogoutTokens.ClientId;
            options.ClientSecret = "test-client-secret";
        });
        builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            options.BackchannelHttpHandler = new StubThunderID();
            foreach (var (key, value) in parameters)
            {
                options.AdditionalAuthorizationParameters[key] = value;
            }
        });

        _app = builder.Build();
        _app.UseAuthentication();
        _app.MapGet("/login", () => Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }));
        await _app.StartAsync();
        return await _app.GetTestClient().GetAsync("/login");
    }

    // The authorize URL's parameters, keeping repeats.
    private static ILookup<string, string> Sent(HttpResponseMessage response) =>
        response.Headers.Location!.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .ToLookup(p => p[0], p => Uri.UnescapeDataString(p[1]));

    [Theory]
    [InlineData("client_id")]
    [InlineData("redirect_uri")]
    [InlineData("code_challenge")]
    [InlineData("code_challenge_method")]
    [InlineData("state")]
    public async Task Owned_parameter_from_the_caller_is_replaced_by_the_sdks(string key)
    {
        var response = await ChallengeAsync(new Dictionary<string, string> { [key] = CallerValue });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var value = Assert.Single(Sent(response)[key]);
        Assert.NotEqual(CallerValue, value);
        var warning = Assert.Single(_warnings);
        Assert.Contains($"sets them itself: {key}.", warning);
        Assert.DoesNotContain(CallerValue, warning);
    }

    [Fact]
    public async Task Sdk_values_are_sent_when_every_owned_parameter_is_supplied()
    {
        var response = await ChallengeAsync(OwnedAuthorizationParameters.Keys.ToDictionary(k => k, _ => CallerValue));

        var sent = Sent(response);
        Assert.All(OwnedAuthorizationParameters.Keys, key => Assert.Single(sent[key]));
        Assert.Equal(LogoutTokens.ClientId, sent["client_id"].Single());
        Assert.Equal("http://localhost/signin-oidc", sent["redirect_uri"].Single());
        Assert.Equal("S256", sent["code_challenge_method"].Single());
        Assert.Equal(43, sent["code_challenge"].Single().Length);
        Assert.Contains("sets them itself: client_id, redirect_uri, code_challenge, code_challenge_method, state.", Assert.Single(_warnings));
    }

    [Fact]
    public async Task Other_parameters_are_passed_through_without_a_warning()
    {
        var response = await ChallengeAsync(new Dictionary<string, string> { ["login_hint"] = "alice@example.com" });

        Assert.Equal("alice@example.com", Assert.Single(Sent(response)["login_hint"]));
        Assert.Empty(_warnings);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private sealed class StubThunderID : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/.well-known/openid-configuration" => Json($$"""
                    {"issuer":"{{Authority}}","authorization_endpoint":"{{Authority}}/oauth2/authorize","token_endpoint":"{{Authority}}/oauth2/token","jwks_uri":"{{Authority}}/oauth2/jwks"}
                    """),
                "/oauth2/jwks" => Json(LogoutTokens.Jwks(LogoutTokens.SigningKey, LogoutTokens.KeyId)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    // Collects ThunderID.AspNetCore's warnings and above.
    private sealed class WarningProvider(ConcurrentQueue<string> warnings) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) =>
            categoryName.StartsWith("ThunderID.AspNetCore", StringComparison.Ordinal) ? new Logger(warnings) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    warnings.Enqueue(formatter(state, exception));
                }
            }
        }
    }
}
