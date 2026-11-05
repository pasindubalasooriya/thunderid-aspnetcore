using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ThunderID.AspNetCore.BackchannelLogout;

namespace ThunderID.AspNetCore.Tests;

// An operation used without AddThunderID fails with a distinct, documented error, not a framework one.
public sealed class MissingRegistrationTests
{
    [Fact]
    public async Task Back_channel_endpoint_without_AddThunderID_fails_when_it_is_mapped()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();

        var ex = Assert.Throws<ThunderIDRuntimeException>(() => app.MapThunderIDBackchannelLogout());

        Assert.Equal(ThunderIDErrorCodes.NotRegistered, ex.Code);
        Assert.Equal("MapThunderIDBackchannelLogout needs AddThunderID. Call builder.Services.AddThunderID(...) before using it.", ex.Message);
    }

    [Fact]
    public async Task Back_channel_endpoint_with_its_callback_without_AddThunderID_fails_when_it_is_mapped()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();

        var ex = Assert.Throws<ThunderIDRuntimeException>(() => app.MapThunderIDBackchannelLogout("/backchannel-logout", _ => Task.CompletedTask));

        Assert.Equal(ThunderIDErrorCodes.NotRegistered, ex.Code);
    }

    [Fact]
    public async Task Sign_out_without_AddThunderID_fails_with_a_ThunderID_error()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        // A cookie scheme alone, so the failure isn't the framework's missing-handler error.
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        await using var app = builder.Build();
        ThunderIDRuntimeException? caught = null;
        app.MapPost("/signout", async (HttpContext context) =>
        {
            try
            {
                await context.SignOutOfThunderIDAsync("/");
            }
            catch (ThunderIDRuntimeException ex)
            {
                caught = ex;
            }
        });
        await app.StartAsync();

        await app.GetTestClient().PostAsync("/signout", null);

        Assert.NotNull(caught);
        Assert.Equal(ThunderIDErrorCodes.NotRegistered, caught.Code);
        Assert.StartsWith("SignOutOfThunderIDAsync needs AddThunderID.", caught.Message);
    }
}
