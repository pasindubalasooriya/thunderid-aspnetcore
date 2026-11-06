using System.Text.Json;
using Microsoft.Playwright;

namespace ThunderID.AspNetCore.E2E;

// RFC 8693 token exchange against a real ThunderID, for the resource server in the e2e bootstrap.
public sealed class TokenExchangeTests : E2ETest
{
    private const string Billing = "https://billing.e2e.example";

    [Fact]
    public async Task Session_token_is_exchanged_for_one_with_the_resource_as_audience_and_the_session_is_unchanged()
    {
        var page = await NewPageAsync();
        await SignInAsync(page);
        var subject = await ClaimAsync(page, "sub");
        var sessionToken = await TextAsync(page, "/token");

        var exchanged = await JsonAsync(page, "/exchange?resource=" + Uri.EscapeDataString(Billing));

        Assert.Equal(Billing, exchanged.GetProperty("aud").GetString());
        Assert.Equal("urn:ietf:params:oauth:token-type:access_token", exchanged.GetProperty("type").GetString());
        Assert.Equal(subject, exchanged.GetProperty("sub").GetString());
        Assert.NotEqual(sessionToken, exchanged.GetProperty("token").GetString());
        Assert.Equal(sessionToken, await TextAsync(page, "/token"));
    }

    [Fact]
    public async Task Unregistered_resource_is_refused_with_thunderids_status()
    {
        var page = await NewPageAsync();
        await SignInAsync(page);

        var refused = await JsonAsync(page, "/exchange?resource=" + Uri.EscapeDataString("https://unknown.e2e.example"));

        Assert.Equal("JS-AUTH_CORE-RCG-HE03", refused.GetProperty("code").GetString());
        Assert.Equal(400, refused.GetProperty("status").GetInt32());
        Assert.Equal("ThunderID's token endpoint refused the token exchange (invalid_target).", refused.GetProperty("message").GetString());
    }

    private static async Task<string> TextAsync(IPage page, string path) => await (await page.GotoAsync(E2EApp.Url + path))!.TextAsync();

    private static async Task<JsonElement> JsonAsync(IPage page, string path) => JsonDocument.Parse(await TextAsync(page, path)).RootElement.Clone();
}
