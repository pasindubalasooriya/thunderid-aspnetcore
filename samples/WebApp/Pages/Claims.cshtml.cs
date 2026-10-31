using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WebApp.Pages;

[Authorize]
public class ClaimsModel : PageModel
{
    public string? IdTokenHeader { get; private set; }
    public string? IdTokenPayload { get; private set; }

    public async Task OnGetAsync()
    {
        var idToken = await HttpContext.GetTokenAsync("id_token");
        if (idToken is null)
        {
            return;
        }

        var parts = idToken.Split('.');
        IdTokenHeader = Pretty(parts[0]);
        IdTokenPayload = Pretty(parts[1]);
    }

    private static string Pretty(string base64Url)
    {
        var base64 = base64Url.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        return JsonSerializer.Serialize(JsonDocument.Parse(json), new JsonSerializerOptions { WriteIndented = true });
    }
}
