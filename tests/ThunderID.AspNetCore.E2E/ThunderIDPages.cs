using Microsoft.Playwright;

namespace ThunderID.AspNetCore.E2E;

/// <summary>
/// ThunderID's hosted sign-in page. It renders each input with its flow <c>ref</c> as the element
/// ID and each action with its flow element ID, as declared in tests/e2e/thunderid/bootstrap/10-e2e.yaml.
/// </summary>
public sealed class ThunderIDSignInPage(IPage page)
{
    public async Task SignInAsync(string username, string password)
    {
        await page.WaitForURLAsync("**/gate/signin**");
        await page.Locator("#username").FillAsync(username);
        await page.Locator("#password").FillAsync(password);
        await page.Locator("#action_001").ClickAsync();
    }

    public async Task SignUpAsync(string username, string password, string email)
    {
        await page.WaitForURLAsync("**/gate/signin**");
        await page.Locator("[data-action-ref=action_signup]").ClickAsync();

        await page.Locator("#action_credentials").WaitForAsync();
        await page.Locator("#username").FillAsync(username);
        await page.Locator("#password").FillAsync(password);
        await page.Locator("#action_credentials").ClickAsync();

        await page.Locator("#action_schema_attrs").WaitForAsync();
        await page.Locator("#email").FillAsync(email);
        await page.Locator("#action_schema_attrs").ClickAsync();
    }
}

/// <summary>The user registered in tests/e2e/thunderid/bootstrap/10-e2e.yaml, with a TEST-ONLY password.</summary>
public static class TestUser
{
    public const string Username = "e2e-user";
    public const string Password = "e2e-test-only-User1";
    public const string Email = "e2e-user@example.com";
}
