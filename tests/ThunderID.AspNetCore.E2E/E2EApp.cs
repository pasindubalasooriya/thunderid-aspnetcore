using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ThunderID.AspNetCore.BackchannelLogout;

namespace ThunderID.AspNetCore.E2E;

/// <summary>
/// The web app the tests sign in to, hosted in the test process so the tests can see what the
/// library did. The browser uses HTTPS; ThunderID's container delivers back-channel logout over
/// plain HTTP through host.docker.internal, which the development certificate doesn't cover.
/// </summary>
public sealed class E2EApp : IAsyncDisposable
{
    public const string Url = "https://localhost:7201";
    public const string Authority = "https://localhost:8091";
    public const string ClientId = "e2e-webapp";

    // TEST-ONLY, registered in tests/e2e/thunderid/bootstrap/10-e2e.yaml for the throwaway container.
    public const string ClientSecret = "e2e-test-only-client-secret-do-not-reuse";

    private WebApplication? _app;

    /// <summary>The refresh token from the latest token response. Never printed.</summary>
    public string? LastRefreshToken { get; private set; }

    public ThunderIDSignOutResult? LastSignOut { get; private set; }

    public ConcurrentQueue<(string? SessionId, string? Subject)> BackchannelLogouts { get; } = new();

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        // On Linux, host.docker.internal is the Docker bridge, so CI listens on every interface.
        builder.WebHost.UseUrls(Url, Environment.GetEnvironmentVariable("E2E_BACKCHANNEL_URL") ?? "http://localhost:5201");

        builder.Services.AddThunderID(options =>
        {
            options.Authority = Authority;
            options.ClientId = ClientId;
            options.ClientSecret = ClientSecret;
        });
        builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            options.Events.OnTokenResponseReceived = context =>
            {
                LastRefreshToken = context.TokenEndpointResponse.RefreshToken;
                return Task.CompletedTask;
            });
        builder.Services.AddAuthorization();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();

        _app.MapGet("/", (HttpContext context) => Html(context.User.Identity?.IsAuthenticated == true
            ? $"""<p id="status">Signed in as {Encode(context.User.Identity.Name)}</p><form method="post" action="/signout"><button id="signout">Sign out</button></form>"""
            : """<p id="status">Signed out</p><a id="signin" href="/profile">Sign in</a>"""));
        _app.MapGet("/profile", (HttpContext context) => Html(
            "<dl>" + string.Concat(context.User.Claims.Select(c => $"""<dt>{Encode(c.Type)}</dt><dd id="claim-{Encode(c.Type)}">{Encode(c.Value)}</dd>""")) + "</dl>"))
            .RequireAuthorization();
        _app.MapPost("/signout", async (HttpContext context) =>
        {
            LastSignOut = await context.SignOutOfThunderIDAsync("/");
            return Results.Empty;
        });
        _app.MapThunderIDBackchannelLogout("/backchannel-logout", context =>
        {
            BackchannelLogouts.Enqueue((context.SessionId, context.Subject));
            return Task.CompletedTask;
        });

        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");

    private static IResult Html(string body) => Results.Content($"<!doctype html><html><body>{body}</body></html>", "text/html");
}
