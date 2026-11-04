using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ThunderID.AspNetCore.Tests;

/// <summary>Builds signed test tokens by hand, so each test controls exactly one header or claim.</summary>
internal static class LogoutTokens
{
    public const string Issuer = "https://localhost:8090";
    public const string ClientId = "WItzJCtJA0Ij77xkgHIjKg";
    public const string KeyId = "MT-DQ4FKOMHPyzw3pdRs_q8kxGkd1lezyLktNeNTv7w";

    // iat of the real logout token ThunderID sent in step 5 (2026-10-28T17:06:24Z).
    public const long IssuedAt = 1793207184;
    public static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(IssuedAt);

    public static readonly RSA SigningKey = RSA.Create(2048);
    public static readonly RSA OtherKey = RSA.Create(2048);

    // Exported once: exporting a key while parallel tests sign with it intermittently produced a
    // public key that failed to verify a correctly signed token (IDX10511).
    private static readonly RSAParameters SigningPublicKey = SigningKey.ExportParameters(false);
    private static readonly RSAParameters OtherPublicKey = OtherKey.ExportParameters(false);

    public static OpenIdConnectConfiguration Configuration()
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
        configuration.SigningKeys.Add(new RsaSecurityKey(SigningPublicKey) { KeyId = KeyId });
        configuration.IdTokenSigningAlgValuesSupported.Add("RS256");
        configuration.IdTokenSigningAlgValuesSupported.Add("ES256");
        return configuration;
    }

    /// <summary>Header and claims shaped exactly like ThunderID's step 5 logout token.</summary>
    public static (JsonObject Header, JsonObject Payload) Shape() => (
        new JsonObject { ["alg"] = "RS256", ["kid"] = KeyId, ["typ"] = "logout+jwt" },
        new JsonObject
        {
            ["aud"] = ClientId,
            ["events"] = new JsonObject { ["http://schemas.openid.net/event/backchannel-logout"] = new JsonObject() },
            ["exp"] = IssuedAt + 120,
            ["iat"] = IssuedAt,
            ["iss"] = Issuer,
            ["jti"] = "01a1837a-2b97-7ae9-8a16-c421cd2ae7bc",
            ["nbf"] = IssuedAt,
            ["sid"] = "01a18372-2364-7a2a-afe3-c0626b927850",
            ["sub"] = "01a182da-b10c-764b-bcc3-4bf3f93282de",
        });

    public static string Create(Action<JsonObject, JsonObject>? change = null, RSA? key = null)
    {
        var (header, payload) = Shape();
        change?.Invoke(header, payload);
        return Sign(header, payload, key ?? SigningKey);
    }

    public static string Sign(JsonObject header, JsonObject payload, RSA key)
    {
        var signingInput = $"{Encode(header)}.{Encode(payload)}";
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64UrlEncoder.Encode(signature)}";
    }

    public static string Unsigned(JsonObject header, JsonObject payload) => $"{Encode(header)}.{Encode(payload)}.";

    public static string Jwks(RSA key, string keyId)
    {
        var parameters = ReferenceEquals(key, SigningKey) ? SigningPublicKey
            : ReferenceEquals(key, OtherKey) ? OtherPublicKey
            : key.ExportParameters(false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA", use = "sig", alg = "RS256", kid = keyId,
                    n = Base64UrlEncoder.Encode(parameters.Modulus!),
                    e = Base64UrlEncoder.Encode(parameters.Exponent!),
                },
            },
        });
    }

    private static string Encode(JsonObject json) => Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(json.ToJsonString()));
}

internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
