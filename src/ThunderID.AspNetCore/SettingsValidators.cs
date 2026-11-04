using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ThunderID.AspNetCore;

// Checked when the app starts, so a misconfigured app fails to start instead of failing on its
// first sign-in. These also refuse a security check turned off later through ASP.NET Core's own
// options, such as PostConfigure. Messages name the setting, never its value.
internal sealed class OpenIdConnectSettingsValidator : IValidateOptions<OpenIdConnectOptions>
{
    public ValidateOptionsResult Validate(string? name, OpenIdConnectOptions options)
    {
        if (name != OpenIdConnectDefaults.AuthenticationScheme)
        {
            return ValidateOptionsResult.Skip;
        }

        var failures = new List<string>();
        SettingsChecks.HttpsUrl(failures, "Authority", options.Authority);
        if (string.IsNullOrEmpty(options.ClientId))
        {
            failures.Add("ClientId is required.");
        }

        if (!options.UsePkce)
        {
            failures.Add("UsePkce must stay true; PKCE can't be turned off.");
        }

        if (options.ResponseType != OpenIdConnectResponseType.Code)
        {
            failures.Add("ResponseType must stay 'code'.");
        }

        if (!options.ProtocolValidator.RequireNonce)
        {
            failures.Add("ProtocolValidator.RequireNonce must stay true.");
        }

        SettingsChecks.TokenValidation(failures, options.TokenValidationParameters);
        return SettingsChecks.Result(failures);
    }
}

internal sealed class JwtBearerSettingsValidator : IValidateOptions<JwtBearerOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme)
        {
            return ValidateOptionsResult.Skip;
        }

        var failures = new List<string>();
        SettingsChecks.HttpsUrl(failures, "Authority", options.Authority);
        if (string.IsNullOrEmpty(options.Audience))
        {
            failures.Add("Audience is required.");
        }

        SettingsChecks.TokenValidation(failures, options.TokenValidationParameters);
        return SettingsChecks.Result(failures);
    }
}

internal static class SettingsChecks
{
    public static void HttpsUrl(List<string> failures, string setting, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            failures.Add($"{setting} is required.");
        }
        else if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add($"{setting} must be an absolute https URL.");
        }
    }

    public static void TokenValidation(List<string> failures, TokenValidationParameters parameters)
    {
        if (!parameters.ValidateIssuer)
        {
            failures.Add("TokenValidationParameters.ValidateIssuer must stay true.");
        }

        if (!parameters.ValidateAudience)
        {
            failures.Add("TokenValidationParameters.ValidateAudience must stay true.");
        }

        if (!parameters.ValidateLifetime)
        {
            failures.Add("TokenValidationParameters.ValidateLifetime must stay true.");
        }

        if (!parameters.RequireSignedTokens)
        {
            failures.Add("TokenValidationParameters.RequireSignedTokens must stay true.");
        }
    }

    public static ValidateOptionsResult Result(List<string> failures) => failures.Count == 0
        ? ValidateOptionsResult.Success
        : ValidateOptionsResult.Fail(failures.Select(failure => "ThunderID: " + failure));
}
