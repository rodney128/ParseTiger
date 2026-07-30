using System.IO;
using ParseTiger.Models;

namespace ParseTiger.Validation;

/// <summary>
/// Deterministic validation of a parsed <see cref="Package"/>. It never
/// interprets intent — it only confirms the package is well-formed and safe to
/// execute: known version, at least one operation, supported operation types,
/// relative non-traversing paths, and the fields each operation needs.
/// </summary>
public sealed class PackageValidator
{
    private static readonly HashSet<string> SupportedOperationTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "replace",
            "insert_before",
            "insert_after"
        };

    public ValidationResult Validate(Package package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var result = new ValidationResult();

        if (string.IsNullOrWhiteSpace(package.Version))
        {
            result.Errors.Add("Package version is missing.");
        }

        if (package.Operations is null || package.Operations.Count == 0)
        {
            result.Errors.Add("The package contains no operations.");
        }
        else
        {
            for (int index = 0; index < package.Operations.Count; index++)
            {
                ValidateOperation(package.Operations[index], index + 1, result);
            }
        }

        result.IsValid = result.Errors.Count == 0;
        return result;
    }

    private static void ValidateOperation(
        Operation operation,
        int number,
        ValidationResult result)
    {
        string where = $"Operation {number}";
        string type = operation.Type?.Trim() ?? string.Empty;

        if (type.Length == 0)
        {
            result.Errors.Add($"{where}: missing type.");
        }
        else if (!SupportedOperationTypes.Contains(type))
        {
            result.Errors.Add($"{where}: unsupported type \"{operation.Type}\".");
        }

        if (string.IsNullOrWhiteSpace(operation.Path))
        {
            result.Errors.Add($"{where}: missing path.");
        }
        else if (!IsSafeRelativePath(operation.Path))
        {
            result.Errors.Add(
                $"{where}: path \"{operation.Path}\" must be relative and must " +
                "not contain \"..\".");
        }

        if (SupportedOperationTypes.Contains(type))
        {
            if (string.IsNullOrEmpty(operation.OldText))
            {
                result.Errors.Add($"{where}: {type} requires a non-empty oldText anchor.");
            }

            if (operation.NewText is null)
            {
                result.Errors.Add($"{where}: {type} requires newText.");
            }
        }
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return false;
        }

        foreach (string segment in path.Split('/', '\\'))
        {
            if (segment == "..")
            {
                return false;
            }
        }

        return true;
    }
}
