using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ParseTiger.Models;

namespace ParseTiger.Validation;

/// <summary>
/// Reads a Visual Studio solution (.sln or .slnx) and lists its projects. It only
/// reads — it does not build, restore, or otherwise interpret the solution. Each
/// project's kind is inferred from its project file (WPF / WinForms / language).
/// </summary>
public sealed class SolutionReader
{
    private static readonly Regex SlnProjectLine = new(
        """
        (?im)^\s*Project\("\{[^}]+\}"\)\s*=\s*"(?<name>[^"]+)"\s*,\s*"(?<path>[^"]+)"
        """,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public SolutionInfo Read(string solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            throw new ArgumentException("No target solution selected.");
        }

        if (!File.Exists(solutionPath))
        {
            throw new FileNotFoundException(
                "The solution file was not found: " + solutionPath);
        }

        string fullPath = Path.GetFullPath(solutionPath);
        string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        bool isSlnx = Path.GetExtension(fullPath)
            .Equals(".slnx", StringComparison.OrdinalIgnoreCase);

        var solution = new SolutionInfo
        {
            Name = Path.GetFileNameWithoutExtension(fullPath),
            Path = fullPath
        };

        foreach (string relative in isSlnx
                     ? ReadSlnxProjects(fullPath)
                     : ReadSlnProjects(fullPath))
        {
            solution.Projects.Add(BuildProject(directory, relative));
        }

        return solution;
    }

    private static IEnumerable<string> ReadSlnxProjects(string fullPath) =>
        XDocument.Load(fullPath)
            .Descendants()
            .Where(element => element.Name.LocalName.Equals(
                "Project", StringComparison.OrdinalIgnoreCase))
            .Select(element => (string?)element.Attribute("Path"))
            .Where(path => !string.IsNullOrWhiteSpace(path) && IsProjectFile(path!))
            .Select(path => path!)
            .ToArray();

    private static IEnumerable<string> ReadSlnProjects(string fullPath)
    {
        string text = File.ReadAllText(fullPath);
        return SlnProjectLine.Matches(text)
            .Select(match => match.Groups["path"].Value)
            .Where(IsProjectFile)
            .ToArray();
    }

    private static bool IsProjectFile(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase);
    }

    private static ProjectInfo BuildProject(string solutionDirectory, string relativePath)
    {
        string normalized = relativePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        string fullProjectPath =
            Path.GetFullPath(Path.Combine(solutionDirectory, normalized));

        return new ProjectInfo
        {
            Name = Path.GetFileNameWithoutExtension(fullProjectPath),
            RelativePath = relativePath,
            Directory = Path.GetDirectoryName(fullProjectPath) ?? string.Empty,
            Kind = DetermineKind(fullProjectPath)
        };
    }

    private static string DetermineKind(string projectFullPath)
    {
        string language = Path.GetExtension(projectFullPath).ToLowerInvariant() switch
        {
            ".vbproj" => "VB",
            ".fsproj" => "F#",
            _ => "C#"
        };

        try
        {
            if (File.Exists(projectFullPath))
            {
                string content = File.ReadAllText(projectFullPath);
                if (Regex.IsMatch(content, @"(?i)<\s*UseWPF\s*>\s*true\s*<"))
                {
                    return "WPF";
                }

                if (Regex.IsMatch(content, @"(?i)<\s*UseWindowsForms\s*>\s*true\s*<"))
                {
                    return "WinForms";
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Fall back to the language label if the project file can't be read.
        }

        return language;
    }
}
