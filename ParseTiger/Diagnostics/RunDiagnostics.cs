using System.Text;
using System.Text.RegularExpressions;

namespace ParseTiger.Diagnostics;

public enum DiagnosticState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped
}

public enum DiagnosticFailureKind
{
    None,
    ProjectLoad,
    ContextCreation,
    Credential,
    Provider,
    ProviderQuota,
    PackageParse,
    Validation,
    Workflow,
    Apply,
    Build,
    Launch,
    Rollback,
    Unexpected
}

public sealed class DiagnosticStage
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public DiagnosticState State { get; set; } = DiagnosticState.Pending;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Detail { get; set; } = string.Empty;
}

public sealed class OperationDiagnostic
{
    public int Number { get; init; }
    public string Type { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public int OldTextLength { get; init; }
    public int MatchCount { get; set; }
    public bool IsUnique => MatchCount == 1;
    public DiagnosticState State { get; set; } = DiagnosticState.Pending;
    public DateTimeOffset? CheckedAt { get; set; }
    public string FailureReason { get; set; } = string.Empty;
    public string ExpectedOldText { get; set; } = string.Empty;
    public string NearbyExcerpt { get; set; } = string.Empty;
}

public sealed class RunDiagnostic
{
    public required string Id { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string SolutionPath { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string SessionBaselineId { get; set; } = string.Empty;
    public string ContextSnapshotId { get; set; } = string.Empty;
    public string Summary { get; set; } = "Running";
    public DiagnosticFailureKind FailureKind { get; set; }
    public int OperationCount { get; set; }
    public List<string> ModifiedFiles { get; } = new();
    public bool ProjectChangesPersisted { get; set; }
    public string ProjectChangesDetail { get; set; } =
        "No project changes have been applied.";
    public int? RepairAttemptNumber { get; set; }
    public int RepairHistoryCount { get; set; }
    public bool CumulativeRepairHistoryIncluded { get; set; }
    public string LatestRepairFailureStage { get; set; } = string.Empty;
    public List<DiagnosticStage> Stages { get; } = new();
    public List<OperationDiagnostic> Operations { get; } = new();
    public List<string> StateEvents { get; } = new();

    public string DisplayName =>
        $"{StartedAt:HH:mm:ss} — {Summary}";
}

public sealed class RunDiagnosticsCollector
{
    private static readonly (string Key, string Name)[] StandardStages =
    [
        ("project", "Project load"),
        ("context", "Context creation"),
        ("credential", "Credential load / verification"),
        ("provider-request", "Generate"),
        ("provider-response", "Receive"),
        ("parse", "Parse"),
        ("validate", "Validate"),
        ("preflight", "Replace-operation preflight"),
        ("backup", "Backup created"),
        ("apply", "Apply"),
        ("build", "Build"),
        ("launch", "Launch"),
        ("rollback", "Rollback")
    ];

    public RunDiagnosticsCollector(string solutionPath, string provider)
    {
        Run = new RunDiagnostic
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedAt = DateTimeOffset.Now,
            SolutionPath = solutionPath,
            Provider = provider
        };
        foreach ((string key, string name) in StandardStages)
        {
            Run.Stages.Add(new DiagnosticStage { Key = key, Name = name });
        }
    }

    public RunDiagnostic Run { get; }

    public void Start(string key, string detail = "")
    {
        DiagnosticStage stage = Find(key);
        stage.State = DiagnosticState.Running;
        stage.StartedAt ??= DateTimeOffset.Now;
        stage.Detail = detail;
    }

    public void Succeed(string key, string detail = "") =>
        Finish(key, DiagnosticState.Succeeded, detail);

    public void Fail(
        string key,
        DiagnosticFailureKind kind,
        string detail)
    {
        Finish(key, DiagnosticState.Failed, detail);
        Run.FailureKind = kind;
        Run.Summary = $"{Find(key).Name} failed";
    }

    public void Skip(string key, string detail) =>
        Finish(key, DiagnosticState.Skipped, detail);

    public void AddOperation(OperationDiagnostic operation) =>
        Run.Operations.Add(operation);

    public void Complete(string summary)
    {
        Run.FinishedAt = DateTimeOffset.Now;
        Run.Summary = summary;
        foreach (DiagnosticStage stage in Run.Stages.Where(
                     stage => stage.State == DiagnosticState.Pending))
        {
            stage.State = DiagnosticState.Skipped;
            stage.FinishedAt = DateTimeOffset.Now;
            stage.Detail = "Workflow ended before this stage.";
        }
    }

    private void Finish(string key, DiagnosticState state, string detail)
    {
        DiagnosticStage stage = Find(key);
        stage.StartedAt ??= DateTimeOffset.Now;
        stage.FinishedAt = DateTimeOffset.Now;
        stage.State = state;
        stage.Detail = detail;
    }

    private DiagnosticStage Find(string key) =>
        Run.Stages.First(stage =>
            stage.Key.Equals(key, StringComparison.Ordinal));
}

public static partial class DiagnosticRedactor
{
    [GeneratedRegex(
        @"(?i)\b(authorization|x-goog-api-key|api[-_ ]?key|token|secret)\b\s*[:=]\s*([^\s,;]+)")]
    private static partial Regex NamedSecret();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerSecret();

