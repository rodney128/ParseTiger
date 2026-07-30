namespace ParseTiger.Models;

/// <summary>The outcome of building a solution.</summary>
public sealed class BuildResult
{
    public bool Succeeded { get; set; }
    public string Output { get; set; } = string.Empty;
}
