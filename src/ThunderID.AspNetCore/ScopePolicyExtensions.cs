using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace ThunderID.AspNetCore;

public static class ScopePolicyExtensions
{
    /// <summary>
    /// Adds a policy that requires every listed scope. ThunderID sends <c>scope</c> as one
    /// space-delimited string, so <c>RequireClaim("scope", ...)</c> never matches it.
    /// </summary>
    public static AuthorizationBuilder AddScopePolicy(this AuthorizationBuilder builder, string name, params string[] scopes)
    {
        if (scopes.Length == 0)
        {
            throw new ArgumentException("At least one scope is required.", nameof(scopes));
        }

        return builder.AddPolicy(name, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context => HasScopes(context.User, scopes)));
    }

    internal static bool HasScopes(ClaimsPrincipal user, IEnumerable<string> required)
    {
        var granted = user.FindAll("scope")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
        return required.All(granted.Contains);
    }
}
