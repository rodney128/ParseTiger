using System.Diagnostics;
using System.IO;

namespace ParseTiger.Build;

/// <summary>
/// Launches a freshly built project's executable. A separate service from building.
/// </summary>
public sealed class AppLauncher
{
    public void Launch(string projectFolder, string projectName)
    {
        string binDirectory = Path.Combine(projectFolder, "bin");
        if (!Directory.Exists(binDirectory))
        {
            throw new FileNotFoundException(
                $"No build output was found for {projectName}.");
        }

        string? executable = Directory
            .EnumerateFiles(binDirectory, projectName + ".exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (executable is null)
        {
            throw new FileNotFoundException(
                $"Could not find {projectName}.exe under {binDirectory}.");
        }

        ProcessStartInfo startInfo =
            CreateStartInfo(projectFolder, projectName, executable);
        if (Process.Start(startInfo) is null)
        {
            throw new InvalidOperationException(
                $"Windows did not start {projectName}.");
        }
    }

    public static ProcessStartInfo CreateStartInfo(
        string projectFolder,
        string projectName,
        string executable)
    {
        string root = Path.GetFullPath(projectFolder);
        string executablePath = Path.GetFullPath(executable);
        bool webProject = IsWebProject(root, projectName);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = webProject
                ? root
                : Path.GetDirectoryName(executablePath) ?? root,
            UseShellExecute = false
        };

        if (webProject)
        {
            // ASP.NET Core otherwise inherits ParseTiger's content root and looks
            // for wwwroot beside ParseTiger itself.
            startInfo.Environment["ASPNETCORE_CONTENTROOT"] = root;
        }

        return startInfo;
    }

    private static bool IsWebProject(string projectFolder, string projectName)
    {
        string expected = Path.Combine(projectFolder, projectName + ".csproj");
        IEnumerable<string> candidates = File.Exists(expected)
            ? [expected]
            : Directory.EnumerateFiles(
                projectFolder,
                "*.*proj",
                SearchOption.TopDirectoryOnly);

        return candidates.Any(path =>
        {
            string text = File.ReadAllText(path);
            return text.Contains(
                       "Microsoft.NET.Sdk.Web",
                       StringComparison.OrdinalIgnoreCase) ||
                   text.Contains(
                       "<Project Sdk=\"Microsoft.NET.Sdk.Razor\"",
                       StringComparison.OrdinalIgnoreCase);
        });
    }
}
