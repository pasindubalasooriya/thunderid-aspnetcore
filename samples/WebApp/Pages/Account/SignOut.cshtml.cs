using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WebApp.Pages.Account;

public class SignOutModel : PageModel
{
    // The local cookie is cleared first: ThunderID redirects back before its back-channel
    // deliveries finish, so the app must not depend on them to end its own session.
    public IActionResult OnPost() =>
        SignOut(new AuthenticationProperties { RedirectUri = "/" },
            CookieAuthenticationDefaults.AuthenticationScheme,
            OpenIdConnectDefaults.AuthenticationScheme);
}
