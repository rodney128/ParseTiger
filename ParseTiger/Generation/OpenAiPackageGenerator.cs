using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ParseTiger.Generation;

/// <summary>
/// Generates a package via OpenAI. Credentials remain in the user-scoped
/// provider settings store and are never copied into UI or diagnostic output.
/// </summary>
public sealed class OpenAiPackageGenerator : IPackageGenerator
{
    private readonly string _model;
    private readonly IProviderSettingsStore _settings;
    private readonly HttpClient _http;

    public OpenAiPackageGenerator(
        string model,
        IProviderSettingsStore settings,
        HttpClient? httpClient = null)
    {
        _model = model;
        _settings = settings;
        _http = httpClient ?? SharedHttp.Client;
    }

    public async Task<string> GenerateAsync(
        string request,
        string projectContext,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report("Credential load: reading OpenAI credential." +
            Environment.NewLine);
        string key = _settings.GetApiKey(GeneratorProvider.OpenAI) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                "OpenAI API key required. Open AI Provider Settings and add a key.");
        }

        bool wasVerified =
            _settings.IsApiKeyVerified(GeneratorProvider.OpenAI);
        progress?.Report(
            $"Credential load: loaded securely; " +
            (wasVerified ? "previously verified." : "verification pending.") +
            Environment.NewLine);
        progress?.Report(
            $"Request construction: building OpenAI request for {_model}." +
            Environment.NewLine);

        var payload = new
        {
            model = _model,
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = GenerationPrompts.System },
                new
                {
                    role = "user",
                    content = GenerationPrompts.BuildUser(request, projectContext)
                }
            }
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.openai.com/v1/chat/completions")
        {
            Content = content
        };
        httpRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", key);

        progress?.Report("Request sent: contacting OpenAI." + Environment.NewLine);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(httpRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderRequestException(
                ProviderFailureKind.Timeout,
                "OpenAI request timed out. Check your network connection and try again.",
                innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ProviderRequestException(
                ProviderFailureKind.Network,
                "OpenAI could not be reached. Check your network connection, firewall, " +
                "or proxy settings. " + exception.Message,
                innerException: exception);
        }

        using (response)
        {
            progress?.Report(
                $"Response received: HTTP {(int)response.StatusCode} " +
                $"({response.ReasonPhrase}).{Environment.NewLine}");
            string body = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw CreateApiException(response.StatusCode, body);
            }

            if (!wasVerified)
            {
                _settings.MarkApiKeyVerified(GeneratorProvider.OpenAI);
                progress?.Report(
                    "Credential verification: OpenAI accepted the saved credential; " +
                    "status is now Verified." + Environment.NewLine);
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                string package = (document.RootElement.GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? string.Empty).Trim();
                if (package.Length == 0)
                {
                    throw new ProviderRequestException(
                        ProviderFailureKind.ResponseParsing,
                        "OpenAI returned an empty package.");
                }

                progress?.Report(
                    $"Package received: {package.Length:N0} characters." +
                    Environment.NewLine);
                return package;
            }
            catch (JsonException exception)
            {
                throw new ProviderRequestException(
                    ProviderFailureKind.ResponseParsing,
                    "OpenAI returned a response that ParseTiger could not read: " +
                    exception.Message,
                    innerException: exception);
            }
        }
    }

    private static ProviderRequestException CreateApiException(
        HttpStatusCode statusCode,
        string body)
    {
        string message = TryReadErrorMessage(body);
        ProviderFailureKind kind = statusCode switch
        {
            HttpStatusCode.Unauthorized => ProviderFailureKind.InvalidCredential,
            HttpStatusCode.Forbidden => ProviderFailureKind.PermissionDenied,
            HttpStatusCode.NotFound => ProviderFailureKind.ModelUnavailable,
            HttpStatusCode.TooManyRequests => ProviderFailureKind.QuotaExceeded,
            _ => ProviderFailureKind.HttpFailure
        };
        string action = kind switch
        {
            ProviderFailureKind.InvalidCredential =>
                "Replace the saved OpenAI API key in AI Provider Settings.",
            ProviderFailureKind.PermissionDenied =>
                "Check the key's project, organization, and model permissions.",
            ProviderFailureKind.ModelUnavailable =>
                "Choose a model available to this OpenAI project.",
            ProviderFailureKind.QuotaExceeded =>
                "Check API credits, quota, and rate limits.",
            _ => "Check OpenAI service status and request settings."
        };

        return new ProviderRequestException(
            kind,
            $"OpenAI API returned HTTP {(int)statusCode}. {message} {action}",
            statusCode);
    }

    private static string TryReadErrorMessage(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out JsonElement error) &&
                error.TryGetProperty("message", out JsonElement message))
            {
                return message.GetString()?.Trim() ??
                    "The API rejected the request.";
            }
        }
        catch (JsonException)
        {
        }

        return string.IsNullOrWhiteSpace(body)
            ? "The API rejected the request without an error body."
            : body.Trim();
    }

    private static class SharedHttp
    {
        internal static readonly HttpClient Client = new()
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
    }
}
