namespace ParseTiger.Generation;

/// <summary>
/// Turns a plain-language change request (plus the current project files, for
/// grounding) into a package JSON string. This is the one AI-shaped seam;
/// everything downstream (validate, apply, build, run) stays deterministic.
/// Implementations are interchangeable and chosen at runtime.
/// </summary>
public interface IPackageGenerator
{
    Task<string> GenerateAsync(
        string request,
        string projectContext,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}
