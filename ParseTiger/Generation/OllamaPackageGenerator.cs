using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ParseTiger.Generation;

/// <summary>
/// Generates a package by asking a locally running Ollama model, streaming the
/// output so the UI can show progress. The result is never trusted directly — it
/// flows through the deterministic validator and exact-text executor.
/// </summary>
public sealed class OllamaPackageGenerator : IPackageGenerator
{
    private const string Endpoint = "http://localhost:11434";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    private readonly string _model;

    public OllamaPackageGenerator(string model) => _model = model;

    /// <summary>Lists locally available model names, or an empty list if Ollama is unreachable.</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync()
    {
        try
        {
            using HttpResponseMessage response =
                await Http.GetAsync(Endpoint + "/api/tags").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(body);
            var names = new List<string>();
            if (document.RootElement.TryGetProperty("models", out JsonElement models))
            {
                foreach (JsonElement model in models.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out JsonElement name) &&
                        name.GetString() is { Length: > 0 } value)
                    {
                        names.Add(value);
                    }
                }
            }

            return names;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }

    public async Task<string> GenerateAsync(
        string request,
        string projectContext,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request))
        {
            throw new InvalidOperationException("Describe the change you want first.");
        }

        var payload = new
        {
            model = _model,
            stream = true,
            format = "json",
            messages = new[]
            {
                new { role = "system", content = GenerationPrompts.System },
                new { role = "user", content = GenerationPrompts.BuildUser(request, projectContext) }
            }
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, Endpoint + "/api/chat") { Content = content };

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                "Could not reach Ollama at " + Endpoint +
                ". Is it running? (" + exception.Message + ")");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"The model \"{_model}\" returned an error: {error}");
            }

            using Stream stream = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var builder = new StringBuilder();
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)
                       .ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                using JsonDocument document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("message", out JsonElement message) &&
                    message.TryGetProperty("content", out JsonElement contentElement))
                {
                    string chunk = contentElement.GetString() ?? string.Empty;
                    if (chunk.Length > 0)
                    {
                        builder.Append(chunk);
                        progress?.Report(chunk);
                    }
                }

                if (document.RootElement.TryGetProperty("done", out JsonElement done) &&
                    done.ValueKind == JsonValueKind.True)
                {
                    break;
                }
            }

            return builder.ToString().Trim();
        }
    }
}
