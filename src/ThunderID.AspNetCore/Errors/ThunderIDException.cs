namespace ThunderID.AspNetCore;

/// <summary>
/// The base of every error ThunderID.AspNetCore raises or reports. Catch this type and switch on
/// <see cref="Code"/>, one of <see cref="ThunderIDErrorCodes"/>. Messages never contain a token,
/// a credential or personal data.
/// </summary>
public abstract class ThunderIDException : Exception
{
    /// <summary>The <see cref="Origin"/> of every error this package raises: its NuGet package ID.</summary>
    public const string PackageOrigin = "ThunderID.AspNetCore";

    private protected ThunderIDException(string code, string message, Exception? innerException)
        : base(message, innerException) => Code = code;

    /// <summary>A stable, machine-readable code from <see cref="ThunderIDErrorCodes"/>. A code never changes meaning.</summary>
    public string Code { get; }

    /// <summary>The package that raised the error.</summary>
    public string Origin => PackageOrigin;
}

/// <summary>ThunderID answered an HTTP request with an error.</summary>
public sealed class ThunderIDApiException(string code, string message, int statusCode, string? statusText, Exception? innerException = null)
    : ThunderIDException(code, message, innerException)
{
    /// <summary>The HTTP status code of ThunderID's answer.</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>The HTTP reason phrase of ThunderID's answer, when it sent one.</summary>
    public string? StatusText { get; } = statusText;
}

/// <summary>A failure that isn't an error answer from ThunderID: configuration, validation, or a network failure.</summary>
public sealed class ThunderIDRuntimeException(string code, string message, Exception? innerException = null)
    : ThunderIDException(code, message, innerException);
