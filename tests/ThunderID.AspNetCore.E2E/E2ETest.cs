using Microsoft.Playwright;

namespace ThunderID.AspNetCore.E2E;

/// <summary>Starts the e2e app and a browser for each test. ThunderID must already be running (tests/e2e/thunderid).</summary>
public abstract class E2ETest : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    protected E2EApp App { get; } = new();

    public async Task InitializeAsync()
    {
        await App.StartAsync();
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync();
    }

    public async Task DisposeAsync()
    {
        await _browser!.DisposeAsync();
        _playwright!.Dispose();
        await App.DisposeAsync();
    }

    /// <summary>A page in a new browser context, so it shares no cookies with other pages.</summary>
    protected async Task<IPage> NewPageAsync() =>
        await (await _browser!.NewContextAsync(new() { IgnoreHTTPSErrors = true })).NewPageAsync();

    protected static async Task SignInAsync(IPage page, string username = TestUser.Username, string password = TestUser.Password)
    {
        await page.GotoAsync(E2EApp.Url + "/profile");
        await new ThunderIDSignInPage(page).SignInAsync(username, password);
        await page.WaitForURLAsync(E2EApp.Url + "/profile");
    }

    protected static Task<string> ClaimAsync(IPage page, string type) => page.Locator($"#claim-{type}").InnerTextAsync();
}
