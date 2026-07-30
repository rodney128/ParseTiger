using System.Text;
using ParseTiger.Diagnostics;

namespace ParseTiger.Generation;

/// <summary>
/// Sanitized evidence from one provider package that reached a deterministic
/// ParseTiger failure stage.
/// </summary>
public sealed record FailedRunRepairAttempt(
    int Number,
    string ProviderOutput,
    string FailureStage,
    string ExactErrors,
    string Diagnostics,
    string BuildAndRuntimeOutput,
    IReadOnlyList<string> AffectedPaths,
    string Learned);

/// <summary>
/// Append-only repair evidence for one requested change. It contains no
/// provider credential. A successful run, a new request, Undo, or Reset clears
/// the history.
/// </summary>
public sealed class FailedRunRetryContext
{
    public FailedRunRetryContext(
        string solutionPath,
        string projectRoot,
        string originalRequest,
        IReadOnlyList<FailedRunRepairAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        if (attempts.Count == 0)
        {
            throw new ArgumentException(
                "Repair history must contain at least one failed attempt.",
                nameof(attempts));
        }

        SolutionPath = solutionPath ?? string.Empty;
        ProjectRoot = projectRoot ?? string.Empty;
        OriginalRequest = originalRequest ?? string.Empty;
        Attempts = attempts.ToArray();
    }

    // Compatibility constructor for callers that capture an initial failure.
    public FailedRunRetryContext(
        string solutionPath,
        string projectRoot,
        string originalRequest,
        string previousPackage,
        string failedStage,
        string diagnostics,
        string buildAndRuntimeOutput,
        IReadOnlyList<string> affectedPaths)
        : this(
            solutionPath,
            projectRoot,
            originalRequest,
            [
                CreateAttempt(
                    1,
                    previousPackage,
                    failedStage,
                    diagnostics,
                    buildAndRuntimeOutput,
                    affectedPaths)
            ])
    {
    }

    public string SolutionPath { get; }
    public string ProjectRoot { get; }
    public string OriginalRequest { get; }
    public IReadOnlyList<FailedRunRepairAttempt> Attempts { get; }
    public int RetryAttemptNumber => Attempts.Count;
    public FailedRunRepairAttempt LatestAttempt => Attempts[^1];

    // These forwarding properties keep UI presentation code compact.
    public string PreviousPackage => LatestAttempt.ProviderOutput;
    public string FailedStage => LatestAttempt.FailureStage;
    public string Diagnostics => LatestAttempt.Diagnostics;
    public string BuildAndRuntimeOutput => LatestAttempt.BuildAndRuntimeOutput;
    public IReadOnlyList<string> AffectedPaths => LatestAttempt.AffectedPaths;

    public FailedRunRetryContext Append(
        string providerOutput,
        string failedStage,
        string diagnostics,
        string buildAndRuntimeOutput,
        IReadOnlyList<string> affectedPaths)
    {
        var attempts = Attempts.ToList();
        attempts.Add(CreateAttempt(
            attempts.Count + 1,
            providerOutput,
            failedStage,
            diagnostics,
            buildAndRuntimeOutput,
            affectedPaths));
        return new FailedRunRetryContext(
            SolutionPath,
            ProjectRoot,
            OriginalRequest,
            attempts);
    }

    private static FailedRunRepairAttempt CreateAttempt(
        int number,
        string providerOutput,
        string failedStage,
        string diagnostics,
        string buildAndRuntimeOutput,
        IReadOnlyList<string> affectedPaths)
    {
        string stage = ClassifyStage(failedStage);
        string exactErrors = ExtractExactErrors(
            failedStage,
            diagnostics,
            buildAndRuntimeOutput);
        string learned =
            $"Attempt {number} failed during {stage}. The next repair must address " +
            $"these errors without repeating this provider output or undoing " +
            $"already successful changes: {Truncate(exactErrors, 1200)}";
        return new FailedRunRepairAttempt(
            number,
            DiagnosticRedactor.Redact(providerOutput),
            stage,
            DiagnosticRedactor.Redact(exactErrors),
            DiagnosticRedactor.Redact(diagnostics),
            DiagnosticRedactor.Redact(buildAndRuntimeOutput),
            affectedPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            DiagnosticRedactor.Redact(learned));
    }

    private static string ClassifyStage(string failedStage)
    {
        string value = failedStage?.Trim() ?? string.Empty;
        int separator = value.IndexOf(':');
        string key = (separator >= 0 ? value[..separator] : value)
            .Trim()
            .ToLowerInvariant();
        return key switch
        {
            "parse" => "parse",
            "validate" or "preflight" => "package validation",
            "apply" or "backup" => "apply",
            "build" => "build",
            "launch" => "launch",
            _ => string.IsNullOrWhiteSpace(key) ? "unknown" : key
        };
    }

