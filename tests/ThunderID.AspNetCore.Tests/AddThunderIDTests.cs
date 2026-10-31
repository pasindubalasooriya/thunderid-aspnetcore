using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ThunderID.AspNetCore.Tests;

public class AddThunderIDTests
{
    private static OpenIdConnectOptions WebAppOptions(Action<ThunderIDOptions>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThunderID(options =>
        {
            options.Authority = "https://localhost:8090";
            options.ClientId = "client";
            options.ClientSecret = "secret";
            extra?.Invoke(options);
        });
        return services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);
    }

    [Fact]
    public void Uses_query_response_mode_and_code_flow_with_pkce()
    {
        var options = WebAppOptions();

        Assert.Equal("query", options.ResponseMode);
        Assert.Equal("code", options.ResponseType);
        Assert.True(options.UsePkce);
        Assert.True(options.SaveTokens);
    }

    [Fact]
    public void Keeps_jwt_claim_names()
    {
        var options = WebAppOptions();

        Assert.False(options.MapInboundClaims);
        Assert.Equal("name", options.TokenValidationParameters.NameClaimType);
    }

    [Fact]
    public void Requests_openid_profile_email_and_extra_scopes()
    {
        var options = WebAppOptions(o => o.Scopes.Add("billing:read"));

        Assert.Equal(["openid", "profile", "email", "billing:read"], options.Scope);
    }

    [Fact]
    public void Sends_resource_only_when_configured()
    {
        Assert.Null(WebAppOptions().Resource);
        Assert.Equal("https://localhost:7102", WebAppOptions(o => o.Resource = "https://localhost:7102").Resource);
    }

    [Fact]
    public void Nonce_fits_thunderid_limit_and_keeps_timestamp()
    {
        var validator = Assert.IsType<ShortNonceProtocolValidator>(WebAppOptions().ProtocolValidator);

        for (var i = 0; i < 100; i++)
        {
            var nonce = validator.GenerateNonce();
            Assert.True(nonce.Length <= ShortNonceProtocolValidator.MaxNonceLength, $"nonce was {nonce.Length} chars");
            var ticks = long.Parse(nonce[..nonce.IndexOf('.')]);
            Assert.InRange(new DateTime(ticks, DateTimeKind.Utc), DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        }
    }

    [Fact]
    public void Nonce_keeps_aspnet_defaults_for_state_and_lifetime()
    {
        var validator = WebAppOptions().ProtocolValidator;

        Assert.False(validator.RequireStateValidation);
        Assert.Equal(TimeSpan.FromMinutes(15), validator.NonceLifetime);
    }

    [Fact]
    public void Api_validates_audience_and_keeps_jwt_claim_names()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThunderIDApi(options =>
        {
            options.Authority = "https://localhost:8090";
            options.Audience = "https://localhost:7102";
        });
        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.Equal("https://localhost:7102", options.Audience);
        Assert.False(options.MapInboundClaims);
        Assert.Equal("sub", options.TokenValidationParameters.NameClaimType);
    }

    [Theory]
    [InlineData("alice", null, "alice")]
    [InlineData("alice", "smith", "alice smith")]
    [InlineData(null, "smith", "smith")]
    public void Name_falls_back_to_given_and_family_name(string? given, string? family, string expected)
    {
        var identity = new ClaimsIdentity();
        if (given is not null) identity.AddClaim(new Claim("given_name", given));
        if (family is not null) identity.AddClaim(new Claim("family_name", family));

        ThunderIDAuthenticationExtensions.AddNameFallback(identity);

        Assert.Equal(expected, identity.FindFirst("name")?.Value);
    }

    [Fact]
    public void Existing_name_is_kept()
    {
        var identity = new ClaimsIdentity([new Claim("name", "Alice A."), new Claim("given_name", "alice")]);

        ThunderIDAuthenticationExtensions.AddNameFallback(identity);

        Assert.Equal(["Alice A."], identity.FindAll("name").Select(c => c.Value));
    }

    [Fact]
    public void No_name_parts_adds_nothing()
    {
        var identity = new ClaimsIdentity([new Claim("email", "alice@example.com")]);

        ThunderIDAuthenticationExtensions.AddNameFallback(identity);

        Assert.Null(identity.FindFirst("name"));
    }
}
