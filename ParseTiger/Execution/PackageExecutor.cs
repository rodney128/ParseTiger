using System.IO;
using ParseTiger.Models;
using ParseTiger.State;

namespace ParseTiger.Execution;

/// <summary>
/// Executes a validated <see cref="Package"/> against a project folder.
/// <see cref="Preview"/> (Phase 4) computes the exact before/after for each
/// operation without writing anything; <see cref="Apply"/> (Phase 5) writes.
/// </summary>
public sealed class PackageExecutor
{
    public PackagePreview Preview(Package package, string projectFolder)
    {
        ArgumentNullException.ThrowIfNull(package);
        var preview = new PackagePreview();
        for (int index = 0; index < package.Operations.Count; index++)
        {
            preview.Operations.Add(PreviewOperation(
                package.Operations[index],
                projectFolder,
                index + 1));
        }

        return preview;
    }

    public PackagePreview Preview(Package package, ProjectStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(snapshot);
        var preview = new PackagePreview();
        for (int index = 0; index < package.Operations.Count; index++)
        {
            preview.Operations.Add(PreviewOperation(
                package.Operations[index],
                snapshot,
                index + 1));
        }

        return preview;
    }

    public ExecutionResult Apply(Package package, string projectFolder)
    {
        ArgumentNullException.ThrowIfNull(package);

        // Re-verify every operation before writing anything (all-or-nothing).
        PackagePreview preview = Preview(package, projectFolder);
        if (!preview.CanApply)
        {
            string reason = preview.Operations.Count == 0
                ? "The package contains no operations."
                : string.Join(
                    " | ",
                    preview.Operations
                        .Where(operation => !operation.Applicable)
                        .Select(operation => $"{operation.Path}: {operation.Error}"));
            return new ExecutionResult
            {
                Succeeded = false,
                Message = "Nothing was applied. " + reason,
                FailedOperationNumber = preview.Operations
                    .FirstOrDefault(operation => !operation.Applicable)?.Number,
                FailedPath = preview.Operations
                    .FirstOrDefault(operation => !operation.Applicable)?.Path,
                MatchCount = preview.Operations
                    .FirstOrDefault(operation => !operation.Applicable)?.MatchCount ?? 0,
                ExpectedOldText = preview.Operations
                    .FirstOrDefault(operation => !operation.Applicable)?.Before ?? string.Empty,
                NearbyExcerpt = preview.Operations
                    .FirstOrDefault(operation => !operation.Applicable)?.NearbyExcerpt ?? string.Empty
            };
        }

        int applied = 0;
        foreach (Operation operation in package.Operations)
        {
            string fullPath = ResolveInside(projectFolder, operation.Path!);
            string content = File.ReadAllText(fullPath);
            string oldText = operation.OldText ?? string.Empty;
            TextMatch match = FindUniqueMatch(content, oldText);

            // Re-check uniqueness at write time in case the file changed.
            if (!match.IsUnique)
            {
                return new ExecutionResult
                {
                    Succeeded = false,
                    Message = $"Aborted at \"{operation.Path}\": the old text is no " +
                        $"longer a unique match. {applied} change(s) were already written.",
                    FailedOperationNumber = applied + 1,
                    FailedPath = operation.Path,
                    MatchCount = match.Count,
                    ExpectedOldText = oldText,
                    NearbyExcerpt = FindNearbyExcerpt(content, oldText)
                };
            }

            string replacement = AdaptNewLines(
                operation.NewText ?? string.Empty,
                content);
            string updated = content[..match.Start] +
                replacement +
                content[match.End..];
            File.WriteAllText(fullPath, updated);
            applied++;
        }

        return new ExecutionResult
        {
            Succeeded = true,
            Message = $"Applied {applied} operation(s) successfully."
        };
    }

    public ExecutionResult Apply(Package package, ProjectStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(snapshot);

        PackagePreview preview = Preview(package, snapshot);
        if (!preview.CanApply)
        {
            OperationPreview? failed =
                preview.Operations.FirstOrDefault(operation => !operation.Applicable);
            return new ExecutionResult
            {
                Succeeded = false,
                Message = "Nothing was applied. " +
                    (failed?.Error ?? "The package contains no operations."),
                FailedOperationNumber = failed?.Number,
                FailedPath = failed?.Path,
                MatchCount = failed?.MatchCount ?? 0,
                ExpectedOldText = failed?.Before ?? string.Empty,
                NearbyExcerpt = failed?.NearbyExcerpt ?? string.Empty
            };
        }

        var updated = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < package.Operations.Count; index++)
        {
            Operation operation = package.Operations[index];
            string relative = NormalizeRelative(operation.Path!);
            if (!snapshot.Files.TryGetValue(relative, out byte[]? originalBytes))
            {
                return Failed(
                    index + 1,
                    operation,
                    0,
                    "The target file is not present in the run context snapshot.",
                    string.Empty);
            }

            string content = updated.TryGetValue(relative, out string? prior)
                ? prior
                : DecodeText(originalBytes);
            string oldText = operation.OldText ?? string.Empty;
            TextMatch match = FindUniqueMatch(content, oldText);
            if (!match.IsUnique)
            {
                return Failed(
                    index + 1,
                    operation,
                    match.Count,
                    "The old text is no longer a unique match in the " +
                    "context-derived working content.",
                    FindNearbyExcerpt(content, oldText));
            }

            string replacement = AdaptNewLines(
                operation.NewText ?? string.Empty,
                content);
            updated[relative] = content[..match.Start] +
                replacement +
                content[match.End..];
        }

