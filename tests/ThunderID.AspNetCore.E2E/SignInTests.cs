namespace ThunderID.AspNetCore.E2E;

public sealed class SignInTests : E2ETest
{
    [Fact]
    public async Task Sign_in_reaches_the_protected_page_with_the_users_claims()
    {
        var page = await NewPageAsync();

        await SignInAsync(page);

        Assert.Equal(TestUser.Email, await ClaimAsync(page, "email"));
        Assert.Equal("E2E User", await ClaimAsync(page, "name"));
        Assert.NotEmpty(await ClaimAsync(page, "sub"));
        Assert.NotEmpty(await ClaimAsync(page, "sid"));
    }

    [Fact]
    public async Task Sign_up_creates_a_user_and_lands_signed_in()
    {
        var username = "e2e-" + Guid.NewGuid().ToString("N")[..12];
        var email = username + "@example.com";
        // TEST-ONLY password for a user that exists only in the throwaway container.
        const string password = "e2e-test-only-Signup1";
        var page = await NewPageAsync();

        await page.GotoAsync(E2EApp.Url + "/profile");
        await new ThunderIDSignInPage(page).SignUpAsync(username, password, email);
        await page.WaitForURLAsync(E2EApp.Url + "/profile");

        Assert.Equal(email, await ClaimAsync(page, "email"));

        // The account exists: a fresh browser signs in with it.
        var again = await NewPageAsync();
        await SignInAsync(again, username, password);
        Assert.Equal(email, await ClaimAsync(again, "email"));
    }
}
