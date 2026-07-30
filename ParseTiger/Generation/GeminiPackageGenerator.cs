using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ParseTiger.Generation;

/// <summary>
/// Generates a package via the Google Gemini API. Credentials come from the
/// user-scoped provider settings store (with environment variables as fallback).
/// </summary>
public sealed class GeminiPackageGenerator : IPackageGenerator
{
    private readonly string _model;
    private readonly IProviderSettingsStore _settings;
    private readonly HttpClient _http;

    public GeminiPackageGenerator(
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
        progress?.Report("Credential load: reading Gemini credential." +
            Environment.NewLine);
        string key = _settings.GetApiKey(GeneratorProvider.Gemini) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                "Gemini API key required. Open AI Provider Settings and add a key.");
        }

        bool wasVerified =
            _settings.IsApiKeyVerified(GeneratorProvider.Gemini);
        progress?.Report(
            $"Credential load: loaded securely; " +
            (wasVerified ? "previously verified." : "verification pending.") +
            Environment.NewLine);
        progress?.Report(
            $"Request construction: building Gemini request for {_model}." +
            Environment.NewLine);
        var payload = new
        {
            system_instruction = new
            {
                parts = new[] { new { text = GenerationPrompts.System } }
            },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[]
                    {
                        new
                        {
                            text = GenerationPrompts.BuildUser(
                                request,
                                projectContext)
                        }
                    }
                }
            },
            generationConfig = new
            {
                responseMimeType = "application/json"
            }
        };

        string url =
            $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent";
        using var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content
        };
        requestMessage.Headers.Add("x-goog-api-key", key);

        progress?.Report("Request sent: contacting Gemini." + Environment.NewLine);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(requestMessage, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderRequestException(
                ProviderFailureKind.Timeout,
                "Gemini request timed out. Check your network connection and try again.",
                innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ProviderRequestException(
                ProviderFailureKind.Network,
                "Gemini could not be reached. Check your network connection, firewall, " +
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
                _settings.MarkApiKeyVerified(GeneratorProvider.Gemini);
                progress?.Report(
                    "Credential verification: Gemini accepted the saved credential; " +
                    "status is now Verified." + Environment.NewLine);
            }

            string package = ExtractPackage(body);
            progress?.Report(
                $"Package received: {package.Length:N0} characters." +
                Environment.NewLine);
            return package;
        }
    }

    private static ProviderRequestException CreateApiException(
        HttpStatusCode statusCode,
        string body)
    {
        string apiMessage = TryReadErrorMessage(body);
        ProviderFailureKind kind;
        string action;
        if (apiMessage.Contains("API key not valid", StringComparison.OrdinalIgnoreCase) ||
            statusCode == HttpStatusCode.Unauthorized)
        {
            kind = ProviderFailureKind.InvalidCredential;
            action = "The saved Gemini key was rejected. Open AI Provider Settings, " +
                "replace it with a valid Google AI Studio key, and try again.";
        }
        else if (statusCode == HttpStatusCode.Forbidden)
        {
            kind = ProviderFailureKind.PermissionDenied;
            action = "The Gemini key lacks permission for this request or model. " +
                "Check key restrictions and model access.";
        }
        else if (statusCode == HttpStatusCode.TooManyRequests)
        {
            kind = ProviderFailureKind.QuotaExceeded;
            action = "Gemini rate limit or quota was reached. Wait, check quota, " +
                "or choose another eligible model.";
        }
        else if (statusCode == HttpStatusCode.NotFound ||
                 apiMessage.Contains("model", StringComparison.OrdinalIgnoreCase))
        {
            kind = ProviderFailureKind.ModelUnavailable;
            action = "The selected Gemini model is unavailable to this key. " +
                "Choose an available model in AI Provider Settings.";
        }
        else
        {
            kind = ProviderFailureKind.HttpFailure;
            action = "Check Gemini service status, the selected model, and request limits.";
        }

        return new ProviderRequestException(
            kind,
            $"Gemini API returned HTTP {(int)statusCode}. {apiMessage} {action}",
            statusCode);
    }

    private static string TryReadErrorMessage(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty(
                    "error",
                    out JsonElement error) &&
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

    private static string ExtractPackage(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty(
                    "candidates",
                    out JsonElement candidates) ||
                candidates.ValueKind != JsonValueKind.Array ||
                candidates.GetArrayLength() == 0)
            {
                throw new ProviderRequestException(
                    ProviderFailureKind.ResponseParsing,
                    "Gemini returned no package candidate. The response may have " +
                    "been blocked or stopped before producing content.");
            }

            JsonElement candidate = candidates[0];
            if (!candidate.TryGetProperty(
                    "content",
                    out JsonElement candidateContent) ||
                !candidateContent.TryGetProperty(
                    "parts",
                    out JsonElement parts) ||
                parts.ValueKind != JsonValueKind.Array ||
                parts.GetArrayLength() == 0 ||
                !parts[0].TryGetProperty("text", out JsonElement text))
            {
                string finishReason = candidate.TryGetProperty(
                    "finishReason",
                    out JsonElement reason)
                    ? reason.GetString() ?? "unknown"
                    : "unknown";
                throw new ProviderRequestException(
                    ProviderFailureKind.ResponseParsing,
                    $"Gemini returned no package text (finish reason: {finishReason}).");
            }

            string package = text.GetString()?.Trim() ?? string.Empty;
            if (package.Length == 0)
            {
                throw new ProviderRequestException(
                    ProviderFailureKind.ResponseParsing,
                    "Gemini returned an empty package.");
            }

            return package;
        }
        catch (JsonException exception)
        {
            throw new ProviderRequestException(
                ProviderFailureKind.ResponseParsing,
                "Gemini returned a response that ParseTiger could not read: " +
                exception.Message,
                innerException: exception);
        }
    }

    private static class SharedHttp
    {
        internal static readonly HttpClient Client = new()
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
    }
}
