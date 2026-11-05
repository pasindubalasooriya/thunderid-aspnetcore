using System.Net;
using System.Text.Json;

namespace ThunderID.AspNetCore.E2E;

// What sign-out relies on: ThunderID issues a refresh token to this client, and revoking it at
// /oauth2/revoke with client_secret_post makes the refresh grant refuse it.
public sealed class RefreshTokenTests : E2ETest
{
    [Fact]
    public async Task ThunderID_issues_a_refresh_token_and_refuses_it_once_revoked()
    {
        await SignInAsync(await NewPageAsync());
        Assert.NotNull(App.LastRefreshToken);

        using var http = new HttpClient();
        var (refreshed, body) = await RefreshAsync(http, App.LastRefreshToken!);
        Assert.Equal(HttpStatusCode.OK, refreshed);
        var rotated = body.GetProperty("refresh_token").GetString()!;

        var revoked = await http.PostAsync(E2EApp.Authority + "/oauth2/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = rotated,
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = E2EApp.ClientId,
            ["client_secret"] = E2EApp.ClientSecret,
        }));
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        var (afterRevocation, error) = await RefreshAsync(http, rotated);
        Assert.Equal(HttpStatusCode.BadRequest, afterRevocation);
        Assert.Equal("invalid_grant", error.GetProperty("error").GetString());
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> RefreshAsync(HttpClient http, string refreshToken)
    {
        var response = await http.PostAsync(E2EApp.Authority + "/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = E2EApp.ClientId,
            ["client_secret"] = E2EApp.ClientSecret,
        }));
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
    }
}
