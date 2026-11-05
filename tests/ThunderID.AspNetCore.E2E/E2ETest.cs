using Microsoft.Playwright;

namespace ThunderID.AspNetCore.E2E;

/// <summary>Starts the e2e app and a browser for each test. ThunderID must already be running (tests/e2e/thunderid).</summary>
public abstract class E2ETest : IAsyncLifetime
{
    // When set, every browser context records a Playwright trace here.
    private static readonly string? TraceDir = Environment.GetEnvironmentVariable("E2E_TRACE_DIR");

    private readonly List<IBrowserContext> _contexts = [];
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
        if (TraceDir is not null)
        {
            for (var i = 0; i < _contexts.Count; i++)
            {
                await _contexts[i].Tracing.StopAsync(new() { Path = Path.Combine(TraceDir, $"{GetType().Name}-{Guid.NewGuid():N}-{i}.zip") });
            }
        }

        await _browser!.DisposeAsync();
        _playwright!.Dispose();
        await App.DisposeAsync();
    }

    /// <summary>A page in a new browser context, so it shares no cookies with other pages.</summary>
    protected async Task<IPage> NewPageAsync()
    {
        var context = await _browser!.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        if (TraceDir is not null)
        {
            await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true });
        }

        _contexts.Add(context);
        return await context.NewPageAsync();
    }

    protected static async Task SignInAsync(IPage page, string username = TestUser.Username, string password = TestUser.Password)
    {
        await page.GotoAsync(E2EApp.Url + "/profile");
        await new ThunderIDSignInPage(page).SignInAsync(username, password);
        await page.WaitForURLAsync(E2EApp.Url + "/profile");
    }

    protected static Task<string> ClaimAsync(IPage page, string type) => page.Locator($"#claim-{type}").InnerTextAsync();
}
