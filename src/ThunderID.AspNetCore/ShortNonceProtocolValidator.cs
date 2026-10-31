using System.Globalization;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ThunderID.AspNetCore;

/// <summary>
/// ThunderID rejects a nonce longer than 64 characters; ASP.NET Core's default is about 115.
/// This keeps the default "&lt;ticks&gt;.&lt;random&gt;" shape, so the timestamp check still works,
/// with 32 random bytes, for 62 characters in total.
/// </summary>
internal sealed class ShortNonceProtocolValidator : OpenIdConnectProtocolValidator
{
    internal const int MaxNonceLength = 64;

    public ShortNonceProtocolValidator()
    {
        // The defaults OpenIdConnectOptions sets on its own validator.
        RequireStateValidation = false;
        NonceLifetime = TimeSpan.FromMinutes(15);
    }

    public override string GenerateNonce() =>
        DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + "." +
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
}
