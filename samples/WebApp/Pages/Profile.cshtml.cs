using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WebApp.Pages;

[Authorize]
public class ProfileModel : PageModel
{
    public void OnGet()
    {
    }
}
