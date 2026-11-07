using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StubThunderID = ThunderID.AspNetCore.Tests.TokenRefreshTests.StubThunderID;

namespace ThunderID.AspNetCore.Tests;

// reInitialize: a configuration reload applies the mutable settings, and a change to an immutable one
// or an invalid value is rejected whole, keeping the previous settings.
public sealed class ReloadTests : IAsyncDisposable
{
    private const string Authority = LogoutTokens.Issuer;
    private const string Secret = "test-client-secret-s1-0a4d";
    private const string RotatedSecret = "test-client-secret-s2-7e1b";
    private const string Applied = "Applied the reloaded ThunderID settings";

    private readonly ConcurrentQueue<string> _logs = new();
    private readonly ReplaceableSource _source = new();
    private readonly StubThunderID _thunderID = new();
    private WebApplication? _app;

    private static Dictionary<string, string?> Settings(string authority = Authority, string clientId = LogoutTokens.ClientId, string secret = Secret,
        string[]? scopes = null, string? resource = null, string? refreshBeforeExpiry = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ThunderID:Authority"] = authority,
            ["ThunderID:ClientId"] = clientId,
            ["ThunderID:ClientSecret"] = secret,
            ["Unrelated:Value"] = "1",
        };
        var i = 0;
        foreach (var scope in scopes ?? ["orders"])
        {
            values[$"ThunderID:Scopes:{i++}"] = scope;
        }

        if (resource is not null)
        {
            values["ThunderID:Resource"] = resource;
        }

        if (refreshBeforeExpiry is not null)
        {
            values["ThunderID:RefreshBeforeExpiry"] = refreshBeforeExpiry;
        }

