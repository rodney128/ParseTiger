using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using ParseTiger.Build;
using ParseTiger.Models;

namespace ParseTiger;

public partial class MainWindow
{
    private Button? _runProjectButton;
    private Button? _buildProjectButton;
    private bool _standaloneActionRunning;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        AddStandaloneProjectButtons();
    }

    private void AddStandaloneProjectButtons()
    {
        if ((_runProjectButton is not null || _buildProjectButton is not null) ||
            RunButton?.Parent is not Panel actions)
        {
            return;
        }

        _runProjectButton = new Button
        {
            Name = "RunProjectButton",
            Content = "Run",
            MinWidth = 90,
            Height = 38,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(0, 0, 8, 0),
            FontWeight = FontWeights.SemiBold,
            ToolTip = "Launch the selected project as it exists now, without generating or applying an AI package."
        };
        _runProjectButton.Click += RunProject_Click;

        _buildProjectButton = new Button
        {
            Name = "BuildProjectButton",
            Content = "Build Project",
            MinWidth = 140,
            Height = 38,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(0, 0, 8, 0),
            FontWeight = FontWeights.SemiBold,
            ToolTip = "Build the selected solution or project without applying changes or launching the app."
        };
        _buildProjectButton.Click += BuildProject_Click;

        int generateIndex = actions.Children.IndexOf(RunButton);
        int insertionIndex = Math.Max(0, generateIndex);
        actions.Children.Insert(insertionIndex, _runProjectButton);
        actions.Children.Insert(insertionIndex + 1, _buildProjectButton);
    }

    private async void RunProject_Click(object sender, RoutedEventArgs e)
    {
        if (_standaloneActionRunning)
        {
            return;
        }

        if (!TryResolveSelectedProject(out _, out ProjectInfo? target) || target is null)
        {
            return;
        }

        _standaloneActionRunning = true;
        SetStandaloneControlsEnabled(false, "Running...");
        Busy.Visibility = Visibility.Visible;
        WorkflowTabs.SelectedIndex = 0;
        BuildOutput.Clear();
        ResetWorkflowActivity();

        string targetName = target.Name;
        SetStage("Running project...", $"Launching {targetName} without applying an AI package.");
        BeginWorkflowActivity("Standalone run", $"Launching {targetName}.");

        try
        {
            if (IsAndroidTarget(target))
            {
                string projectPath = ResolveProjectPath(target);
                AppendOutput($"▶ dotnet build \"{projectPath}\" -t:Run --nologo -v m{Environment.NewLine}");
                int exitCode = await RunDotnetTargetAsync(projectPath, "Run");
                if (exitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Android deployment/run returned exit code {exitCode}. See Output for details.");
                }
            }
            else
            {
                AppendOutput($"▶ Launching existing build output for {targetName}.{Environment.NewLine}");
                new AppLauncher().Launch(target.Directory, targetName);
            }

            string summary = $"Launched {targetName}. No source files were changed.";
            AppendOutput($"{Environment.NewLine}✓ {summary}{Environment.NewLine}");
            CompleteWorkflowActivity("Standalone run", summary);
            SetStage("Run started", summary);
        }
        catch (FileNotFoundException exception)
        {
            ShowStandaloneFailure(
                "Run could not start",
                exception.Message + " Build the project first, then try Run again.",
                "Standalone run");
        }
        catch (Exception exception)
        {
            ShowStandaloneFailure("Run failed", exception.Message, "Standalone run");
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            _standaloneActionRunning = false;
            SetStandaloneControlsEnabled(true);
            UpdateActionAvailability();
        }
    }

    private async void BuildProject_Click(object sender, RoutedEventArgs e)
    {
        if (_standaloneActionRunning)
        {
            return;
        }

        string targetPath = SolutionPath.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            ShowSolutionSelectionReminder();
            return;
        }

        targetPath = Path.GetFullPath(targetPath);
        if (!File.Exists(targetPath))
        {
            ShowStandaloneFailure(
                "Build could not start",
                $"The selected file does not exist: {targetPath}",
                "Standalone build");
            return;
        }

        string extension = Path.GetExtension(targetPath);
        if (!extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            ShowStandaloneFailure(
                "Build could not start",
                "Select a .sln, .slnx, or .csproj file.",
                "Standalone build");
            return;
        }

        _standaloneActionRunning = true;
        SetStandaloneControlsEnabled(false, "Building...");
        Busy.Visibility = Visibility.Visible;
        WorkflowTabs.SelectedIndex = 0;
        BuildOutput.Clear();
        ResetWorkflowActivity();

        string displayName = Path.GetFileName(targetPath);
        SetStage("Building project...", $"Building {displayName} without changing or launching the project.");
        BeginWorkflowActivity("Standalone build", $"Building {displayName}.");
        AppendOutput($"▶ dotnet build \"{targetPath}\" --nologo -v m{Environment.NewLine}");

        var elapsed = Stopwatch.StartNew();
        var progress = new Progress<string>(AppendOutput);

        try
        {
            BuildResult result = await Task.Run(() =>
                new SolutionBuilder().Build(targetPath, progress));
            elapsed.Stop();

            (int warnings, int errors) = ReadBuildCounts(result.Output);
            string outcome = result.Succeeded ? "Build succeeded" : "Build failed";
            string summary =
                $"{outcome} for {displayName}. {errors:N0} error(s), " +
                $"{warnings:N0} warning(s), duration {elapsed.Elapsed:mm\\:ss}.";

            AppendOutput(Environment.NewLine + summary + Environment.NewLine);
            _lastBuildHealth = result.Succeeded ? "Succeeded" : "Failed — see Output";
            RefreshPlanningPanels();

            if (result.Succeeded)
            {
                CompleteWorkflowActivity("Standalone build", summary);
                SetStage("Build succeeded", summary);
            }
            else
            {
                FailWorkflowActivity("Standalone build", summary);
                SetStage("Build failed", summary);
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            elapsed.Stop();
            string detail = exception.NativeErrorCode == 2
                ? "The dotnet command was not found. Install or repair the .NET SDK and try again."
                : $"The build process could not start: {exception.Message}";
            ShowStandaloneFailure("Build could not start", detail, "Standalone build");
        }
        catch (Exception exception)
        {
            elapsed.Stop();
            ShowStandaloneFailure("Build could not start", exception.Message, "Standalone build");
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            _standaloneActionRunning = false;
            SetStandaloneControlsEnabled(true);
            UpdateActionAvailability();
        }
    }

    private async Task<int> RunDotnetTargetAsync(string projectPath, string target)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add($"-t:{target}");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("m");

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                Dispatcher.Invoke(() => AppendOutput(eventArgs.Data + Environment.NewLine));
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                Dispatcher.Invoke(() => AppendOutput(eventArgs.Data + Environment.NewLine));
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Windows did not start the dotnet run process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static bool IsAndroidTarget(ProjectInfo target) =>
        target.Kind.Contains("Android", StringComparison.OrdinalIgnoreCase) ||
        target.Kind.Contains("MAUI", StringComparison.OrdinalIgnoreCase) ||
        target.RelativePath.Contains("Android", StringComparison.OrdinalIgnoreCase);

    private static string ResolveProjectPath(ProjectInfo target)
    {
        string projectPath = Path.IsPathRooted(target.RelativePath)
            ? target.RelativePath
            : Path.Combine(target.Directory, target.RelativePath);
        projectPath = Path.GetFullPath(projectPath);
        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException(
                $"The selected project file was not found: {projectPath}");
        }

        return projectPath;
    }

    private void SetStandaloneControlsEnabled(bool enabled, string? busyLabel = null)
    {
        if (_runProjectButton is not null)
        {
            _runProjectButton.IsEnabled = enabled;
            _runProjectButton.Content = enabled ? "Run" : busyLabel ?? "Working...";
        }

        if (_buildProjectButton is not null)
        {
            _buildProjectButton.IsEnabled = enabled;
            _buildProjectButton.Content = enabled ? "Build Project" : "Build Project";
        }

        BrowseButton.IsEnabled = enabled;
        SolutionPath.IsEnabled = enabled;
        RunButton.IsEnabled = enabled;
        RetryButton.IsEnabled = enabled && _lastFailedRun is not null;
    }

    private void ShowStandaloneFailure(string stage, string detail, string activity)
    {
        AppendOutput($"{Environment.NewLine}✖ {stage}: {detail}{Environment.NewLine}");
        FailWorkflowActivity(activity, detail);
        SetStage(stage, detail);
    }

    private static (int Warnings, int Errors) ReadBuildCounts(string output)
    {
        int warnings = ReadLastCount(output, @"(?im)^\s*(\d+)\s+Warning\(s\)\s*$");
        int errors = ReadLastCount(output, @"(?im)^\s*(\d+)\s+Error\(s\)\s*$");
        return (warnings, errors);
    }

    private static int ReadLastCount(string output, string pattern)
    {
        MatchCollection matches = Regex.Matches(output ?? string.Empty, pattern);
        return matches.Count > 0 && int.TryParse(matches[^1].Groups[1].Value, out int count)
            ? count
            : 0;
    }
}
