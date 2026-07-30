using System.IO;

namespace ParseTiger.State;

public sealed record GitRecoveryState(bool Available, string? RepositoryRoot)
{
    public string DisplayText => Available
        ? $"Git recovery: available — repository {RepositoryRoot}"
        : "Git recovery: unavailable — no Git repository was detected";
}

/// <summary>
/// Reports whether the selected project has an independent Git recovery path.
/// It never changes repository state.
/// </summary>
public static class RecoveryStateInspector
{
    public static GitRecoveryState InspectGit(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return new GitRecoveryState(false, null);
        }

        var directory = new DirectoryInfo(Path.GetFullPath(projectRoot));
        while (directory is not null)
        {
            string marker = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return new GitRecoveryState(true, directory.FullName);
            }
            directory = directory.Parent;
        }

        return new GitRecoveryState(false, null);
    }
}
