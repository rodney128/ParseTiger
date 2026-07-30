using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml.Linq;
using ParseTiger.Execution;
using ParseTiger.Models;

namespace ParseTiger.State;

public sealed class ProjectSessionMemory
{
    private readonly List<string> _recentFiles = [];
    private readonly List<string> _successfulChanges = [];

    public string Solution { get; private set; } = "Not selected";
    public string Project { get; private set; } = "Not selected";
    public string Framework { get; private set; } = "Unknown";
    public string CurrentRequest { get; private set; } = "None";

    public void Select(AiProjectFacts facts)
    {
        Solution = facts.SolutionPath;
        Project = facts.ProjectName;
        Framework = facts.TargetFramework;
    }

    public void SetRequest(string? request) =>
        CurrentRequest = string.IsNullOrWhiteSpace(request) ? "None" : request.Trim();

    public void SetRecentFiles(IEnumerable<string> paths)
    {
        _recentFiles.Clear();
        _recentFiles.AddRange(paths.Take(12));
    }

    public void RecordSuccess(IEnumerable<string> paths)
    {
        string value = string.Join(", ", paths.Distinct(StringComparer.OrdinalIgnoreCase));
        if (value.Length == 0)
        {
            return;
        }

        _successfulChanges.Insert(0, value);
        if (_successfulChanges.Count > 5)
        {
            _successfulChanges.RemoveAt(_successfulChanges.Count - 1);
        }
    }

    public string Format() =>
        $"Solution: {Solution}{Environment.NewLine}" +
        $"Project: {Project}{Environment.NewLine}" +
        $"Target framework: {Framework}{Environment.NewLine}" +
        $"Current task: {CurrentRequest}{Environment.NewLine}{Environment.NewLine}" +
        "Recent context files:" + Environment.NewLine +
        (_recentFiles.Count == 0 ? "  None" :
            string.Join(Environment.NewLine, _recentFiles.Select(path => "  • " + path))) +
        Environment.NewLine + Environment.NewLine +
        "Recent successful patches:" + Environment.NewLine +
        (_successfulChanges.Count == 0 ? "  None this session" :
            string.Join(Environment.NewLine, _successfulChanges.Select(value => "  • " + value)));
}

public sealed class SessionEventLog
{
    private readonly List<string> _events = [];

    public void Add(string message)
    {
        _events.Add($"[{DateTimeOffset.Now:HH:mm:ss}] {message}");
        if (_events.Count > 500)
        {
            _events.RemoveRange(0, _events.Count - 500);
        }
    }

    public string Format() => string.Join(Environment.NewLine, _events);
}

public sealed record AiProjectFacts(
    string SolutionPath,
    string ProjectName,
    string ProjectFile,
    string ProjectRoot,
    string ProjectKind,
    string Sdk,
    string TargetFramework,
    bool GitAvailable,
    string GitDescription,
    int AuthoredFileCount,
    IReadOnlyList<string> GeneratedFolders);

public static class ProjectHealthInspector
{
    private static readonly string[] GeneratedNames =
        ["bin", "obj", "publish", "publish-check", "artifacts", "TestResults"];

    public static AiProjectFacts Inspect(
        SolutionInfo solution,
        ProjectInfo project,
        string projectFile)
    {
        string sdk = "Unknown";
        string framework = "Unknown";
        try
        {
            XDocument document = XDocument.Load(projectFile);
            sdk = document.Root?.Attribute("Sdk")?.Value ?? "Unknown";
            framework = document.Descendants()
                .FirstOrDefault(element =>
                    element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                ?.Value.Trim() ?? "Unknown";
        }
        catch
        {
            // Health reporting must never prevent a solution from loading.
        }

        GitRecoveryState git = RecoveryStateInspector.InspectGit(project.Directory);
        string[] generated = GeneratedNames
            .Where(name => Directory.Exists(Path.Combine(project.Directory, name)))
            .ToArray();
        int authored = ProjectFilePolicy.EnumerateFiles(project.Directory).Count;
        return new AiProjectFacts(
            solution.Path,
            project.Name,
            projectFile,
            project.Directory,
            project.Kind,
            sdk,
            framework,
            git.Available,
            git.DisplayText,
            authored,
            generated);
    }

    public static string Format(AiProjectFacts facts, string buildStatus = "Not run this session")
    {
        var text = new StringBuilder();
        text.AppendLine($"✓ Startup project: {facts.ProjectName} ({facts.ProjectKind})");
        text.AppendLine($"✓ Project file: {Path.GetFileName(facts.ProjectFile)}");
        text.AppendLine($"✓ SDK: {facts.Sdk}");
        text.AppendLine($"✓ Target framework: {facts.TargetFramework}");
        text.AppendLine($"✓ Authored files available: {facts.AuthoredFileCount:N0}");
        text.AppendLine(facts.GeneratedFolders.Count == 0
            ? "✓ Generated folders: none detected"
            : $"✓ Generated folders excluded: {string.Join(", ", facts.GeneratedFolders)}");
        text.AppendLine(facts.GitAvailable
            ? "✓ " + facts.GitDescription
            : "• " + facts.GitDescription);
        text.Append($"• Build: {buildStatus}");
        return text.ToString();
    }
}

public static class IntentSummaryFormatter
{
    public static string Format(string? request, AiProjectFacts? facts, int contextFiles)
    {
        string goal = string.IsNullOrWhiteSpace(request)
            ? "Enter a requested change."
            : request.Trim();
        return $"Goal: {goal}{Environment.NewLine}" +
               $"Project: {facts?.ProjectName ?? "Not selected"}{Environment.NewLine}" +
               $"Framework: {facts?.ProjectKind ?? "Unknown"} / " +
               $"{facts?.TargetFramework ?? "Unknown"}{Environment.NewLine}" +
               $"Context files: {contextFiles:N0}{Environment.NewLine}" +
               "Constraints: replace-only package; project-relative paths; " +
               "generated output and unrelated configuration excluded.";
    }
}

public static class ContextPreviewFormatter
{
    public static string Format(ProjectStateSnapshot snapshot, IReadOnlyList<string> selected)
    {
        var text = new StringBuilder();
        text.AppendLine($"Snapshot: {snapshot.Id}");
        text.AppendLine($"Selected: {selected.Count:N0} of {snapshot.Files.Count:N0} authored files");
        text.AppendLine();
        foreach (string path in selected)
        {
            text.AppendLine(path);
        }

        return text.ToString().TrimEnd();
    }
}
