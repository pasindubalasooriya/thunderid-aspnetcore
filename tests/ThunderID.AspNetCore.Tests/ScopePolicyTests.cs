using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace ThunderID.AspNetCore.Tests;

public class ScopePolicyTests
{
    private static async Task<bool> Authorize(string? scopeClaim, params string[] requiredScopes)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationBuilder().AddScopePolicy("test", requiredScopes);
        var authorization = services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();

        var claims = scopeClaim is null ? [] : new[] { new Claim("scope", scopeClaim) };
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Bearer"));
        return (await authorization.AuthorizeAsync(user, "test")).Succeeded;
    }

    [Fact]
    public async Task Single_scope_inside_space_delimited_claim_is_found() =>
        Assert.True(await Authorize("openid profile email billing:read", "billing:read"));

    [Fact]
    public async Task All_required_scopes_present_succeeds() =>
        Assert.True(await Authorize("billing:read billing:write", "billing:read", "billing:write"));

    [Fact]
    public async Task One_required_scope_missing_fails() =>
        Assert.False(await Authorize("billing:read", "billing:read", "billing:write"));

    [Fact]
    public async Task Missing_scope_claim_fails() =>
        Assert.False(await Authorize(null, "billing:read"));

    [Fact]
    public async Task Parent_permission_does_not_satisfy_child_scope() =>
        Assert.False(await Authorize("openid billing", "billing:read"));

    [Fact]
    public async Task Child_scope_does_not_satisfy_parent_permission() =>
        Assert.False(await Authorize("billing:read", "billing"));

    [Fact]
    public async Task Scope_match_is_case_sensitive() =>
        Assert.False(await Authorize("Billing:Read", "billing:read"));

    [Fact]
    public async Task Extra_spaces_are_ignored() =>
        Assert.True(await Authorize("  openid   billing:read ", "billing:read"));

    [Fact]
    public async Task Unauthenticated_user_fails_even_with_scope()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationBuilder().AddScopePolicy("test", "billing:read");
        var authorization = services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim("scope", "billing:read")]));

        Assert.False((await authorization.AuthorizeAsync(anonymous, "test")).Succeeded);
    }

    [Fact]
    public void Policy_without_scopes_is_rejected()
    {
        var ex = Assert.Throws<ThunderIDRuntimeException>(() => new ServiceCollection().AddAuthorizationBuilder().AddScopePolicy("test"));

        Assert.Equal(ThunderIDErrorCodes.ScopePolicyHasNoScopes, ex.Code);
    }
}
