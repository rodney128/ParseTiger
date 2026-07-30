namespace ParseTiger.Execution;

/// <summary>One file's original bytes, captured before a change was applied.</summary>
public sealed record FileBackup(string FullPath, byte[] OriginalBytes);

/// <summary>A snapshot of the files a package will touch, so they can be restored.</summary>
public sealed class ProjectBackup
{
    public List<FileBackup> Files { get; init; } = new();

    /// <summary>
    /// When set, restore also removes included project files that were not in
    /// this snapshot. Package backups leave this false because they cover only
    /// the files touched by that package.
    /// </summary>
    public bool DeleteUnexpectedFilesOnRestore { get; init; }

    public string? ProjectRoot { get; init; }

    public bool HasFiles => Files.Count > 0;
}
