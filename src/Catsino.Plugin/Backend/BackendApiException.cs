using System.Net;
using Catsino.Plugin.Contracts;
using Catsino.Plugin.Security;

namespace Catsino.Plugin.Backend;

public sealed class BackendApiException(HttpStatusCode statusCode, ApiErrorDto error)
    : Exception(SecretRedactor.Redact(error.Message))
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string ErrorCode { get; } = error.Code;

    public IReadOnlyDictionary<string, string[]>? ValidationErrors { get; } = error.ValidationErrors;
}
