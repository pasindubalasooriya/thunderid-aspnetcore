using System.Net;
using System.Text.Json;
using Microsoft.Playwright;

namespace ThunderID.AspNetCore.E2E;

public sealed class SignOutTests : E2ETest
{
    [Fact]
    public async Task Sign_out_revokes_the_refresh_token_and_ends_the_session_at_thunderid()
    {
        var page = await NewPageAsync();
        await SignInAsync(page);
        var refreshToken = App.LastRefreshToken;
        Assert.NotNull(refreshToken);

        await SignOutAsync(page);

        Assert.False(App.LastSignOut!.RevocationFailed, App.LastSignOut.RevocationError?.Message);
        Assert.Equal("invalid_grant", await RefreshErrorAsync(refreshToken!));
        Assert.Equal("Signed out", await page.Locator("#status").InnerTextAsync());

        // ThunderID's session ended too: signing in again asks for credentials instead of using SSO.
        await page.GotoAsync(E2EApp.Url + "/profile");
        await page.WaitForURLAsync("**/gate/signin**");
        await page.Locator("#action_001").WaitForAsync();
    }

    [Fact]
    public async Task Back_channel_logout_ends_a_copy_of_the_session_elsewhere()
    {
        var page = await NewPageAsync();
        await SignInAsync(page);
        var sid = await ClaimAsync(page, "sid");

        // Another tab or device holding the same app session.
        var copy = await NewPageAsync();
        var appCookies = await page.Context.CookiesAsync([E2EApp.Url]);
        await copy.Context.AddCookiesAsync(appCookies.Select(c => new Microsoft.Playwright.Cookie
        {
            Name = c.Name,
            Value = c.Value,
            Domain = c.Domain,
            Path = c.Path,
            Secure = c.Secure,
            HttpOnly = c.HttpOnly,
            SameSite = c.SameSite,
        }));
        await copy.GotoAsync(E2EApp.Url + "/profile");
        Assert.Equal(sid, await ClaimAsync(copy, "sid"));

        await SignOutAsync(page);

        // ThunderID delivers the logout token asynchronously.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (App.BackchannelLogouts.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        Assert.Contains(App.BackchannelLogouts, l => l.SessionId == sid);
        await copy.GotoAsync(E2EApp.Url + "/profile");
        await copy.WaitForURLAsync("**/gate/signin**");
    }

    private static async Task SignOutAsync(IPage page)
    {
        await page.GotoAsync(E2EApp.Url + "/");
        await page.Locator("#signout").ClickAsync();

        // The round trip through ThunderID's end-session endpoint ends on the home page, signed out.
        await page.Locator("#status", new() { HasText = "Signed out" }).WaitForAsync();
        Assert.Equal(E2EApp.Url + "/", page.Url);
    }

    private static async Task<string?> RefreshErrorAsync(string refreshToken)
    {
        using var http = new HttpClient();
        var response = await http.PostAsync(E2EApp.Authority + "/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = E2EApp.ClientId,
            ["client_secret"] = E2EApp.ClientSecret,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString();
    }
}
