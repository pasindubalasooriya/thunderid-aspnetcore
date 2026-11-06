using System.Net;
using System.Text.Json;
using Microsoft.Playwright;

namespace ThunderID.AspNetCore.E2E;

// Refresh against a real ThunderID. Its tokens last 3600 seconds, so with RefreshBeforeExpiry at
// 3595 seconds a token is due for refresh 5 seconds after it was issued, and a new one isn't.
public sealed class SessionRefreshTests() : E2ETest(new E2EApp { RefreshBeforeExpiry = TimeSpan.FromSeconds(3595) })
{
    private static readonly TimeSpan UntilDue = TimeSpan.FromSeconds(6);

    [Fact]
    public async Task Tokens_are_refreshed_ahead_of_expiry_and_a_replayed_refresh_token_ends_the_session()
    {
        var page = await NewPageAsync();
        await SignInAsync(page);
        var signInRefreshToken = App.LastRefreshToken!;
        Assert.Equal(0, App.Refreshes);

        await Task.Delay(UntilDue);
        var refreshed = await TokenAsync(page);

        Assert.NotEqual("none", refreshed);
        Assert.NotEqual(App.SignInAccessToken, refreshed);
        Assert.Equal(1, App.Refreshes);
        Assert.Equal(refreshed, await TokenAsync(page));
        Assert.Equal(1, App.Refreshes);

        // ThunderID rotated the sign-in refresh token. Sending it again is a replay: refused, and the
        // whole session is revoked, so the app's next refresh is refused and the user is signed out.
        using var http = new HttpClient();
        var replay = await http.PostAsync(E2EApp.Authority + "/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = signInRefreshToken,
            ["client_id"] = E2EApp.ClientId,
            ["client_secret"] = E2EApp.ClientSecret,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("invalid_grant", JsonDocument.Parse(await replay.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());

        await Task.Delay(UntilDue);
        await page.GotoAsync(E2EApp.Url + "/");
        Assert.Equal("Signed out", await page.Locator("#status").InnerTextAsync());
        Assert.Equal(2, App.Refreshes);
    }

    [Fact]
    public async Task Concurrent_requests_with_one_cookie_cause_one_refresh()
    {
        var page = await NewPageAsync();
        await SignInAsync(page);
        await Task.Delay(UntilDue);

        var tokens = await page.EvaluateAsync<string[]>("() => Promise.all(Array.from({ length: 10 }, () => fetch('/token').then(r => r.text())))");

        Assert.Equal(10, tokens.Length);
        Assert.Single(tokens.Distinct());
        Assert.NotEqual(App.SignInAccessToken, tokens[0]);
        Assert.Equal(1, App.Refreshes);

        // No request replayed the rotated token, so the session is intact.
        await page.GotoAsync(E2EApp.Url + "/");
        Assert.StartsWith("Signed in as", await page.Locator("#status").InnerTextAsync());
        Assert.Equal(1, App.Refreshes);
    }

    [Fact]
    public async Task Revoked_refresh_token_ends_the_session_at_the_next_refresh()
    {
        var page = await NewPageAsync();
        await SignInAsync(page);

        using var http = new HttpClient();
        var revoked = await http.PostAsync(E2EApp.Authority + "/oauth2/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = App.LastRefreshToken!,
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = E2EApp.ClientId,
            ["client_secret"] = E2EApp.ClientSecret,
        }));
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        await Task.Delay(UntilDue);
        await page.GotoAsync(E2EApp.Url + "/");

        Assert.Equal("Signed out", await page.Locator("#status").InnerTextAsync());
        Assert.Equal(1, App.Refreshes);
    }

    private static async Task<string> TokenAsync(IPage page)
    {
        var response = await page.GotoAsync(E2EApp.Url + "/token");
        return await response!.TextAsync();
    }
}
