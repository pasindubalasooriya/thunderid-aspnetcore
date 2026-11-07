using System.Reflection;
using System.Text.RegularExpressions;

namespace ThunderID.AspNetCore.Tests;

public sealed class ErrorModelTests
{
    private static readonly Dictionary<string, string> Codes = typeof(ThunderIDErrorCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral)
        .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void Runtime_error_carries_its_code_origin_message_and_cause()
    {
        var cause = new IOException("connection reset");
        var error = new ThunderIDRuntimeException(ThunderIDErrorCodes.RevocationUnreachable, "ThunderID's revocation endpoint could not be reached.", cause);

        Assert.Equal("ASPNETCORE-SIGN_OUT-REV-NE01", error.Code);
        Assert.Equal("ThunderID.AspNetCore", error.Origin);
        Assert.Equal("ThunderID's revocation endpoint could not be reached.", error.Message);
        Assert.Same(cause, error.InnerException);
    }

    [Fact]
    public void Api_error_also_carries_the_status_code_and_text()
    {
        var error = new ThunderIDApiException(ThunderIDErrorCodes.RevocationRejected, "ThunderID refused to revoke the token.", 400, "Bad Request");

        Assert.Equal(ThunderIDErrorCodes.RevocationRejected, error.Code);
        Assert.Equal(ThunderIDException.PackageOrigin, error.Origin);
        Assert.Equal(400, error.StatusCode);
        Assert.Equal("Bad Request", error.StatusText);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Both_kinds_are_caught_as_ThunderIDException(bool api)
    {
        Exception thrown = api
            ? new ThunderIDApiException(ThunderIDErrorCodes.SignInTokenRequestRejected, "m", 400, null)
            : new ThunderIDRuntimeException(ThunderIDErrorCodes.NotRegistered, "m");

        try
        {
            throw thrown;
        }
        catch (ThunderIDException caught)
        {
            Assert.Same(thrown, caught);
        }
    }

    [Fact]
    public void Every_code_is_unique_and_in_the_shared_format()
    {
        Assert.NotEmpty(Codes);
        Assert.Equal(Codes.Count, Codes.Values.Distinct(StringComparer.Ordinal).Count());
        Assert.All(Codes.Values, code => Assert.Matches(new Regex("^[A-Z]+-[A-Z_]+-[A-Z0-9]+-(IV|NF|HE|NE)[0-9]{2}$"), code));
    }

    [Fact]
    public void Only_codes_shared_with_the_javascript_sdks_lack_the_aspnetcore_prefix()
    {
        // Each reused code names the same condition in @thunderid/javascript or @thunderid/node (javascript-sdks f9be4b0).
        var reused = new Dictionary<string, string>
        {
            [nameof(ThunderIDErrorCodes.LogoutTokenReplayed)] = "NODE-AUTH_CLIENT-HBL-IV01",
            [nameof(ThunderIDErrorCodes.LogoutTokenMalformed)] = "JS-AUTH_HELPER-VLT-IV01",
            [nameof(ThunderIDErrorCodes.LogoutTokenType)] = "JS-LOGOUT_TOKEN-VLTC-IV02",
            [nameof(ThunderIDErrorCodes.LogoutTokenAlgorithm)] = "JS-LOGOUT_TOKEN-VLTC-IV03",
            [nameof(ThunderIDErrorCodes.LogoutTokenNonce)] = "JS-LOGOUT_TOKEN-VLTC-IV04",
            [nameof(ThunderIDErrorCodes.LogoutTokenEvent)] = "JS-LOGOUT_TOKEN-VLTC-IV05",
            [nameof(ThunderIDErrorCodes.LogoutTokenIssuer)] = "JS-LOGOUT_TOKEN-VLTC-IV06",
            [nameof(ThunderIDErrorCodes.LogoutTokenAudience)] = "JS-LOGOUT_TOKEN-VLTC-IV07",
            [nameof(ThunderIDErrorCodes.LogoutTokenIssuedAt)] = "JS-LOGOUT_TOKEN-VLTC-IV08",
            [nameof(ThunderIDErrorCodes.LogoutTokenExpired)] = "JS-LOGOUT_TOKEN-VLTC-IV09",
            [nameof(ThunderIDErrorCodes.LogoutTokenNoSessionOrSubject)] = "JS-LOGOUT_TOKEN-VLTC-IV11",
            [nameof(ThunderIDErrorCodes.LogoutTokenJwtIdMissing)] = "JS-LOGOUT_TOKEN-VLTC-IV12",
            [nameof(ThunderIDErrorCodes.LogoutTokenSignature)] = "JS-CRYPTO_HELPER-VLTS-IV01",
        };

        Assert.Equal(reused, Codes.Where(c => !c.Value.StartsWith("ASPNETCORE-", StringComparison.Ordinal)).ToDictionary());
    }
}
