using System.Text.Json;
using ParseTiger.Models;

namespace ParseTiger.Packages;

/// <summary>
/// Turns raw package JSON into a <see cref="Package"/>. This is a purely
/// syntactic parse: it fails only on malformed JSON. Semantic checks (required
/// fields, allowed operation types, path safety) belong to Phase 3 validation.
/// </summary>
public sealed class PackageParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public Package Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new FormatException("The package is empty.");
        }

        Package? package;
        try
        {
            package = JsonSerializer.Deserialize<Package>(json, Options);
        }
        catch (JsonException exception)
        {
            throw new FormatException(
                "The package is not valid JSON: " + exception.Message,
                exception);
        }

        if (package is null)
        {
            throw new FormatException("The package could not be read.");
        }

        // A missing or explicitly-null operations array parses to an empty list,
        // so later phases can rely on a non-null collection.
        package.Operations ??= new();
        return package;
    }
}
