using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using ParseTiger.Build;
using ParseTiger.Models;

namespace ParseTiger;

public partial class MainWindow
{
    private Button? _buildProjectButton;
    private bool _standaloneBuildRunning;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        AddBuildProjectButton();
    }

    private void AddBuildProjectButton()
    {
        if (_buildProjectButton is not null || RunButton?.Parent is not Panel actions)
        {
            return;
        }

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

        int runIndex = actions.Children.IndexOf(RunButton);
        actions.Children.Insert(Math.Max(0, runIndex), _buildProjectButton);
    }

    private async void BuildProject_Click(object sender, RoutedEventArgs e)
    {
        if (_standaloneBuildRunning)
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
            ShowStandaloneBuildFailure(
                "Build could not start",
                $"The selected file does not exist: {targetPath}");
            return;
        }

        string extension = Path.GetExtension(targetPath);
        if (!extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            ShowStandaloneBuildFailure(
                "Build could not start",
                "Select a .sln, .slnx, or .csproj file.");
            return;
        }

        _standaloneBuildRunning = true;
        SetStandaloneBuildControlsEnabled(false);
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
                $"{outcome}. {errors:N0} error(s), {warnings:N0} warning(s), " +
                $"duration {elapsed.Elapsed:mm\\:ss}.";

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
            ShowStandaloneBuildFailure("Build could not start", detail);
        }
        catch (Exception exception)
        {
            elapsed.Stop();
            ShowStandaloneBuildFailure("Build could not start", exception.Message);
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            _standaloneBuildRunning = false;
            SetStandaloneBuildControlsEnabled(true);
            UpdateActionAvailability();
        }
    }

    private void SetStandaloneBuildControlsEnabled(bool enabled)
    {
        if (_buildProjectButton is not null)
        {
            _buildProjectButton.IsEnabled = enabled;
            _buildProjectButton.Content = enabled ? "Build Project" : "Building...";
        }

        BrowseButton.IsEnabled = enabled;
        SolutionPath.IsEnabled = enabled;
        RunButton.IsEnabled = enabled;
        RetryButton.IsEnabled = enabled && _lastFailedRun is not null;
    }

    private void ShowStandaloneBuildFailure(string stage, string detail)
    {
        AppendOutput($"{Environment.NewLine}✖ {stage}: {detail}{Environment.NewLine}");
        FailWorkflowActivity("Standalone build", detail);
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
