namespace ParseTiger.Generation;

/// <summary>
/// Normalizes and validates the user instruction that accompanies project context.
/// </summary>
public static class RequestedChange
{
    public static string Require(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new ArgumentException(
                "Describe the requested change before creating an AI request. " +
                "ParseTiger will not send an empty REQUESTED CHANGE.",
                nameof(value));
        }

        return normalized;
    }
}
