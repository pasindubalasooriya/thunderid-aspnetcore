using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ThunderID.AspNetCore;

// A failed sign-in callback reaches the app as ASP.NET Core's AuthenticationFailureException.
// These make its InnerException a ThunderIDException, so the app can switch on a code.
internal static class SignInFailures
{
    private static readonly object StatusKey = new();

    // Wraps the handler the OIDC backchannel is built from, so a refused token request's status can
    // be told to the failure below. Must run before ASP.NET Core's post-configure builds Backchannel;
    // an app that sets Backchannel itself gets the failure without a status.
    internal sealed class CaptureTokenStatus(IHttpContextAccessor accessor) : IPostConfigureOptions<OpenIdConnectOptions>
    {
        public void PostConfigure(string? name, OpenIdConnectOptions options)
        {
            if (name == OpenIdConnectDefaults.AuthenticationScheme && options.Backchannel is null)
            {
                options.BackchannelHttpHandler = new StatusRecorder(accessor) { InnerHandler = options.BackchannelHttpHandler ?? new HttpClientHandler() };
            }
        }
    }

    // Runs ahead of any OnRemoteFailure the app sets, which then sees the ThunderID error.
    internal sealed class WrapRemoteFailure : IPostConfigureOptions<OpenIdConnectOptions>
    {
        public void PostConfigure(string? name, OpenIdConnectOptions options)
        {
            if (name != OpenIdConnectDefaults.AuthenticationScheme)
            {
                return;
            }

            var app = options.Events.OnRemoteFailure;
            options.Events.OnRemoteFailure = context =>
            {
                context.Failure = ToThunderIDException(context.HttpContext, context.Failure);
                return app(context);
            };
        }
    }

    internal static ThunderIDException? ToThunderIDException(HttpContext context, Exception? failure)
    {
        if (failure is null or ThunderIDException)
        {
            return failure as ThunderIDException;
        }

        // ThunderID's OAuth error code (such as invalid_grant), never its description or the request.
        var error = failure.Data["error"] as string;
        if (context.Request.Query.ContainsKey("error"))
        {
            return new ThunderIDRuntimeException(ThunderIDErrorCodes.SignInErrorResponse,
                $"ThunderID returned an error to the sign-in callback: {error ?? "unknown"}.", failure);
        }

        var message = $"ThunderID's token endpoint refused the authorization code: {error ?? "unknown"}.";
        if (context.Items[StatusKey] is (int status, string reason))
        {
            return new ThunderIDApiException(ThunderIDErrorCodes.SignInTokenRequestRejected, message, status, reason, failure);
        }

        if (error is not null)
        {
            return new ThunderIDRuntimeException(ThunderIDErrorCodes.SignInTokenRequestRejected, message, failure);
        }

        return new ThunderIDRuntimeException(ThunderIDErrorCodes.SignInCallbackInvalid,
            "The sign-in callback failed a check: state, correlation, nonce or ID token validation.", failure);
    }

    private sealed class StatusRecorder(IHttpContextAccessor accessor) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.Method == HttpMethod.Post && !response.IsSuccessStatusCode && accessor.HttpContext is { } context)
            {
                context.Items[StatusKey] = ((int)response.StatusCode, response.ReasonPhrase ?? "");
            }

            return response;
        }
    }
}
