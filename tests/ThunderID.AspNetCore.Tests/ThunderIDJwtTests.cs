using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;

namespace ThunderID.AspNetCore.Tests;

public sealed class ThunderIDJwtTests
{
    [Fact]
    public void Payload_claims_are_read_with_their_json_types()
    {
        var token = LogoutTokens.Sign(
            new JsonObject { ["alg"] = "RS256", ["typ"] = "at+jwt" },
            new JsonObject { ["sub"] = "u-1", ["exp"] = 1767225600, ["aud"] = new JsonArray("a", "b"), ["act"] = new JsonObject { ["sub"] = "svc" } },
            LogoutTokens.SigningKey);

        var claims = ThunderIDJwt.DecodeUnverified(token);

        Assert.Equal("u-1", claims["sub"].GetString());
        Assert.Equal(1767225600, claims["exp"].GetInt64());
        Assert.Equal(["a", "b"], claims["aud"].EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("svc", claims["act"].GetProperty("sub").GetString());
        Assert.Equal(4, claims.Count);
    }

    [Fact]
    public void Nothing_is_verified()
    {
        // Signed with a key nobody trusts, then given a garbage signature, and expired long ago.
        var signed = LogoutTokens.Sign(new JsonObject { ["alg"] = "RS256" }, new JsonObject { ["sub"] = "attacker", ["exp"] = 1 }, LogoutTokens.OtherKey);
        var forged = signed[..signed.LastIndexOf('.')] + ".not-a-signature";

        Assert.Equal("attacker", ThunderIDJwt.DecodeUnverified(forged)["sub"].GetString());
        Assert.Equal("attacker", ThunderIDJwt.DecodeUnverified(LogoutTokens.Unsigned(new JsonObject { ["alg"] = "none" }, new JsonObject { ["sub"] = "attacker" }))["sub"].GetString());
    }

    public static TheoryData<string?> NotJwts => new()
    {
        null,
        "",
        "secret-opaque-refresh-token",
        "a.b",
        "a..c",
        "a.b.c.d.e",
        $"h.{Base64UrlEncoder.Encode("[1,2]")}.s",
        $"h.{Base64UrlEncoder.Encode("\"secret-text\"")}.s",
        $"h.{Base64UrlEncoder.Encode("{\"sub\":\"secret-text\"")}.s",
        $"h.{Base64UrlEncoder.Encode("{\"sub\":\"x\",\"sub\":\"y\"}")}.s",
        "h.secret!!*.s",
    };

    [Theory]
    [MemberData(nameof(NotJwts))]
    public void Anything_else_is_a_coded_error_that_does_not_quote_the_input(string? value)
    {
        var error = Assert.Throws<ThunderIDRuntimeException>(() => ThunderIDJwt.DecodeUnverified(value!));

        Assert.Equal(ThunderIDErrorCodes.JwtMalformed, error.Code);
        Assert.Equal("The value isn't a JWT with a JSON payload, so it couldn't be decoded.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Fact]
    public void A_claim_value_is_independent_of_the_parsed_document()
    {
        var token = LogoutTokens.Unsigned(new JsonObject { ["alg"] = "none" }, new JsonObject { ["roles"] = new JsonArray("admin") });

        var claims = ThunderIDJwt.DecodeUnverified(token);
        GC.Collect();

        Assert.Equal("admin", claims["roles"][0].GetString());
        Assert.Equal(JsonValueKind.Array, claims["roles"].ValueKind);
        Assert.Equal("[\"admin\"]", Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(claims["roles"])));
    }
}
