using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ParseTiger.Execution;

namespace ParseTiger.State;

public sealed record ProjectIdentity(
    string SolutionPath,
    string ProjectName,
    string ProjectKind,
    string ProjectRoot);

public sealed class ProjectStateSnapshot
{
    public required string Id { get; init; }
    public required ProjectIdentity Identity { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required IReadOnlyDictionary<string, byte[]> Files { get; init; }

    public ProjectBackup ToBackup()
    {
        var backup = new ProjectBackup
        {
            ProjectRoot = Identity.ProjectRoot,
            DeleteUnexpectedFilesOnRestore = true
        };
        backup.Files.AddRange(
            Files.Select(entry => new FileBackup(
                PackageExecutor.ResolveInside(Identity.ProjectRoot, entry.Key),
                entry.Value.ToArray())));
        return backup;
    }
}

public sealed record ProjectStateEvent(
    DateTimeOffset Timestamp,
    string Kind,
    string SnapshotId,
    string Detail);

public sealed record CurrentStateCheck(
    ProjectStateSnapshot Snapshot,
    bool Refreshed,
    string Detail);

/// <summary>
/// Owns the immutable session starting point and the refreshable current view
/// of one selected project. The durable user checkpoint remains BaselineService.
/// </summary>
public sealed class ProjectStateService
{
    private readonly List<ProjectStateEvent> _events = new();

    public ProjectStateSnapshot? SessionBaseline { get; private set; }
    public ProjectStateSnapshot? CurrentState { get; private set; }
    public IReadOnlyList<ProjectStateEvent> Events => _events;

    public CurrentStateCheck OpenOrSelect(ProjectIdentity identity)
    {
        ProjectIdentity normalized = Normalize(identity);
        if (SessionBaseline is null ||
            !SameIdentity(SessionBaseline.Identity, normalized))
        {
            ProjectStateSnapshot opened = Capture(normalized);
            SessionBaseline = opened;
            CurrentState = opened;
            AddEvent(
                "Session Baseline",
                opened,
                $"Created automatically for {normalized.ProjectName}.");
            AddEvent(
                "Current State",
                opened,
                "Loaded from the automatic Session Baseline.");
            return new CurrentStateCheck(
                opened,
                true,
                "A new immutable Session Baseline and Current State were created.");
        }

        return EnsureCurrent(normalized, "solution/project selection");
    }

    public CurrentStateCheck EnsureCurrent(
        ProjectIdentity identity,
        string reason)
    {
        ProjectIdentity normalized = Normalize(identity);
        if (SessionBaseline is null ||
            !SameIdentity(SessionBaseline.Identity, normalized))
        {
            return OpenOrSelect(normalized);
        }

        ProjectStateSnapshot disk = Capture(normalized);
        if (CurrentState is not null && CurrentState.Id == disk.Id)
        {
            return new CurrentStateCheck(
                CurrentState,
                false,
                "Current files match the last refreshed snapshot.");
        }

        string previous = CurrentState?.Id ?? "(none)";
        CurrentState = disk;
        AddEvent(
            "Current State",
            disk,
            $"Refreshed for {reason}; previous snapshot {previous}. " +
            "External or out-of-band file changes were detected.");
        return new CurrentStateCheck(
            disk,
            true,
            "External file changes were detected; Current State was refreshed.");
    }

    public ProjectStateSnapshot RefreshCurrent(
        ProjectIdentity identity,
        string reason)
    {
        ProjectIdentity normalized = Normalize(identity);
        if (SessionBaseline is null ||
            !SameIdentity(SessionBaseline.Identity, normalized))
        {
            return OpenOrSelect(normalized).Snapshot;
        }

        ProjectStateSnapshot refreshed = Capture(normalized);
        string previous = CurrentState?.Id ?? "(none)";
        CurrentState = refreshed;
        AddEvent(
            "Current State",
            refreshed,
            $"Refreshed after {reason}; previous snapshot {previous}.");
        return refreshed;
    }

    public void RecordProjectBaselineReset(ProjectStateSnapshot snapshot)
    {
        CurrentState = snapshot;
        AddEvent(
            "Project Baseline Reset",
            snapshot,
            "The durable Project Baseline was restored deterministically, " +
            "then Current State was reloaded from disk.");
    }

    public bool DiskMatches(ProjectStateSnapshot snapshot) =>
        Capture(snapshot.Identity).Id == snapshot.Id;

    public string BuildTextContext(
        ProjectStateSnapshot snapshot,
        IReadOnlySet<string> includedExtensions,
        IEnumerable<string>? selectedPaths = null)
    {
        var builder = new StringBuilder();
        IEnumerable<string> candidates = selectedPaths ?? snapshot.Files.Keys;
        string[] files = candidates
            .Where(snapshot.Files.ContainsKey)
            .Where(path => includedExtensions.Contains(Path.GetExtension(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        builder.AppendLine("PROJECT FILE INVENTORY:");
        foreach (string relative in files)
        {
            builder.Append("- ").AppendLine(relative);
        }

        builder.AppendLine();
        builder.AppendLine("PROJECT FILE CONTENTS:");
        foreach (string relative in files)
        {
            builder.Append("=== ").Append(relative).AppendLine(" ===");
            builder.AppendLine(DecodeText(snapshot.Files[relative]));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    public static ProjectStateSnapshot Capture(ProjectIdentity identity)
    {
        ProjectIdentity normalized = Normalize(identity);
        var mutable = new SortedDictionary<string, byte[]>(
            StringComparer.OrdinalIgnoreCase);
        foreach (string fullPath in ProjectFilePolicy.EnumerateFiles(
                     normalized.ProjectRoot))
        {
            string relative = Path.GetRelativePath(
                    normalized.ProjectRoot,
                    fullPath)
                .Replace('\\', '/');
            mutable[relative] = File.ReadAllBytes(fullPath);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AddHash(hash, normalized.SolutionPath);
        AddHash(hash, normalized.ProjectName);
        AddHash(hash, normalized.ProjectRoot);
        foreach ((string relative, byte[] bytes) in mutable)
        {
            AddHash(hash, relative);
            hash.AppendData(bytes);
        }

        string id = Convert.ToHexString(hash.GetHashAndReset())[..16];
        var copied = mutable.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
        return new ProjectStateSnapshot
        {
            Id = id,
            Identity = normalized,
            CapturedAt = DateTimeOffset.Now,
            Files = new ReadOnlyDictionary<string, byte[]>(copied)
        };
    }

    private void AddEvent(
        string kind,
        ProjectStateSnapshot snapshot,
        string detail) =>
        _events.Add(new ProjectStateEvent(
            DateTimeOffset.Now,
            kind,
            snapshot.Id,
            detail));

    private static ProjectIdentity Normalize(ProjectIdentity identity) => new(
        Path.GetFullPath(identity.SolutionPath),
        identity.ProjectName,
        identity.ProjectKind,
        Path.GetFullPath(identity.ProjectRoot));

    private static bool SameIdentity(ProjectIdentity left, ProjectIdentity right) =>
        left.SolutionPath.Equals(right.SolutionPath, StringComparison.OrdinalIgnoreCase) &&
        left.ProjectName.Equals(right.ProjectName, StringComparison.OrdinalIgnoreCase) &&
        left.ProjectRoot.Equals(right.ProjectRoot, StringComparison.OrdinalIgnoreCase);

    private static void AddHash(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static string DecodeText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
