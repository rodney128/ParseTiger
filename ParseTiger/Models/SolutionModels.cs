namespace ParseTiger.Models;

/// <summary>A read (not parsed-for-execution) view of a solution and its projects.</summary>
public sealed class SolutionInfo
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public List<ProjectInfo> Projects { get; set; } = new();
}

/// <summary>One project referenced by a solution.</summary>
public sealed class ProjectInfo
{
    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>Absolute directory that contains the project file.</summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>A short kind label for display, e.g. "WPF", "C#", "VB".</summary>
    public string Kind { get; set; } = "C#";
}
