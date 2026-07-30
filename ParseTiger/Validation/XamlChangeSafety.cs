using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ParseTiger.Models;

namespace ParseTiger.Validation;

/// <summary>
/// Performs deterministic XAML-specific checks against both the proposed
/// operation and the complete document that would be written.
/// </summary>
public static partial class XamlChangeSafety
{
    public static void AssessOperation(
        Operation operation,
        string resultingDocument,
        OperationPreview preview)
    {
        if (!IsXaml(operation.Path))
        {
            return;
        }

        string oldText = operation.OldText ?? string.Empty;
        string newText = operation.NewText ?? string.Empty;
        if (operation.Type?.Equals(
                "replace",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            string? boundaryError = FindParentBoundaryChange(oldText, newText);
            if (boundaryError is not null)
            {
                preview.Error = boundaryError;
                preview.Applicable = false;
                return;
            }
        }
        else
        {
            string? insertionError = FindUnbalancedInsertion(newText);
            if (insertionError is not null)
            {
                preview.Error = insertionError;
                preview.Applicable = false;
                return;
            }
        }

        string? structureError = ValidateDocument(resultingDocument);
        if (structureError is not null)
        {
            preview.Error = structureError;
            preview.Applicable = false;
            return;
        }

        string? scopeWarning = FindScopeWarning(operation);
        if (scopeWarning is not null)
        {
            preview.Warnings.Add(scopeWarning);
        }
    }

    public static string? ValidateDocument(string content)
    {
        try
        {
            _ = XDocument.Parse(
                content,
                LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            return null;
        }
        catch (XmlException exception)
        {
            return $"The resulting XAML is not well formed: {exception.Message}";
        }
    }

    public static bool IsXaml(string? path) =>
        path?.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) == true;

    private static string? FindParentBoundaryChange(
        string oldText,
        string newText)
    {
        Dictionary<string, int> before = GetFragmentBalances(oldText);
        Dictionary<string, int> after = GetFragmentBalances(newText);
        foreach (string tag in before.Keys
                     .Concat(after.Keys)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            int oldBalance = before.GetValueOrDefault(tag);
            int newBalance = after.GetValueOrDefault(tag);
            if (newBalance < oldBalance && newBalance < 0)
            {
                return $"Unsafe XAML boundary change: newText introduces a parent " +
                    $"closing tag </{tag}> that is not paired inside oldText. " +
                    "Use insert_before or insert_after with a stable unique anchor, " +
                    "or replace the complete balanced parent element.";
            }

            if (newBalance > oldBalance && oldBalance < 0)
            {
                return $"Unsafe XAML boundary change: the replacement removes a " +
                    $"parent closing tag </{tag}> that oldText does not open. " +
                    "Replace the complete balanced parent element instead.";
            }
        }

        return null;
    }

    private static string? FindUnbalancedInsertion(string newText)
    {
        foreach ((string tag, int balance) in GetFragmentBalances(newText))
        {
            if (balance != 0)
            {
                string boundary = balance < 0 ? $"</{tag}>" : $"<{tag}>";
                return "Unsafe localized XAML insertion: newText contains the " +
                    $"unbalanced boundary {boundary}. Insert a complete balanced " +
                    "element, or replace the complete balanced container.";
            }
        }

        return null;
    }

    private static Dictionary<string, int> GetFragmentBalances(string fragment)
    {
        var balances = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        foreach (Match match in XmlTag().Matches(fragment))
        {
            string tag = match.Groups["name"].Value;
            bool closing = match.Groups["closing"].Success;
            bool selfClosing = match.Groups["self"].Success;
            if (selfClosing)
            {
                continue;
            }

            balances[tag] = balances.GetValueOrDefault(tag) +
                (closing ? -1 : 1);
        }
        return balances;
    }

    private static string? FindScopeWarning(Operation operation)
    {
        string oldText = operation.OldText ?? string.Empty;
        string newText = operation.NewText ?? string.Empty;
        int oldLines = CountMeaningfulLines(oldText);
        int newLines = CountMeaningfulLines(newText);

        if (operation.Type?.Equals(
                "replace",
                StringComparison.OrdinalIgnoreCase) == true &&
            newLines >= oldLines + 8 &&
            newLines >= Math.Max(12, oldLines * 3))
        {
            return $"The replacement expands a {oldLines}-line match into " +
                $"{newLines} lines. Its scope is large relative to its anchor; " +
                "prefer a localized insert operation or a complete balanced " +
                "container replacement.";
        }

        bool isReplace = operation.Type?.Equals(
            "replace",
            StringComparison.OrdinalIgnoreCase) == true;
        if (!isReplace &&
            newLines > 150)
        {
            return $"The localized insertion adds {newLines} lines. Review the " +
                "scope before applying this unusually large change.";
        }

        return null;
    }

    private static int CountMeaningfulLines(string text) =>
        Math.Max(
            1,
            text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Count(line => !string.IsNullOrWhiteSpace(line)));

    [GeneratedRegex(
        @"<\s*(?<closing>/)?\s*(?<name>[A-Za-z_][\w:.-]*)(?:\s[^<>]*?)?\s*(?<self>/)?>",
        RegexOptions.CultureInvariant)]
    private static partial Regex XmlTag();
}
