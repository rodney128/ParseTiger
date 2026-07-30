using System.IO;
using System.Text.RegularExpressions;

namespace ParseTiger.State;

/// <summary>
/// Selects a focused, deterministic source set for an AI request. The full
/// snapshot remains the validation and rollback source of truth.
/// </summary>
public static partial class ProjectContextSelector
{
    private const int MaximumFiles = 36;

    private static readonly HashSet<string> StopWords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "about", "after", "again", "also", "change", "could", "existing",
            "from", "have", "into", "make", "only", "page", "project", "requested",
            "should", "that", "their", "this", "using", "want", "website", "with"
        };

    public static IReadOnlyList<string> Select(
        ProjectStateSnapshot snapshot,
        IReadOnlySet<string> includedExtensions,
        string? request,
        IEnumerable<string>? affectedPaths = null)
    {
        string[] eligible = snapshot.Files.Keys
            .Where(path => includedExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (eligible.Length <= MaximumFiles)
        {
            return eligible;
        }

        string normalizedRequest = request ?? string.Empty;
        string[] terms = WordPattern()
            .Matches(normalizedRequest)
            .Select(match => match.Value)
            .Where(term => term.Length >= 4 && !StopWords.Contains(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var affected = new HashSet<string>(
            affectedPaths ?? [],
            StringComparer.OrdinalIgnoreCase);
        bool webRequest = ContainsAny(
            normalizedRequest,
            "web", "homepage", "front page", "razor", "css", "site");
        bool wpfRequest = ContainsAny(
            normalizedRequest,
            "wpf", "window", "xaml", "desktop");

        return eligible
            .Select(path => new
            {
                Path = path,
                Score = Score(
                    path,
                    snapshot.Files[path],
                    terms,
                    affected,
                    webRequest,
                    wpfRequest)
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumFiles)
            .Select(item => item.Path)
            .ToArray();
    }

    private static int Score(
        string path,
        byte[] content,
        IReadOnlyList<string> terms,
        IReadOnlySet<string> affected,
        bool webRequest,
        bool wpfRequest)
    {
        string fileName = Path.GetFileName(path);
        string extension = Path.GetExtension(path);
        int score = affected.Contains(path) ? 10_000 : 0;

        if (extension is ".csproj" or ".vbproj" or ".fsproj" or ".props" or ".targets")
        {
            score += 500;
        }
        if (fileName.Equals("Program.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("App.xaml", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("App.xaml.cs", StringComparison.OrdinalIgnoreCase))
        {
            score += 300;
        }
        if (webRequest)
        {
            if (path.Contains("Pages/Index.", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Pages/Shared/_Layout.", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("site.css", StringComparison.OrdinalIgnoreCase))
            {
                score += 2_000;
            }
            if (extension is ".cshtml" or ".razor" or ".css" or ".html")
            {
                score += 250;
            }
        }
        if (wpfRequest &&
            (fileName.StartsWith("MainWindow.", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase)))
        {
            score += 1_500;
        }

        string text = terms.Count == 0
            ? string.Empty
            : System.Text.Encoding.UTF8.GetString(content);
        foreach (string term in terms)
        {
            if (path.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 800;
            }
            else if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 50;
            }
        }

        // Prefer authored source over broad configuration when scores tie.
        if (extension is ".cs" or ".xaml" or ".cshtml" or ".razor" or ".css")
        {
            score += 25;
        }
        return score;
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex("[A-Za-z][A-Za-z0-9_-]*")]
    private static partial Regex WordPattern();
}