    [GeneratedRegex(@"\b(?:AIza[0-9A-Za-z_-]{20,}|sk-[A-Za-z0-9_-]{16,})\b")]
    private static partial Regex KnownKey();

    public static string Redact(string? value)
    {
        string text = value ?? string.Empty;
        text = NamedSecret().Replace(text, "$1: [REDACTED]");
        text = BearerSecret().Replace(text, "Bearer [REDACTED]");
        return KnownKey().Replace(text, "[REDACTED]");
    }
}

public static class DiagnosticReportFormatter
{
    public static string Format(RunDiagnostic run)
    {
        var report = new StringBuilder();
        report.AppendLine("PARSETIGER RUN DIAGNOSTICS");
        report.AppendLine($"Run ID: {run.Id}");
        report.AppendLine($"Started: {run.StartedAt:O}");
        report.AppendLine($"Finished: {run.FinishedAt:O}");
        report.AppendLine($"Result: {run.Summary}");
        report.AppendLine($"Failure category: {run.FailureKind}");
        report.AppendLine($"Solution: {DiagnosticRedactor.Redact(run.SolutionPath)}");
        report.AppendLine($"Project: {run.ProjectName}");
        report.AppendLine($"Provider: {run.Provider}");
        report.AppendLine($"Session Baseline ID: {run.SessionBaselineId}");
        report.AppendLine($"Run Context Snapshot ID: {run.ContextSnapshotId}");
        if (run.RepairAttemptNumber is not null)
        {
            report.AppendLine($"Retry attempt number: {run.RepairAttemptNumber}");
            report.AppendLine(
                $"Prior failures included: {run.RepairHistoryCount}");
            report.AppendLine(
                $"Cumulative repair history included: " +
                $"{(run.CumulativeRepairHistoryIncluded ? "Yes" : "No")}");
            report.AppendLine(
                $"Latest repair failure stage: {run.LatestRepairFailureStage}");
        }
        report.AppendLine();
        report.AppendLine(RunSummaryFormatter.Format(run));
        report.AppendLine();
        report.AppendLine("STATE EVENTS");
        if (run.StateEvents.Count == 0)
        {
            report.AppendLine("(none)");
        }
        foreach (string stateEvent in run.StateEvents)
        {
            report.AppendLine(DiagnosticRedactor.Redact(stateEvent));
        }
        report.AppendLine();
        report.AppendLine("STAGES");
        foreach (DiagnosticStage stage in run.Stages)
        {
            string timestamp = stage.StartedAt?.ToString("HH:mm:ss.fff") ?? "--:--:--.---";
            report.AppendLine(
                $"[{timestamp}] {stage.State,-9} {stage.Name}: " +
                DiagnosticRedactor.Redact(stage.Detail));
        }

        report.AppendLine();
        report.AppendLine("REPLACE OPERATIONS");
        if (run.Operations.Count == 0)
        {
            report.AppendLine("(none)");
        }
        foreach (OperationDiagnostic operation in run.Operations)
        {
            report.AppendLine(
                $"Operation {operation.Number}: {operation.Type} {operation.RelativePath}");
            report.AppendLine(
                $"  State: {operation.State}; oldText length: {operation.OldTextLength}; " +
                $"occurrences: {operation.MatchCount}; unique: {operation.IsUnique}");
            if (!string.IsNullOrWhiteSpace(operation.FailureReason))
            {
                report.AppendLine(
                    "  Failure: " + DiagnosticRedactor.Redact(operation.FailureReason));
            }
            if (!string.IsNullOrEmpty(operation.ExpectedOldText))
            {
                report.AppendLine("  Expected oldText:");
                report.AppendLine(Indent(Truncate(
                    DiagnosticRedactor.Redact(operation.ExpectedOldText), 2000)));
            }
            if (!string.IsNullOrEmpty(operation.NearbyExcerpt))
            {
                report.AppendLine("  Nearby / closest context:");
                report.AppendLine(Indent(Truncate(
                    DiagnosticRedactor.Redact(operation.NearbyExcerpt), 1200)));
            }
        }

        report.AppendLine();
        report.AppendLine(
            "Sensitive credentials and authorization values are redacted.");
        return report.ToString().TrimEnd();
    }

