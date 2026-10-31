using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using ThunderID.AspNetCore.BackchannelLogout;

namespace ThunderID.AspNetCore.Tests;

public class LogoutTokenValidatorTests
{
    private static LogoutTokenValidator Validator(TimeProvider? time = null) => new(
        new StaticConfigurationManager<OpenIdConnectConfiguration>(LogoutTokens.Configuration()),
        LogoutTokens.ClientId,
        TimeSpan.FromMinutes(2),
        time ?? new ManualTimeProvider(LogoutTokens.Now));

    private static async Task<string?> Rejection(string token, TimeProvider? time = null)
    {
        var result = await Validator(time).ValidateAsync(token);
        Assert.False(result.IsValid);
        Assert.Null(result.Token);
        return result.Error;
    }

    [Fact]
    public async Task Token_shaped_like_thunderids_is_accepted()
    {
        var result = await Validator().ValidateAsync(LogoutTokens.Create());

        Assert.True(result.IsValid, result.Error);
        var token = result.Token!;
        Assert.Equal(LogoutTokens.Issuer, token.Issuer);
        Assert.Equal("01a18372-2364-7a2a-afe3-c0626b927850", token.SessionId);
        Assert.Equal("01a182da-b10c-764b-bcc3-4bf3f93282de", token.Subject);
        Assert.Equal("01a1837a-2b97-7ae9-8a16-c421cd2ae7bc", token.JwtId);
        Assert.Equal(LogoutTokens.Now, token.IssuedAt);
        Assert.Equal(LogoutTokens.Now.AddSeconds(120), token.ExpiresAt);
    }

    [Fact]
    public async Task Audience_array_containing_the_client_is_accepted()
    {
        var token = LogoutTokens.Create((_, p) => p["aud"] = new JsonArray("other-client", LogoutTokens.ClientId));

        Assert.True((await Validator().ValidateAsync(token)).IsValid);
    }

    [Fact]
    public async Task Sub_only_token_is_accepted()
    {
        var result = await Validator().ValidateAsync(LogoutTokens.Create((_, p) => p.Remove("sid")));

        Assert.True(result.IsValid, result.Error);
        Assert.Null(result.Token!.SessionId);
    }

    [Fact]
    public async Task Sid_only_token_is_accepted()
    {
        var result = await Validator().ValidateAsync(LogoutTokens.Create((_, p) => p.Remove("sub")));

        Assert.True(result.IsValid, result.Error);
        Assert.Null(result.Token!.Subject);
    }

    // 1. typ

    [Fact]
    public async Task Id_token_typ_is_rejected() =>
        Assert.Equal("typ is not logout+jwt", await Rejection(LogoutTokens.Create((h, _) => h["typ"] = "JWT")));

    [Fact]
    public async Task Missing_typ_is_rejected() =>
        Assert.Equal("typ is not logout+jwt", await Rejection(LogoutTokens.Create((h, _) => h.Remove("typ"))));

    [Fact]
    public async Task Media_type_form_of_typ_is_accepted() =>
        Assert.True((await Validator().ValidateAsync(LogoutTokens.Create((h, _) => h["typ"] = "application/logout+jwt"))).IsValid);

    // 2. signature

    [Fact]
    public async Task Signature_from_another_key_with_the_same_kid_is_rejected() =>
        Assert.Equal("signature is invalid", await Rejection(LogoutTokens.Create(key: LogoutTokens.OtherKey)));

    [Fact]
    public async Task Tampered_payload_is_rejected()
    {
        var parts = LogoutTokens.Create().Split('.');
        var forged = LogoutTokens.Create((_, p) => p["sub"] = "someone-else").Split('.')[1];

        Assert.Equal("signature is invalid", await Rejection($"{parts[0]}.{forged}.{parts[2]}"));
    }

    [Fact]
    public async Task Unknown_kid_is_rejected() =>
        Assert.Equal("signing key is unknown", await Rejection(LogoutTokens.Create((h, _) => h["kid"] = "rotated-away")));

    [Fact]
    public async Task Unsigned_alg_none_token_is_rejected()
    {
        var (header, payload) = LogoutTokens.Shape();
        header["alg"] = "none";

        Assert.NotNull(await Rejection(LogoutTokens.Unsigned(header, payload)));
    }

    [Fact]
    public async Task Algorithm_not_used_for_id_tokens_is_rejected()
    {
        var (header, payload) = LogoutTokens.Shape();
        header["alg"] = "RS512";

        Assert.NotNull(await Rejection(LogoutTokens.Sign(header, payload, LogoutTokens.SigningKey)));
    }

    // 3. issuer

    [Fact]
    public async Task Wrong_issuer_is_rejected() =>
        Assert.Equal("issuer does not match", await Rejection(LogoutTokens.Create((_, p) => p["iss"] = "https://evil.example")));

    [Fact]
    public async Task Issuer_with_trailing_slash_is_rejected() =>
        Assert.Equal("issuer does not match", await Rejection(LogoutTokens.Create((_, p) => p["iss"] = LogoutTokens.Issuer + "/")));

    // 4. audience

    [Fact]
    public async Task Token_for_another_client_is_rejected() =>
        Assert.Equal("audience does not contain the client id", await Rejection(LogoutTokens.Create((_, p) => p["aud"] = "another-app")));

