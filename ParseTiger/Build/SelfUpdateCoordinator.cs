using System.Diagnostics;
using System.IO;

namespace ParseTiger.Build;

public sealed record SelfUpdatePlan(
    string StagedOutputDirectory,
    string DestinationOutputDirectory,
    string ExecutableName,
    int ProcessId,
    string ScriptPath,
    string LogPath);

/// <summary>
/// Installs a verified staged ParseTiger build after the running executable exits,
/// then restarts the normal executable from its original output directory.
/// </summary>
public sealed class SelfUpdateCoordinator
{
    private const string UpdaterScript =
        """
        param(
            [int]$TargetProcessId,
            [string]$SourceDirectory,
            [string]$DestinationDirectory,
            [string]$ExecutableName,
            [string]$LogPath
        )
        $ErrorActionPreference = 'Stop'
        try {
            Wait-Process -Id $TargetProcessId -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 250
            New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
            Get-ChildItem -LiteralPath $SourceDirectory -Force |
                Copy-Item -Destination $DestinationDirectory -Recurse -Force
            $executable = Join-Path $DestinationDirectory $ExecutableName
            Start-Process -FilePath $executable -WorkingDirectory $DestinationDirectory
            Set-Content -LiteralPath $LogPath -Value 'Installed and restarted.' -Encoding UTF8
            exit 0
        }
        catch {
            Set-Content -LiteralPath $LogPath -Value $_.Exception.ToString() -Encoding UTF8
            exit 1
        }
        """;

    public static string CreateStagingDirectory()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ParseTiger",
            "SelfUpdates",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static SelfUpdatePlan Prepare(
        string stagingRoot,
        string projectName,
        string currentExecutable,
        int processId)
    {
        string root = Path.GetFullPath(stagingRoot);
        string executableName = projectName + ".exe";
        string? stagedExecutable = Directory
            .EnumerateFiles(root, executableName, SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (stagedExecutable is null)
        {
            throw new FileNotFoundException(
                $"The verified staged build did not produce {executableName} under {root}.");
        }

        string destinationExecutable = Path.GetFullPath(currentExecutable);
        string destinationDirectory =
            Path.GetDirectoryName(destinationExecutable) ??
            throw new InvalidOperationException(
                "The running ParseTiger executable has no output directory.");
        string stagedDirectory =
            Path.GetDirectoryName(stagedExecutable) ??
            throw new InvalidOperationException(
                "The staged ParseTiger executable has no output directory.");
        if (stagedDirectory.Equals(
                destinationDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A self-update must be built outside the running output directory.");
        }

        return new SelfUpdatePlan(
            stagedDirectory,
            destinationDirectory,
            Path.GetFileName(destinationExecutable),
            processId,
            Path.Combine(root, "install-and-restart.ps1"),
            Path.Combine(root, "install-and-restart.log"));
    }

    public void Schedule(SelfUpdatePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        File.WriteAllText(plan.ScriptPath, UpdaterScript);
        ProcessStartInfo startInfo = CreateStartInfo(plan);
        if (Process.Start(startInfo) is null)
        {
            throw new InvalidOperationException(
                "Windows did not start the ParseTiger self-update helper.");
        }
    }

    public static ProcessStartInfo CreateStartInfo(SelfUpdatePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(plan.ScriptPath);
        startInfo.ArgumentList.Add(plan.ProcessId.ToString());
        startInfo.ArgumentList.Add(plan.StagedOutputDirectory);
        startInfo.ArgumentList.Add(plan.DestinationOutputDirectory);
        startInfo.ArgumentList.Add(plan.ExecutableName);
        startInfo.ArgumentList.Add(plan.LogPath);
        return startInfo;
    }
}