    private static string Truncate(string text, int limit) =>
        text.Length <= limit
            ? text
            : text[..limit] + Environment.NewLine +
              $"… [truncated {text.Length - limit:N0} characters]";

    private static string Indent(string text) =>
        "    " + text.Replace(
            Environment.NewLine,
            Environment.NewLine + "    ",
            StringComparison.Ordinal);
}

public static class RunSummaryFormatter
{
    public static string Format(RunDiagnostic run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var summary = new StringBuilder();
        summary.AppendLine("RUN SUMMARY");
        summary.AppendLine(
            $"Package Generated: {DescribePackageGeneration(run)}");
        summary.AppendLine(
            $"Package Applied:   {DescribeStage(run, "apply")}");
        summary.AppendLine(
            $"Build:             {DescribeStage(run, "build")}");
        summary.AppendLine(
            $"Launch:            {DescribeStage(run, "launch")}");
        summary.AppendLine();
        summary.AppendLine("PROJECT CHANGES");
        summary.AppendLine($"Operations: {run.OperationCount:N0}");
        summary.AppendLine(
            $"Files modified: {run.ModifiedFiles.Count:N0}");
        foreach (string file in run.ModifiedFiles)
        {
            summary.AppendLine($"- {file}");
        }
        summary.AppendLine(
            $"Persisted on disk: {(run.ProjectChangesPersisted ? "Yes" : "No")}");
        summary.AppendLine(run.ProjectChangesDetail);

        if (run.ProjectChangesPersisted &&
            State(run, "build") == DiagnosticState.Succeeded &&
            State(run, "launch") == DiagnosticState.Failed)
        {
            summary.AppendLine();
            summary.AppendLine(
                "The project files were successfully modified and built. " +
                "Only application launch failed. Undo remains available.");
        }

        return summary.ToString().TrimEnd();
    }

    private static string DescribePackageGeneration(RunDiagnostic run)
    {
        DiagnosticStage? response = Find(run, "provider-response");
        if (response?.State == DiagnosticState.Succeeded)
        {
            return "Succeeded" + Detail(response);
        }

        DiagnosticStage? request = Find(run, "provider-request");
        if (request?.State == DiagnosticState.Skipped)
        {
            DiagnosticStage? parse = Find(run, "parse");
            return parse?.State == DiagnosticState.Succeeded
                ? "Manual package accepted"
                : "Manual package";
        }

        return Describe(response ?? request);
    }

    private static string DescribeStage(RunDiagnostic run, string key) =>
        Describe(Find(run, key));

    private static string Describe(DiagnosticStage? stage) =>
        stage is null
            ? "Pending"
            : stage.State switch
            {
                DiagnosticState.Pending => "Pending",
                DiagnosticState.Running => "Running",
                DiagnosticState.Succeeded => "Succeeded" + Detail(stage),
                DiagnosticState.Failed => "Failed" + Detail(stage),
                DiagnosticState.Skipped => "Not reached" + Detail(stage),
                _ => stage.State.ToString()
            };

    private static string Detail(DiagnosticStage stage) =>
        string.IsNullOrWhiteSpace(stage.Detail)
            ? string.Empty
            : $" — {DiagnosticRedactor.Redact(stage.Detail)}";

    private static DiagnosticState State(RunDiagnostic run, string key) =>
        Find(run, key)?.State ?? DiagnosticState.Pending;

    private static DiagnosticStage? Find(RunDiagnostic run, string key) =>
        run.Stages.FirstOrDefault(stage =>
            stage.Key.Equals(key, StringComparison.Ordinal));
}

public static class RunOutcomePolicy
{
    public static bool ShouldRollback(
        bool mutationStarted,
        bool workflowSucceeded,
        bool preserveBuiltChanges) =>
        mutationStarted && !workflowSucceeded && !preserveBuiltChanges;
}