    // 5. lifetime

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var later = new ManualTimeProvider(LogoutTokens.Now.AddSeconds(120) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));

        Assert.Equal("token is outside its lifetime", await Rejection(LogoutTokens.Create(), later));
    }

    [Fact]
    public async Task Token_just_inside_clock_skew_is_accepted()
    {
        var later = new ManualTimeProvider(LogoutTokens.Now.AddSeconds(120) + TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));

        Assert.True((await Validator(later).ValidateAsync(LogoutTokens.Create())).IsValid);
    }

    [Fact]
    public async Task Missing_exp_is_rejected() =>
        Assert.Equal("token is outside its lifetime", await Rejection(LogoutTokens.Create((_, p) => p.Remove("exp"))));

    [Fact]
    public async Task Iat_in_the_future_is_rejected() =>
        Assert.Equal("iat is in the future", await Rejection(LogoutTokens.Create((_, p) =>
        {
            p["iat"] = LogoutTokens.IssuedAt + 600;
            p["nbf"] = LogoutTokens.IssuedAt;
            p["exp"] = LogoutTokens.IssuedAt + 720;
        })));

    [Fact]
    public async Task Missing_iat_is_rejected() =>
        Assert.Equal("iat is missing", await Rejection(LogoutTokens.Create((_, p) => p.Remove("iat"))));

    // 6. events

    [Fact]
    public async Task Missing_events_is_rejected() =>
        Assert.Equal("events has no back-channel logout member", await Rejection(LogoutTokens.Create((_, p) => p.Remove("events"))));

    [Fact]
    public async Task Events_without_the_logout_member_is_rejected() =>
        Assert.Equal("events has no back-channel logout member", await Rejection(LogoutTokens.Create((_, p) =>
            p["events"] = new JsonObject { ["https://schemas.openid.net/secevent/risc/event-type/account-disabled"] = new JsonObject() })));

    [Fact]
    public async Task Logout_member_that_is_not_an_object_is_rejected() =>
        Assert.Equal("events has no back-channel logout member", await Rejection(LogoutTokens.Create((_, p) =>
            p["events"] = new JsonObject { [LogoutTokenValidator.BackchannelLogoutEvent] = true })));

    // 7. nonce

    [Fact]
    public async Task Nonce_is_rejected() =>
        Assert.Equal("nonce must not be present", await Rejection(LogoutTokens.Create((_, p) => p["nonce"] = "abc")));

    [Fact]
    public async Task Id_token_replayed_as_logout_token_is_rejected()
    {
        // What an ID token from step 4 looks like: typ JWT, a nonce, no events.
        var idToken = LogoutTokens.Create((h, p) =>
        {
            h["typ"] = "JWT";
            p.Remove("events");
            p["nonce"] = "639288034526283726.lt2OhjPW9UR4b4BNwJK3knFA---fUrevcd5c5KPCCiY";
            p["auth_time"] = LogoutTokens.IssuedAt;
        });

        Assert.NotNull(await Rejection(idToken));
    }

    // 8. sid or sub, and jti

    [Fact]
    public async Task Neither_sid_nor_sub_is_rejected() =>
        Assert.Equal("neither sid nor sub is present", await Rejection(LogoutTokens.Create((_, p) =>
        {
            p.Remove("sid");
            p.Remove("sub");
        })));

    [Fact]
    public async Task Empty_sid_and_sub_are_rejected() =>
        Assert.Equal("neither sid nor sub is present", await Rejection(LogoutTokens.Create((_, p) =>
        {
            p["sid"] = "";
            p["sub"] = "";
        })));

    [Fact]
    public async Task Missing_jti_is_rejected() =>
        Assert.Equal("jti is missing", await Rejection(LogoutTokens.Create((_, p) => p.Remove("jti"))));

    // Input shape

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Missing_token_is_rejected(string? token) =>
        Assert.Equal("logout_token is missing", (await Validator().ValidateAsync(token)).Error);

    [Fact]
    public async Task Garbage_is_rejected() =>
        Assert.Equal("logout_token is not a JWT", await Rejection("not-a-jwt"));

    [Fact]
    public async Task Oversized_token_is_rejected() =>
        Assert.Equal("logout_token is too large", await Rejection(new string('a', LogoutTokenValidator.MaxTokenLength + 1)));

    [Fact]
    public async Task Errors_never_contain_the_token()
    {
        var token = LogoutTokens.Create((_, p) => p["iss"] = "https://evil.example");
        var error = await Rejection(token);

        Assert.DoesNotContain(token.Split('.')[1], error);
        Assert.DoesNotContain("01a18372", error);
    }

    // Bounded JWKS refresh (SDK spec AC9.7)

    [Fact]
    public async Task Unknown_kids_cannot_force_a_jwks_fetch_per_request()
    {
        var retriever = new CountingRetriever(LogoutTokens.Jwks(LogoutTokens.SigningKey, LogoutTokens.KeyId));
        var manager = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{LogoutTokens.Issuer}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            retriever);
        var validator = new LogoutTokenValidator(manager, LogoutTokens.ClientId, timeProvider: new ManualTimeProvider(LogoutTokens.Now));

        for (var i = 0; i < 20; i++)
        {
            var token = LogoutTokens.Create((h, _) => h["kid"] = $"unknown-{i}", LogoutTokens.OtherKey);
            Assert.False((await validator.ValidateAsync(token)).IsValid);
        }

        await Task.Delay(200); // let any background refresh finish before counting
        Assert.InRange(retriever.JwksFetches, 1, 2);
    }

    private sealed class CountingRetriever(string jwks) : IDocumentRetriever
    {
        private int _jwksFetches;

        public int JwksFetches => Volatile.Read(ref _jwksFetches);

        public Task<string> GetDocumentAsync(string address, CancellationToken cancel)
        {
            if (address.EndsWith("/jwks", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _jwksFetches);
                return Task.FromResult(jwks);
            }

            return Task.FromResult($$"""{"issuer":"{{LogoutTokens.Issuer}}","jwks_uri":"{{LogoutTokens.Issuer}}/oauth2/jwks","id_token_signing_alg_values_supported":["RS256","ES256"]}""");
        }
    }
}