        return values;
    }

    private async Task<HttpClient> StartAsync(Dictionary<string, string?>? settings = null)
    {
        _source.Replace(settings ?? Settings(), notify: false);
        var configuration = new ConfigurationBuilder().Add(_source).Build();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ListLoggerProvider(_logs));
        builder.Services.AddThunderID(configuration.GetSection("ThunderID"));
        builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options => options.BackchannelHttpHandler = _thunderID);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.MapGet("/login", () => Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }));
        _app.MapPost("/test/exchange", async (HttpContext context) =>
            (await context.ExchangeThunderIDTokenAsync(new ThunderIDTokenExchangeRequest { SubjectToken = "test-subject-token-3c8f" })).AccessToken);
        await _app.StartAsync();
        return _app.GetTestClient();
    }

    private void Reload(Dictionary<string, string?> settings) => _source.Replace(settings, notify: true);

    private static async Task<ILookup<string, string>> AuthorizeAsync(HttpClient client)
    {
        var response = await client.GetAsync("/login");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .ToLookup(p => p[0], p => Uri.UnescapeDataString(p[1]));
    }

    private async Task<string> SecretSentAsync(HttpClient client)
    {
        (await client.PostAsync("/test/exchange", null)).EnsureSuccessStatusCode();
        Assert.True(_thunderID.TokenRequests.TryDequeue(out var form));
        return form!["client_secret"];
    }

    private OpenIdConnectOptions Oidc => _app!.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);

    private TimeSpan RefreshBeforeExpiry => _app!.Services.GetRequiredService<IOptionsMonitor<TokenRefreshSettings>>().CurrentValue.RefreshBeforeExpiry;

    private string[] Warnings => _logs.Where(m => m.StartsWith("The reloaded ThunderID settings weren't applied", StringComparison.Ordinal)).ToArray();

    [Fact]
    public async Task Settings_are_read_from_the_section()
    {
        var client = await StartAsync(Settings(scopes: ["orders", "billing"], resource: "https://api.example", refreshBeforeExpiry: "00:02:00"));

        var sent = await AuthorizeAsync(client);

        Assert.Equal(LogoutTokens.ClientId, sent["client_id"].Single());
        Assert.Equal("openid profile email orders billing", sent["scope"].Single());
        Assert.Equal("https://api.example", sent["resource"].Single());
        Assert.Equal(Secret, await SecretSentAsync(client));
        Assert.Equal(TimeSpan.FromMinutes(2), RefreshBeforeExpiry);
    }

    [Fact]
    public async Task Changed_scopes_and_resource_are_in_the_next_authorize_request()
    {
        var client = await StartAsync();
        Assert.Equal("openid profile email orders", (await AuthorizeAsync(client))["scope"].Single());

        Reload(Settings(scopes: ["billing"], resource: "https://billing.example"));
        var sent = await AuthorizeAsync(client);

        Assert.Equal("openid profile email billing", sent["scope"].Single());
        Assert.Equal("https://billing.example", sent["resource"].Single());
        Assert.Contains(Applied, _logs);
        Assert.Empty(Warnings);
    }

    [Fact]
    public async Task A_rotated_secret_is_sent_at_the_next_token_request_and_never_logged()
    {
        var client = await StartAsync();
        Assert.Equal(Secret, await SecretSentAsync(client));

        Reload(Settings(secret: RotatedSecret));

        Assert.Equal(RotatedSecret, await SecretSentAsync(client));
        Assert.DoesNotContain(_logs, m => m.Contains(Secret) || m.Contains(RotatedSecret));
    }

    [Fact]
    public async Task A_changed_refresh_margin_applies()
    {
        await StartAsync();
        Assert.Equal(TimeSpan.FromSeconds(60), RefreshBeforeExpiry);

        Reload(Settings(refreshBeforeExpiry: "00:05:00"));

        Assert.Equal(TimeSpan.FromMinutes(5), RefreshBeforeExpiry);
    }

    [Theory]
    [InlineData("https://other.example", LogoutTokens.ClientId, "Authority can't change")]
    [InlineData(Authority, "other-client-9b2e", "ClientId can't change")]
    [InlineData("https://other.example", "other-client-9b2e", "Authority and ClientId can't change")]
    public async Task Changing_authority_or_client_id_is_rejected_whole(string authority, string clientId, string reason)
    {
        var client = await StartAsync();
        var before = Oidc;

        Reload(Settings(authority: authority, clientId: clientId, secret: RotatedSecret, scopes: ["billing"], refreshBeforeExpiry: "00:05:00"));

        var warning = Assert.Single(Warnings);
        Assert.Equal($"The reloaded ThunderID settings weren't applied (ASPNETCORE-CONFIG-RLD-IV01): {reason} while the app runs; restart the app to use a new value. The previous settings stay.", warning);
        Assert.DoesNotContain("other", warning);
        Assert.Same(before, Oidc);
        var sent = await AuthorizeAsync(client);
        Assert.Equal(LogoutTokens.ClientId, sent["client_id"].Single());
        Assert.Equal("openid profile email orders", sent["scope"].Single());
        Assert.Equal(Secret, await SecretSentAsync(client));
        Assert.Equal(TimeSpan.FromSeconds(60), RefreshBeforeExpiry);
        Assert.DoesNotContain(Applied, _logs);
    }

    [Theory]
    [InlineData("-00:00:01", "RefreshBeforeExpiry can't be negative.")]
    [InlineData("soon-4f1a", "RefreshBeforeExpiry must be a time span, such as 00:01:00.")]
    public async Task An_invalid_value_is_rejected_whole_and_requests_keep_working(string refreshBeforeExpiry, string reason)
    {
        var client = await StartAsync();
        var before = Oidc;

        Reload(Settings(secret: RotatedSecret, scopes: ["billing"], refreshBeforeExpiry: refreshBeforeExpiry));

        var warning = Assert.Single(Warnings);
        Assert.Equal($"The reloaded ThunderID settings weren't applied (ASPNETCORE-CONFIG-VAL-IV01): The ThunderID settings are invalid: {reason} The previous settings stay.", warning);
        Assert.DoesNotContain("soon", warning);
        Assert.Same(before, Oidc);
        Assert.Equal("openid profile email orders", (await AuthorizeAsync(client))["scope"].Single());
        Assert.Equal(Secret, await SecretSentAsync(client));
        Assert.Equal(TimeSpan.FromSeconds(60), RefreshBeforeExpiry);
    }

    [Fact]
    public async Task A_valid_reload_after_a_rejected_one_applies()
    {
        var client = await StartAsync();

        Reload(Settings(clientId: "other-client-9b2e", scopes: ["billing"]));
        Reload(Settings(scopes: ["billing"]));

        Assert.Single(Warnings);
        Assert.Contains(Applied, _logs);
        Assert.Equal("openid profile email billing", (await AuthorizeAsync(client))["scope"].Single());
    }

    [Fact]
    public async Task A_reload_that_leaves_the_settings_alone_keeps_the_cached_options()
    {
        await StartAsync();
        var before = Oidc;

        var unrelated = Settings();
        unrelated["Unrelated:Value"] = "2";
        Reload(unrelated);

        Assert.Same(before, Oidc);
        Assert.DoesNotContain(Applied, _logs);
        Assert.Empty(Warnings);
    }

    [Fact]
    public void An_unreadable_value_at_startup_fails_with_a_thunderid_error()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Settings(refreshBeforeExpiry: "soon-4f1a")).Build();

        var error = Assert.Throws<ThunderIDRuntimeException>(() => new ServiceCollection().AddThunderID(configuration.GetSection("ThunderID")));

        Assert.Equal(ThunderIDErrorCodes.InvalidSettings, error.Code);
        Assert.Equal("The ThunderID settings are invalid: RefreshBeforeExpiry must be a time span, such as 00:01:00.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task An_invalid_section_at_startup_fails_like_the_action_overload()
    {
        var error = await Assert.ThrowsAsync<ThunderIDRuntimeException>(() => StartAsync(Settings(authority: "http://insecure.example", clientId: "", refreshBeforeExpiry: "-00:00:01")));

        Assert.Equal(ThunderIDErrorCodes.InvalidSettings, error.Code);
        Assert.Equal("The ThunderID settings are invalid: Authority must be an absolute https URL. ClientId is required. RefreshBeforeExpiry can't be negative.", error.Message);
    }

    [Fact]
    public void The_action_overload_doesnt_watch_for_changes()
    {
        var services = new ServiceCollection();
        services.AddThunderID(options =>
        {
            options.Authority = Authority;
            options.ClientId = LogoutTokens.ClientId;
        });

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(SettingsReloadWatcher));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IOptionsChangeTokenSource<OpenIdConnectOptions>));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IOptionsChangeTokenSource<TokenRefreshSettings>));
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    // In-memory configuration whose whole content can be replaced, signalling a reload like a changed file.
    private sealed class ReplaceableSource : ConfigurationProvider, IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

        public void Replace(Dictionary<string, string?> values, bool notify)
        {
            Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
            if (notify)
            {
                OnReload();
            }
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
