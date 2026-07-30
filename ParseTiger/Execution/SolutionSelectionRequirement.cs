namespace ParseTiger.Execution;

public static class SolutionSelectionRequirement
{
    public const string Reminder =
        "Select a target solution first. Use Browse to choose the Visual Studio " +
        "solution ParseTiger should inspect and modify.";

    public static bool IsMissing(string? solutionPath) =>
        string.IsNullOrWhiteSpace(solutionPath);
}
