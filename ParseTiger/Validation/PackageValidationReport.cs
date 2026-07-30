using System.Text;
using ParseTiger.Execution;
using ParseTiger.Models;

namespace ParseTiger.Validation;

public static class PackageValidationReport
{
    public static string ParseFailure(string detail) =>
        $"✗ JSON parse{Environment.NewLine}  {detail}{Environment.NewLine}" +
        "• Schema, paths, exact anchors, and safety checks were not run.";

    public static string Build(
        Package package,
        ValidationResult validation,
        PackagePreview? preview = null)
    {
        var text = new StringBuilder();
        text.AppendLine("✓ JSON parsed");
        text.AppendLine($"✓ Package version: {package.Version}");
        text.AppendLine(package.Operations.Count > 0
            ? $"✓ Operations present: {package.Operations.Count:N0}"
            : "✗ No operations present");
        text.AppendLine(validation.IsValid
            ? "✓ Schema, operation types, and relative paths are valid"
            : "✗ Package rule validation failed");

        foreach (string error in validation.Errors)
        {
            text.AppendLine("  • " + error);
        }
        foreach (string warning in validation.Warnings)
        {
            text.AppendLine("  ⚠ " + warning);
        }

        if (preview is not null)
        {
            foreach (OperationPreview operation in preview.Operations)
            {
                text.AppendLine(operation.Applicable
                    ? $"✓ Operation {operation.Number}: {operation.Path} has one exact match for its anchor"
                    : $"✗ Operation {operation.Number}: {operation.Path} — {operation.Error}");
                foreach (string warning in operation.Warnings)
                {
                    text.AppendLine($"  ⚠ {warning}");
                }
            }
        }
        else
        {
            text.AppendLine("• Exact current-file anchor check not run");
        }

        return text.ToString().TrimEnd();
    }
}
