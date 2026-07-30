namespace ParseTiger.Models;

/// <summary>A parsed package: a version and a list of operations to apply.</summary>
public sealed class Package
{
    public string? Version { get; set; }
    public List<Operation> Operations { get; set; } = new();
}

/// <summary>A single change operation within a package.</summary>
public sealed class Operation
{
    public string? Type { get; set; }
    public string? Path { get; set; }
    public string? OldText { get; set; }
    public string? NewText { get; set; }
}

/// <summary>The outcome of validating a package.</summary>
public sealed class ValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

/// <summary>The outcome of executing a package.</summary>
public sealed class ExecutionResult
{
    public bool Succeeded { get; set; }
    public string? Message { get; set; }
    public int? FailedOperationNumber { get; set; }
    public string? FailedPath { get; set; }
    public int MatchCount { get; set; }
    public string ExpectedOldText { get; set; } = string.Empty;
    public string NearbyExcerpt { get; set; } = string.Empty;
}

/// <summary>The computed before/after for a whole package (no files written).</summary>
public sealed class PackagePreview
{
    public List<OperationPreview> Operations { get; set; } = new();

    /// <summary>True only when every operation would apply cleanly.</summary>
    public bool CanApply =>
        Operations.Count > 0 && Operations.All(operation => operation.Applicable);

    public bool HasWarnings =>
        Operations.Any(operation => operation.Warnings.Count > 0);
}

/// <summary>The computed before/after for one operation.</summary>
public sealed class OperationPreview
{
    public int Number { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool Applicable { get; set; }
    public string? Error { get; set; }
    public string Before { get; set; } = string.Empty;
    public string After { get; set; } = string.Empty;
    public int OldTextLength { get; set; }
    public int MatchCount { get; set; }
    public string NearbyExcerpt { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = new();

    public string StatusText =>
        Applicable
            ? Warnings.Count == 0 ? "Will apply" : "Will apply with warning"
            : "Cannot apply: " + (Error ?? "unknown");
}
