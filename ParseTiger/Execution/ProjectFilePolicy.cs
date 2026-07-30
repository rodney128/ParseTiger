using System.IO;

namespace ParseTiger.Execution;

/// <summary>
/// Defines the project files that participate in baselines and full-project
/// rollback. Generated output and source-control metadata are never captured.
/// </summary>
public static class ProjectFilePolicy
{
    private static readonly HashSet<string> ExcludedDirectories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "publish", "publish-check", "artifacts",
            "TestResults", "coverage", "node_modules", "packages",
            ".vs", ".idea", ".git", ".svn", ".hg"
        };

    public static IReadOnlyList<string> EnumerateFiles(string projectFolder)
    {
        string root = Path.GetFullPath(projectFolder);
        return Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => IsIncluded(root, path))
            .OrderBy(path => Path.GetRelativePath(root, path),
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsIncluded(string projectFolder, string fullPath)
    {
        string relative = Path.GetRelativePath(projectFolder, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            return false;
        }

        return relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .All(segment => !ExcludedDirectories.Contains(segment));
    }
}
