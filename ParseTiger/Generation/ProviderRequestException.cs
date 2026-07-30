using System.Net;

namespace ParseTiger.Generation;

public enum ProviderFailureKind
{
    InvalidCredential,
    PermissionDenied,
    ModelUnavailable,
    QuotaExceeded,
    HttpFailure,
    ResponseParsing,
    Network,
    Timeout,
    Unknown
}

/// <summary>A safe, user-actionable provider failure without credential data.</summary>
public sealed class ProviderRequestException : Exception
{
    public ProviderRequestException(
        ProviderFailureKind kind,
        string message,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public ProviderFailureKind Kind { get; }
    public HttpStatusCode? StatusCode { get; }
}
