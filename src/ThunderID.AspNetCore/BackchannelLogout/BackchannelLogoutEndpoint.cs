using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace ThunderID.AspNetCore.BackchannelLogout;

public static class BackchannelLogoutEndpoint
{
    // A form body with one logout token is well under 2 KB.
    internal const int MaxBodyBytes = 16 * 1024;

    /// <summary>
    /// Receives ThunderID's back-channel logout notifications at <paramref name="pattern"/>, which
    /// must match the back-channel logout URI registered for the application.
    /// </summary>
    public static IEndpointConventionBuilder MapThunderIDBackchannelLogout(this IEndpointRouteBuilder endpoints, string pattern = "/backchannel-logout") =>
        // Passed as a Delegate so the returned IResult is written; the RequestDelegate overload would drop it.
        endpoints.MapPost(pattern, (Func<HttpContext, Task<IResult>>)HandleAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .ExcludeFromDescription();

    internal static async Task<IResult> HandleAsync(HttpContext context)
    {
        // Every answer, including errors, must not be cached.
        context.Response.Headers.CacheControl = "no-store";

        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(BackchannelLogoutEndpoint));

        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return Reject(logger, "content type is not application/x-www-form-urlencoded");
        }

        var body = await ReadCappedAsync(context.Request, context.RequestAborted);
        if (body is null)
        {
            return Reject(logger, "body is too large");
        }

        var form = QueryHelpers.ParseQuery(body);
        if (!form.TryGetValue("logout_token", out var values) || values.Count != 1)
        {
            return Reject(logger, "body must carry exactly one logout_token");
        }

        var validator = context.RequestServices.GetRequiredService<LogoutTokenValidator>();
        var result = await validator.ValidateAsync(values[0], context.RequestAborted);
        if (!result.IsValid)
        {
            return Reject(logger, result.Error!);
        }

        var token = result.Token!;
        bool recorded;
        try
        {
            var store = context.RequestServices.GetRequiredService<ILogoutSessionStore>();
            recorded = await store.TryRecordLogoutAsync(token, context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A 5xx tells ThunderID to retry the delivery.
            logger.LogError(ex, "Back-channel logout could not be recorded for sid {SessionId}", token.SessionId);
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }

        if (!recorded)
        {
            return Reject(logger, "jti was already used");
        }

        logger.LogInformation("Back-channel logout recorded: sid {SessionId}, sub {Subject}", token.SessionId, token.Subject);
        return Results.Ok();
    }

    private static IResult Reject(ILogger logger, string reason)
    {
        logger.LogWarning("Back-channel logout rejected: {Reason}", reason);
        return Results.BadRequest(new { error = "invalid_request", error_description = reason });
    }

    // Returns null when the body is larger than the cap, without reading the rest of it.
    private static async Task<string?> ReadCappedAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await request.Body.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0)
        {
            total += read;
        }

        return total > MaxBodyBytes ? null : System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }
}
