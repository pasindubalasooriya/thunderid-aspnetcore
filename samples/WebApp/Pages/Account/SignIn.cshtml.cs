using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WebApp.Pages.Account;

public class SignInModel : PageModel
{
    public IActionResult OnGet() =>
        Challenge(new AuthenticationProperties { RedirectUri = "/Profile" }, OpenIdConnectDefaults.AuthenticationScheme);
}