        // Recheck the exact bytes supplied to the AI before any write occurs.
        foreach (string relative in updated.Keys)
        {
            string fullPath = ResolveInside(snapshot.Identity.ProjectRoot, relative);
            if (!File.Exists(fullPath) ||
                !File.ReadAllBytes(fullPath).SequenceEqual(snapshot.Files[relative]))
            {
                Operation operation = package.Operations.First(item =>
                    NormalizeRelative(item.Path!).Equals(
                        relative,
                        StringComparison.OrdinalIgnoreCase));
                return Failed(
                    package.Operations.IndexOf(operation) + 1,
                    operation,
                    0,
                    "The file changed after the AI context was created. " +
                    "Nothing was applied.",
                    string.Empty);
            }
        }

        foreach ((string relative, string content) in updated)
        {
            File.WriteAllText(
                ResolveInside(snapshot.Identity.ProjectRoot, relative),
                content);
        }

        return new ExecutionResult
        {
            Succeeded = true,
            Message = $"Applied {package.Operations.Count} operation(s) " +
                $"from context snapshot {snapshot.Id} successfully."
        };
    }

    private static OperationPreview PreviewOperation(
        Operation operation,
        string projectFolder,
        int number)
    {
        var result = new OperationPreview
        {
            Number = number,
            Path = operation.Path ?? string.Empty,
            Type = operation.Type ?? string.Empty,
            Before = operation.OldText ?? string.Empty,
            OldTextLength = operation.OldText?.Length ?? 0
        };

        if (string.IsNullOrWhiteSpace(operation.Path))
        {
            result.Error = "Missing path.";
            return result;
        }

        string fullPath;
        try
        {
            fullPath = ResolveInside(projectFolder, operation.Path);
        }
        catch (ArgumentException exception)
        {
            result.Error = exception.Message;
            return result;
        }

        if (!File.Exists(fullPath))
        {
            result.Error = "File not found.";
            return result;
        }

        string content = File.ReadAllText(fullPath);
        string oldText = operation.OldText ?? string.Empty;
        TextMatch match = FindUniqueMatch(content, oldText);
        result.MatchCount = match.Count;
        if (oldText.Length == 0 || result.MatchCount == 0)
        {
            result.Error = "The old text was not found in the file.";
            result.NearbyExcerpt = FindNearbyExcerpt(content, oldText);
            return result;
        }

        if (result.MatchCount != 1)
        {
            result.Error =
                $"The old text occurs {result.MatchCount} times; exactly one match is required.";
            result.NearbyExcerpt = FindNearbyExcerpt(content, oldText);
            return result;
        }

        result.Before = oldText;
        result.After = operation.NewText ?? string.Empty;
        result.Applicable = true;
        return result;
    }

    private static OperationPreview PreviewOperation(
        Operation operation,
        ProjectStateSnapshot snapshot,
        int number)
    {
        var result = new OperationPreview
        {
            Number = number,
            Path = operation.Path ?? string.Empty,
            Type = operation.Type ?? string.Empty,
            Before = operation.OldText ?? string.Empty,
            OldTextLength = operation.OldText?.Length ?? 0
        };
        if (string.IsNullOrWhiteSpace(operation.Path))
        {
            result.Error = "Missing path.";
            return result;
        }

        string relative = NormalizeRelative(operation.Path);
        if (!snapshot.Files.TryGetValue(relative, out byte[]? bytes))
        {
            result.Error = "File not found in the run context snapshot.";
            return result;
        }

        string content = DecodeText(bytes);
        string oldText = operation.OldText ?? string.Empty;
        TextMatch match = FindUniqueMatch(content, oldText);
        result.MatchCount = match.Count;
        if (oldText.Length == 0 || result.MatchCount == 0)
        {
            result.Error = "The old text was not found in the context snapshot.";
            result.NearbyExcerpt = FindNearbyExcerpt(content, oldText);
            return result;
        }

        if (result.MatchCount != 1)
        {
            result.Error =
                $"The old text occurs {result.MatchCount} times in the context " +
                "snapshot; exactly one match is required.";
            result.NearbyExcerpt = FindNearbyExcerpt(content, oldText);
            return result;
        }

        result.After = operation.NewText ?? string.Empty;
        result.Applicable = true;
        return result;
    }

    private static ExecutionResult Failed(
        int number,
        Operation operation,
        int matchCount,
        string message,
        string nearby) => new()
    {
        Succeeded = false,
        Message = message,
        FailedOperationNumber = number,
        FailedPath = operation.Path,
        MatchCount = matchCount,
        ExpectedOldText = operation.OldText ?? string.Empty,
        NearbyExcerpt = nearby
    };

    private static string NormalizeRelative(string path) =>
        path.Replace('\\', '/');

    private static string DecodeText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(
            stream,
            System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static int CountOccurrences(string content, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int count = 0;
        int position = 0;
        while ((position = content.IndexOf(
                    text,
                    position,
                    StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += text.Length;
        }
        return count;
    }

    /// <summary>
    /// Finds an exact unique match first. If none exists, accepts a unique match
    /// that differs only by CRLF versus LF line endings. AI JSON commonly uses
    /// LF while Visual Studio files on Windows use CRLF; treating those as the
    /// same keeps matching deterministic without ignoring whitespace or content.
    /// </summary>
    private static TextMatch FindUniqueMatch(string content, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return TextMatch.None;
        }

        int exactCount = CountOccurrences(content, text);
        if (exactCount == 1)
        {
            int start = content.IndexOf(text, StringComparison.Ordinal);
            return new TextMatch(1, start, start + text.Length);
        }
        if (exactCount > 1)
        {
            return new TextMatch(exactCount, -1, -1);
        }

        string normalizedContent = NormalizeNewLines(content);
        string normalizedText = NormalizeNewLines(text);
        int normalizedCount = CountOccurrences(normalizedContent, normalizedText);
        if (normalizedCount != 1)
        {
            return new TextMatch(normalizedCount, -1, -1);
        }

        int normalizedStart = normalizedContent.IndexOf(
            normalizedText,
            StringComparison.Ordinal);
        int normalizedEnd = normalizedStart + normalizedText.Length;
        return new TextMatch(
            1,
            MapNormalizedOffsetToOriginal(content, normalizedStart),
            MapNormalizedOffsetToOriginal(content, normalizedEnd));
    }

    private static string NormalizeNewLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private static string AdaptNewLines(string text, string existingContent)
    {
        string newline = existingContent.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : "\n";
        return NormalizeNewLines(text).Replace("\n", newline, StringComparison.Ordinal);
    }

    private static int MapNormalizedOffsetToOriginal(string original, int offset)
    {
        int normalized = 0;
        int originalIndex = 0;
        while (originalIndex < original.Length && normalized < offset)
        {
            if (original[originalIndex] == '\r' &&
                originalIndex + 1 < original.Length &&
                original[originalIndex + 1] == '\n')
            {
                originalIndex += 2;
            }
            else
            {
                originalIndex++;
            }
            normalized++;
        }
        return originalIndex;
    }

    private readonly record struct TextMatch(
        int Count,
        int Start,
        int End)
    {
        public static TextMatch None => new(0, -1, -1);
        public bool IsUnique => Count == 1 && Start >= 0 && End >= Start;
    }

    internal static string FindNearbyExcerpt(string content, string oldText)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        string normalizedNeedle = oldText.Replace("\r\n", "\n", StringComparison.Ordinal);
        string normalizedContent = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        int index = normalizedContent.IndexOf(
            normalizedNeedle,
            StringComparison.Ordinal);
        if (index < 0)
        {
            string? anchor = normalizedNeedle
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length >= 4)
                .OrderByDescending(line => line.Length)
                .FirstOrDefault(line => normalizedContent.Contains(
                    line,
                    StringComparison.Ordinal));
            index = anchor is null
                ? 0
                : normalizedContent.IndexOf(anchor, StringComparison.Ordinal);
        }

        int start = Math.Max(0, index - 180);
        int length = Math.Min(480, normalizedContent.Length - start);
        string excerpt = normalizedContent.Substring(start, length);
        if (start > 0)
        {
            excerpt = "…" + excerpt;
        }
        if (start + length < normalizedContent.Length)
        {
            excerpt += "…";
        }
        return excerpt;
    }

    internal static string ResolveInside(string projectFolder, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(projectFolder))
        {
            throw new ArgumentException("No project folder is selected.");
        }

        string root = Path.GetFullPath(projectFolder);
        string full = Path.GetFullPath(Path.Combine(root, relativePath));
        string prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The path \"{relativePath}\" is outside the project folder.");
        }

        return full;
    }
}
