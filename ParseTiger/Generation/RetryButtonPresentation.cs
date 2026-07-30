using System.Text.RegularExpressions;

namespace ParseTiger.Generation;

public enum RetryButtonVisualState
{
    Unavailable,
    Available,
    Running
}

public sealed record RetryButtonPresentation(
    RetryButtonVisualState State,
    string ButtonText,
    string StatusText,
    string Background,
    string Foreground,
    double Opacity)
{
    public static RetryButtonPresentation Unavailable(string reason) =>
        new(
            RetryButtonVisualState.Unavailable,
            "Retry Failed Run",
            $"Retry unavailable: {reason}",
            "#E5E7EB",
            "#6B7280",
            0.62);

    public static RetryButtonPresentation Available(FailedRunRetryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        int compilerErrorCount = Regex.Matches(
                context.BuildAndRuntimeOutput,
                @"\berror\s+[A-Z]{2,}\d+\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Count;
        string reason = compilerErrorCount > 0
            ? $"previous build failed with {compilerErrorCount} " +
              $"{(compilerErrorCount == 1 ? "error" : "errors")}."
            : $"previous run failed at {DescribeStage(context.FailedStage)}.";

        return new(
            RetryButtonVisualState.Available,
            "Retry Failed Run",
            $"Retry available: {reason}",
            "#D97706",
            "#FFFFFF",
            1.0);
    }

    public static RetryButtonPresentation Running() =>
        new(
            RetryButtonVisualState.Running,
            "Retrying Failed Run...",
            "Retry in progress: repairing the previous failed run.",
            "#B45309",
            "#FFFFFF",
            0.9);

    private static string DescribeStage(string failedStage)
    {
        string value = failedStage?.Trim() ?? string.Empty;
        int separator = value.IndexOf(':');
        if (separator >= 0)
        {
            value = value[..separator].Trim();
        }

        return string.IsNullOrWhiteSpace(value) ? "an unknown stage" : value;
    }
}
