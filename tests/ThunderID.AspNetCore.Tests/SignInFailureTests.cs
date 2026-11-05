using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace ThunderID.AspNetCore.Tests;

// A failed sign-in callback reaches the app as the framework's AuthenticationFailureException,
// with a ThunderIDException as its InnerException.
public sealed class SignInFailureTests : IAsyncDisposable
{
    private const string Authority = LogoutTokens.Issuer;
    private const string Code = "test-authorization-code-7c41";

    private readonly ConcurrentQueue<Exception> _thrown = new();
    private WebApplication? _app;

    private async Task<HttpClient> StartAsync(Action<OpenIdConnectOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddThunderID(options =>
        {
            options.Authority = Authority;
            options.ClientId = LogoutTokens.ClientId;
            options.ClientSecret = "test-client-secret";
        });
        builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            options.BackchannelHttpHandler = new StubThunderID();
            configure?.Invoke(options);
        });

        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                _thrown.Enqueue(ex);
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        });
        _app.UseAuthentication();
        _app.MapGet("/login", () => Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }));
        await _app.StartAsync();
        return _app.GetTestClient();
    }

    // Starts a sign-in, then returns to the callback with the given query, carrying its cookies.
    private static async Task<HttpResponseMessage> CallbackAsync(HttpClient client, Func<string, string> query)
    {
        var login = await client.GetAsync("/login");
        var state = QueryHelpers.ParseQuery(login.Headers.Location!.Query)["state"].ToString();
        var request = new HttpRequestMessage(HttpMethod.Get, "/signin-oidc?" + query(Uri.EscapeDataString(state)));
        request.Headers.Add("Cookie", string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])));
        return await client.SendAsync(request);
    }

    private ThunderIDException ThrownThunderIDError()
    {
        var thrown = Assert.Single(_thrown);
        Assert.IsType<AuthenticationFailureException>(thrown);
        return Assert.IsAssignableFrom<ThunderIDException>(thrown.InnerException);
    }

    [Fact]
    public async Task Refused_code_is_an_api_error_with_thunderids_status()
    {
        var client = await StartAsync();

        await CallbackAsync(client, state => $"code={Code}&state={state}");

        var error = Assert.IsType<ThunderIDApiException>(ThrownThunderIDError());
        Assert.Equal(ThunderIDErrorCodes.SignInTokenRequestRejected, error.Code);
        Assert.Equal((400, "Bad Request"), (error.StatusCode, error.StatusText));
        Assert.Equal("ThunderID's token endpoint refused the authorization code: invalid_grant.", error.Message);
        Assert.IsType<OpenIdConnectProtocolException>(error.InnerException);
    }

    [Fact]
    public async Task Concurrent_callbacks_each_get_their_own_status()
    {
        var client = await StartAsync();

        await Parallel.ForEachAsync(Enumerable.Range(0, 40), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (i, _) =>
            await CallbackAsync(client, state => $"code={(i % 2 == 0 ? "bad-client" : Code)}&state={state}"));

        var errors = _thrown.Select(e => Assert.IsType<ThunderIDApiException>(e.InnerException)).ToList();
        Assert.Equal(40, errors.Count);
        Assert.All(errors, e => Assert.Equal(e.StatusCode == 401 ? "ThunderID's token endpoint refused the authorization code: invalid_client." : "ThunderID's token endpoint refused the authorization code: invalid_grant.", e.Message));
        Assert.Equal(20, errors.Count(e => e.StatusCode == 401));
    }

    [Fact]
    public async Task Error_sent_to_the_callback_is_a_runtime_error_with_thunderids_code()
    {
        var client = await StartAsync();

        await CallbackAsync(client, state => $"error=access_denied&state={state}");

        var error = Assert.IsType<ThunderIDRuntimeException>(ThrownThunderIDError());
        Assert.Equal(ThunderIDErrorCodes.SignInErrorResponse, error.Code);
        Assert.Equal("ThunderID returned an error to the sign-in callback: access_denied.", error.Message);
    }

    [Fact]
    public async Task State_that_was_not_issued_is_a_runtime_error()
    {
        var client = await StartAsync();

        await CallbackAsync(client, _ => $"code={Code}&state=not-a-state-we-issued");

        var error = Assert.IsType<ThunderIDRuntimeException>(ThrownThunderIDError());
        Assert.Equal(ThunderIDErrorCodes.SignInCallbackInvalid, error.Code);
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public async Task Invalid_id_token_after_an_accepted_code_is_a_runtime_error_not_an_api_error()
    {
        var client = await StartAsync();

        await CallbackAsync(client, state => $"code=accepted&state={state}");

        var error = Assert.IsType<ThunderIDRuntimeException>(ThrownThunderIDError());
        Assert.Equal(ThunderIDErrorCodes.SignInCallbackInvalid, error.Code);
    }

    [Fact]
    public async Task App_with_its_own_backchannel_gets_the_code_without_a_status()
    {
        var client = await StartAsync(options => options.Backchannel = new HttpClient(new StubThunderID()));

        await CallbackAsync(client, state => $"code={Code}&state={state}");

        var error = Assert.IsType<ThunderIDRuntimeException>(ThrownThunderIDError());
        Assert.Equal(ThunderIDErrorCodes.SignInTokenRequestRejected, error.Code);
    }

    [Fact]
    public async Task Apps_own_failure_handler_still_runs_and_sees_the_thunderid_error()
    {
        Exception? seen = null;
        var client = await StartAsync(options => options.Events.OnRemoteFailure = context =>
        {
            seen = context.Failure;
            context.Response.Redirect("/sign-in-failed");
            context.HandleResponse();
            return Task.CompletedTask;
        });

        var response = await CallbackAsync(client, state => $"code={Code}&state={state}");

        Assert.Empty(_thrown);
        Assert.Equal("/sign-in-failed", response.Headers.Location!.ToString());
        Assert.Equal(ThunderIDErrorCodes.SignInTokenRequestRejected, Assert.IsType<ThunderIDApiException>(seen).Code);
    }

    [Fact]
    public async Task Error_never_contains_the_authorization_code()
    {
        var client = await StartAsync();

        await CallbackAsync(client, state => $"code={Code}&state={state}");

        var error = ThrownThunderIDError();
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            Assert.DoesNotContain(Code, e.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    // Discovery, and a token endpoint: "accepted" gets 200 with an ID token that isn't a JWT,
    // "bad-client" gets 401 invalid_client, and any other code 400 invalid_grant.
    private sealed class StubThunderID : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/.well-known/openid-configuration":
                    return Json(HttpStatusCode.OK, $$"""
                        {"issuer":"{{Authority}}","authorization_endpoint":"{{Authority}}/oauth2/authorize","token_endpoint":"{{Authority}}/oauth2/token","jwks_uri":"{{Authority}}/oauth2/jwks","end_session_endpoint":"{{Authority}}/oauth2/logout"}
                        """);
                case "/oauth2/jwks":
                    return Json(HttpStatusCode.OK, LogoutTokens.Jwks(LogoutTokens.SigningKey, LogoutTokens.KeyId));
                case "/oauth2/token":
                    var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
                    await Task.Delay(Random.Shared.Next(1, 10), cancellationToken);
                    if (form["code"] == "accepted")
                    {
                        return Json(HttpStatusCode.OK, """{"access_token":"at","token_type":"Bearer","id_token":"not-a-jwt"}""");
                    }

                    return form["code"] == "bad-client"
                        ? Json(HttpStatusCode.Unauthorized, """{"error":"invalid_client","error_description":"client authentication failed"}""")
                        : Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"the code is expired"}""");
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
