namespace ParseTiger.Generation;

/// <summary>
/// "No AI" generator: if the request already contains JSON it is used as-is (so
/// the deterministic pipeline can run from a hand-written package); otherwise it
/// reports that an AI model must be selected.
/// </summary>
public sealed class PassthroughPackageGenerator : IPackageGenerator
{
    public Task<string> GenerateAsync(
        string request,
        string projectContext,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        string trimmed = (request ?? string.Empty).Trim();
        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            return Task.FromResult(trimmed);
        }

        throw new InvalidOperationException(
            "Select an AI model to turn plain English into a package, " +
            "or paste a JSON package into Returned ParseTiger Package.");
    }
}
