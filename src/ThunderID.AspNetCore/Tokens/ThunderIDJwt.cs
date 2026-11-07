using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace ThunderID.AspNetCore;

/// <summary>Reads a JWT's claims for display or logging, without verifying it.</summary>
public static class ThunderIDJwt
{
    /// <summary>
    /// Reads the claims in a JWT's payload <b>without verifying anything</b>: not the signature, the
    /// issuer, the audience or the expiry. Anyone can create a token that decodes. Never use the result
    /// to decide who the caller is or what they may do; for that, validate the token with
    /// <c>AddThunderIDApi</c> or use the signed-in user's claims. Throws a
    /// <see cref="ThunderIDRuntimeException"/> with code <see cref="ThunderIDErrorCodes.JwtMalformed"/>
    /// when <paramref name="token"/> isn't a JWT with a JSON object payload.
    /// </summary>
    public static IReadOnlyDictionary<string, JsonElement> DecodeUnverified(string token)
    {
        // A signed JWT has three parts; an encrypted one has five and its payload can't be read.
        var parts = token?.Split('.');
        if (parts is not { Length: 3 } || parts[1].Length == 0)
        {
            throw Malformed();
        }

        try
        {
            using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[1]));
            if (payload.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Malformed();
            }

            return payload.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            // Not passed on as the inner exception: parser messages can quote the input.
            throw Malformed();
        }
    }

    private static ThunderIDRuntimeException Malformed() =>
        new(ThunderIDErrorCodes.JwtMalformed, "The value isn't a JWT with a JSON payload, so it couldn't be decoded.");
}
