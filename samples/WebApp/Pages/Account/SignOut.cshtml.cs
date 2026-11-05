using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ThunderID.AspNetCore;

namespace WebApp.Pages.Account;

public class SignOutModel : PageModel
{
    // Revokes the session's token at ThunderID, clears the local cookie, then redirects to
    // ThunderID's end-session endpoint. The cookie is cleared even when revocation fails.
    public async Task<IActionResult> OnPost()
    {
        var result = await HttpContext.SignOutOfThunderIDAsync("/");
        if (result.RevocationFailed)
        {
            TempData["Notice"] = "You're signed out of this app, but ThunderID couldn't be reached to revoke your session's token.";
        }

        // The sign-out has already written the redirect.
        return new EmptyResult();
    }
}
