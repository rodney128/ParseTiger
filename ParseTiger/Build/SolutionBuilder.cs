using System.Diagnostics;
using System.IO;
using System.Text;
using ParseTiger.Models;

namespace ParseTiger.Build;

/// <summary>
/// Builds a solution deterministically by shelling out to <c>dotnet build</c>,
/// streaming each line to the optional progress reporter as it arrives. A separate
/// service from execution and launching.
/// </summary>
public sealed class SolutionBuilder
{
    public BuildResult Build(string solutionPath, IProgress<string>? progress = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{solutionPath}\" --nologo -v m",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(solutionPath) ??
                Environment.CurrentDirectory
        };

        var output = new StringBuilder();
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output.AppendLine(e.Data);
                progress?.Report(e.Data + Environment.NewLine);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output.AppendLine(e.Data);
                progress?.Report(e.Data + Environment.NewLine);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        return new BuildResult
        {
            Succeeded = process.ExitCode == 0,
            Output = output.ToString().TrimEnd()
        };
    }
}