    private static string ExtractExactErrors(
        string failedStage,
        string diagnostics,
        string buildAndRuntimeOutput)
    {
        var errors = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(failedStage))
        {
            errors.AppendLine(failedStage.Trim());
        }
        if (!string.IsNullOrWhiteSpace(buildAndRuntimeOutput))
        {
            errors.AppendLine(buildAndRuntimeOutput.Trim());
        }
        if (errors.Length == 0 && !string.IsNullOrWhiteSpace(diagnostics))
        {
            errors.AppendLine(diagnostics.Trim());
        }
        return errors.ToString().TrimEnd();
    }

    private static string Truncate(string value, int limit) =>
        value.Length <= limit ? value : value[..limit] + " …";
}

/// <summary>Builds a cumulative, user-triggered repair request.</summary>
public static class FailedRunRetryPrompt
{
    public static string Build(FailedRunRetryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var prompt = new StringBuilder();
        prompt.AppendLine("RETRY A FAILED PARSETIGER RUN");
        prompt.AppendLine();
        prompt.AppendLine(
            $"This is user-triggered repair attempt {context.RetryAttemptNumber}. " +
            "Do not assume another retry will happen automatically.");
        prompt.AppendLine("Return corrected ParseTiger JSON only.");
        prompt.AppendLine("Repair the current failure while preserving every working change already present.");
        prompt.AppendLine("Do not repeat any approach recorded as failed below.");
        prompt.AppendLine("Do not repeat operations that are already successful or already present.");
        prompt.AppendLine("Every oldText must be copied verbatim from the CURRENT PROJECT FILES supplied after this repair history.");
        prompt.AppendLine("Use the smallest exact unique oldText anchor that safely identifies the edit.");
        prompt.AppendLine("The latest CURRENT PROJECT FILES are authoritative and supersede stale text in prior packages.");
        prompt.AppendLine();
        prompt.AppendLine("ORIGINAL REQUESTED CHANGE:");
        prompt.AppendLine(context.OriginalRequest);
        prompt.AppendLine();
        prompt.AppendLine("PARSETIGER PACKAGE RULES AND PROJECT CONSTRAINTS:");
        prompt.AppendLine(GenerationPrompts.System);
        prompt.AppendLine();
        prompt.AppendLine("RESOLVED PROJECT CONSTRAINTS:");
        prompt.AppendLine($"Solution: {context.SolutionPath}");
        prompt.AppendLine($"Project root: {context.ProjectRoot}");
        prompt.AppendLine("All operation paths must remain inside this project root.");
        prompt.AppendLine();
        prompt.AppendLine(
            $"COMPLETE REPAIR HISTORY ({context.Attempts.Count} failed " +
            $"{(context.Attempts.Count == 1 ? "attempt" : "attempts")}):");

        foreach (FailedRunRepairAttempt attempt in context.Attempts)
        {
            prompt.AppendLine();
            prompt.AppendLine($"--- FAILED ATTEMPT {attempt.Number} ---");
            prompt.AppendLine($"Failure stage classification: {attempt.FailureStage}");
            prompt.AppendLine("What was learned:");
            prompt.AppendLine(attempt.Learned);
            prompt.AppendLine("Affected file paths:");
            if (attempt.AffectedPaths.Count == 0)
            {
                prompt.AppendLine("(none recorded)");
            }
            else
            {
                foreach (string path in attempt.AffectedPaths)
                {
                    prompt.Append("- ").AppendLine(path);
                }
            }
            prompt.AppendLine("Provider package/output:");
            prompt.AppendLine(attempt.ProviderOutput);
            prompt.AppendLine("Exact errors from this attempt:");
            prompt.AppendLine(attempt.ExactErrors);
            prompt.AppendLine("Full deterministic diagnostics:");
            prompt.AppendLine(attempt.Diagnostics);
            prompt.AppendLine("Validation / apply / build / launch output:");
            prompt.AppendLine(attempt.BuildAndRuntimeOutput);
        }

        prompt.AppendLine();
        prompt.AppendLine("CURRENT REPAIR INSTRUCTION:");
        prompt.AppendLine(
            $"Repair the latest {context.LatestAttempt.FailureStage} failure. " +
            "Use everything learned from every prior attempt. Preserve successful " +
            "changes and do not repeat any prior failed provider output or approach.");
        prompt.AppendLine(
            "The CURRENT PROJECT FILES appended by ParseTiger immediately after " +
            "this prompt contain the latest file contents relevant to the failure.");
        return prompt.ToString().TrimEnd();
    }
}

public static class FailedRunRetryPolicy
{
    private static readonly HashSet<string> RetryableStages =
        new(StringComparer.Ordinal)
        {
            "parse", "validate", "preflight", "apply", "build", "launch"
        };

    public static bool CanRetry(RunDiagnostic run)
    {
        ArgumentNullException.ThrowIfNull(run);
        bool packageWasReceived = run.Stages.Any(stage =>
            stage.Key == "provider-response" &&
            stage.State == DiagnosticState.Succeeded);
        return run.FailureKind != DiagnosticFailureKind.None &&
            packageWasReceived &&
            run.Stages.Any(stage =>
                stage.State == DiagnosticState.Failed &&
                RetryableStages.Contains(stage.Key));
    }
}
