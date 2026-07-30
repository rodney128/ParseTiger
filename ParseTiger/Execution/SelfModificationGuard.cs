using System.IO;
using ParseTiger.Models;

namespace ParseTiger.Execution;

public sealed record SelfModificationAssessment(
    bool IsSelfModification,
    bool TouchesCoreSafetyCode,
    IReadOnlyList<string> CorePaths)
{
    public bool RequiresConfirmation => IsSelfModification;

    public string BuildMessage()
    {
        string detail = CorePaths.Count == 0
            ? "The package modifies the ParseTiger project."
            : "The package modifies ParseTiger safety-critical files:" +
              Environment.NewLine + string.Join(
                  Environment.NewLine,
                  CorePaths.Select(path => "• " + path));
        return detail + Environment.NewLine + Environment.NewLine +
               "A complete pre-apply checkpoint will be retained. The solution " +
               "must build successfully or the change will be rolled back. Continue?";
    }
}

public static class SelfModificationGuard
{
    private static readonly string[] CoreAreas =
    [
        "Execution/", "Validation/", "Generation/", "State/",
        "Packages/", "Reset/", "MainWindow.xaml", "MainWindow.xaml.cs"
    ];

    public static SelfModificationAssessment Assess(ProjectInfo project, Package package)
    {
        bool self = project.Name.Equals("ParseTiger", StringComparison.OrdinalIgnoreCase) ||
                    project.Directory.EndsWith(
                        $"{Path.DirectorySeparatorChar}ParseTiger",
                        StringComparison.OrdinalIgnoreCase);
        string[] core = self
            ? package.Operations
                .Select(operation => (operation.Path ?? string.Empty).Replace('\\', '/'))
                .Where(path => CoreAreas.Any(area =>
                    path.StartsWith(area, StringComparison.OrdinalIgnoreCase) ||
                    path.Equals(area, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        return new SelfModificationAssessment(self, core.Length > 0, core);
    }
}
