using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ParseTiger.Generation;

/// <summary>Generates a package via the Anthropic (Claude) API. Needs ANTHROPIC_API_KEY.</summary>
public sealed class AnthropicPackageGenerator : IPackageGenerator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly string _model;

    public AnthropicPackageGenerator(string model) => _model = model;

    public async Task<string> GenerateAsync(
        string request,
        string projectContext,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report("Contacting Claude (" + _model + ")…" + Environment.NewLine);
        string key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                "Set the ANTHROPIC_API_KEY environment variable to use Claude. " +
                "This is a paid API key from console.anthropic.com — separate from a " +
                "Claude Pro subscription.");
        }

        var payload = new
        {
            model = _model,
            max_tokens = 4096,
            temperature = 0,
            system = GenerationPrompts.System,
            messages = new[]
            {
                new { role = "user", content = GenerationPrompts.BuildUser(request, projectContext) }
            }
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = content };
        httpRequest.Headers.Add("x-api-key", key);
        httpRequest.Headers.Add("anthropic-version", "2023-06-01");

        using HttpResponseMessage response =
            await Http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Claude error {(int)response.StatusCode}: {body}");
        }

        using JsonDocument document = JsonDocument.Parse(body);
        return (document.RootElement.GetProperty("content")[0]
            .GetProperty("text").GetString() ?? string.Empty).Trim();
    }
}
