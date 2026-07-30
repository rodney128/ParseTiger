using System.IO;
using ParseTiger.Models;

namespace ParseTiger.Validation;

/// <summary>
/// Classifies protected Visual Studio project-file edits. ParseTiger does not
/// receive the original human request in its returned-package format, so every
/// such edit requires an explicit user decision before it can be applied.
/// </summary>
public sealed class ProjectFileChangeGuard
{
    private static readonly (string Token, string Description)[] SensitiveTokens =
    [
        ("<PackageReference", "NuGet package references or dependencies"),
        ("<TargetFramework", "the target framework"),
        ("Sdk=", "the project SDK"),
        ("<PropertyGroup", "project or build properties"),
        ("<ItemGroup", "project items or dependencies"),
        ("<ProjectReference", "project references"),
        ("<Reference", "assembly references"),
        ("<Import", "imported build configuration")
    ];

    public ProjectFileChangeAssessment Assess(Package package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var assessment = new ProjectFileChangeAssessment();
        foreach ((Operation operation, int index) in package.Operations
                     .Select((operation, index) => (operation, index)))
        {
            if (!string.Equals(
                    Path.GetExtension(operation.Path),
                    ".csproj",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string changedText = (operation.OldText ?? string.Empty) + "\n" +
                (operation.NewText ?? string.Empty);
            foreach ((string token, string description) in SensitiveTokens)
            {
                if (changedText.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    categories.Add(description);
                }
            }

            if (categories.Count == 0)
            {
                categories.Add("Visual Studio project configuration");
            }

            assessment.Changes.Add(new ProtectedProjectFileChange
            {
                OperationNumber = index + 1,
                Path = operation.Path ?? string.Empty,
                Categories = categories.OrderBy(value => value).ToArray()
            });
        }

        return assessment;
    }
}

public sealed class ProjectFileChangeAssessment
{
    public List<ProtectedProjectFileChange> Changes { get; } = new();
    public bool RequiresExplicitConfirmation => Changes.Count > 0;

    public string BuildConfirmationMessage()
    {
        var lines = new List<string>
        {
            "This package changes a Visual Studio project file.",
            "",
            "Project-file changes can alter dependencies, restore, build, or runtime behavior:"
        };

        foreach (ProtectedProjectFileChange change in Changes)
        {
            lines.Add(
                $"- Operation {change.OperationNumber}: {change.Path} " +
                $"({string.Join(", ", change.Categories)})");
        }

        lines.Add("");
        lines.Add(
            "ParseTiger cannot infer authorization from the returned JSON. " +
            "Continue only if your original request explicitly asked for these exact changes.");
        lines.Add("");
        lines.Add("Apply this protected project-file change?");
        return string.Join(Environment.NewLine, lines);
    }
}

public sealed class ProtectedProjectFileChange
{
    public int OperationNumber { get; init; }
    public string Path { get; init; } = string.Empty;
    public IReadOnlyList<string> Categories { get; init; } = [];
}
