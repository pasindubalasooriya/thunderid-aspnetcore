using System.Security.Claims;

namespace WebApp;

// Reads the profile claims under their JWT names (AddThunderID turns off claim type mapping).
public static class UserDisplay
{
    public static string? GivenName(ClaimsPrincipal user) => user.FindFirstValue("given_name");

    public static string? FamilyName(ClaimsPrincipal user) => user.FindFirstValue("family_name");

    public static string? Email(ClaimsPrincipal user) => user.FindFirstValue("email");

    public static string DisplayName(ClaimsPrincipal user) =>
        user.Identity?.Name
        ?? Email(user)
        ?? user.FindFirstValue("sub")
        ?? "Signed-in user";

    public static string Initials(ClaimsPrincipal user)
    {
        var given = GivenName(user);
        var family = FamilyName(user);
        if (!string.IsNullOrWhiteSpace(given) && !string.IsNullOrWhiteSpace(family))
        {
            return $"{char.ToUpperInvariant(given[0])}{char.ToUpperInvariant(family[0])}";
        }

        var letters = DisplayName(user).Where(char.IsLetter).Take(2).ToArray();
        return letters.Length == 0 ? "?" : new string(letters).ToUpperInvariant();
    }
}
