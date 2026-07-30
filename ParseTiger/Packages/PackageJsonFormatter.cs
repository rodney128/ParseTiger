using System;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ParseTiger.Packages;

/// <summary>
/// Formats complete JSON documents without interpreting escape sequences inside
/// JSON string values. Invalid or incomplete input is returned untouched.
/// </summary>
public static class PackageJsonFormatter
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static bool TryFormat(string text, out string formatted)
    {
        formatted = text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text, DocumentOptions);
            string serialized = JsonSerializer.Serialize(
                document.RootElement,
                SerializerOptions);
            string newline = text.Contains("\r\n", StringComparison.Ordinal)
                ? "\r\n"
                : text.Contains('\n')
                    ? "\n"
                    : Environment.NewLine;
            formatted = newline == "\n"
                ? serialized
                : serialized.Replace("\n", newline, StringComparison.Ordinal);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
