using System.IO;
using ParseTiger.Models;

namespace ParseTiger.Execution;

/// <summary>
/// Captures and restores the exact bytes of the files a package will change, so a
/// failed build (or an Undo) can put the project back exactly as it was.
/// </summary>
public static class BackupService
{
    public static ProjectBackup Capture(Package package, string projectFolder)
    {
        ArgumentNullException.ThrowIfNull(package);
        var backup = new ProjectBackup();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Operation operation in package.Operations)
        {
            if (string.IsNullOrWhiteSpace(operation.Path))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = PackageExecutor.ResolveInside(projectFolder, operation.Path);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (seen.Add(fullPath) && File.Exists(fullPath))
            {
                backup.Files.Add(new FileBackup(fullPath, File.ReadAllBytes(fullPath)));
            }
        }

        return backup;
    }

    public static ProjectBackup CaptureProject(string projectFolder)
    {
        string root = Path.GetFullPath(projectFolder);
        var backup = new ProjectBackup
        {
            ProjectRoot = root,
            DeleteUnexpectedFilesOnRestore = true
        };

        foreach (string file in ProjectFilePolicy.EnumerateFiles(root))
        {
            backup.Files.Add(new FileBackup(file, File.ReadAllBytes(file)));
        }

        return backup;
    }

    public static void Restore(ProjectBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (backup.DeleteUnexpectedFilesOnRestore)
        {
            if (string.IsNullOrWhiteSpace(backup.ProjectRoot))
            {
                throw new InvalidOperationException(
                    "The full-project backup has no project root.");
            }

            string root = Path.GetFullPath(backup.ProjectRoot);
            var expected = backup.Files
                .Select(file => Path.GetFullPath(file.FullPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string current in ProjectFilePolicy.EnumerateFiles(root))
            {
                if (!expected.Contains(Path.GetFullPath(current)))
                {
                    File.Delete(current);
                }
            }
        }

        foreach (FileBackup file in backup.Files)
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(file.FullPath)
                ?? throw new InvalidOperationException("A backup path has no directory."));
            File.WriteAllBytes(file.FullPath, file.OriginalBytes);
        }
    }
}
