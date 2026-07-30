using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using ParseTiger.Build;
using ParseTiger.Diagnostics;
using ParseTiger.Execution;
using ParseTiger.Generation;
using ParseTiger.Models;
using ParseTiger.Packages;
using ParseTiger.Reset;
using ParseTiger.State;
using ParseTiger.Validation;

namespace ParseTiger;

/// <summary>
/// Copies a self-contained project-aware request for an external AI. Run consumes
/// only returned JSON, then validates, applies, builds, and launches, stopping at
/// the first failed stage.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly HashSet<string> SourceExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".cshtml", ".razor", ".xaml", ".csproj", ".vbproj", ".fsproj",
            ".props", ".targets", ".xml", ".json", ".config", ".css", ".js",
            ".html", ".htm"
        };

    private readonly Stopwatch _stopwatch = new();
    private readonly DispatcherTimer _timer;
    private readonly BaselineService _baselineService = new();
    private readonly BaselineService _checkpointService = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ParseTiger",
        "Checkpoints"));
    private readonly ProjectStateService _projectState = new();
    private readonly ProjectDefaultTemplateService _defaultTemplateService = new();
    private readonly IProviderSettingsStore _providerSettings =
        new WindowsProviderSettingsStore();
    private readonly IReadOnlyList<GeneratorOption> _providerOptions =
    [
        new("Paste JSON / Manual", GeneratorProvider.PasteJson),
        new("Gemini API", GeneratorProvider.Gemini),
        new("OpenAI API", GeneratorProvider.OpenAI)
    ];
    private ProjectBackup? _lastBackup;
    private AiRequestContext? _aiRequestContext;
    private string _stage = "Ready";
    private string _stageDetail =
        "Select a solution and choose how to generate the package.";
    private bool _isFormattingReturnedPackage;
    private readonly ObservableCollection<RunDiagnostic> _runHistory = new();
    private readonly ProjectSessionMemory _projectMemory = new();
    private readonly SessionEventLog _sessionLog = new();
    private RunDiagnosticsCollector? _currentDiagnostics;
    private ProjectStateSnapshot? _manualPackageContext;
    private ManualUploadSession? _manualUpload;
    private FailedRunRetryContext? _lastFailedRun;
    private bool _retryRequested;
    private bool _isRetryRunning;
    private AiProjectFacts? _projectFacts;
    private string _lastBuildHealth = "Not run this session";
    private string _healthBuildSolution = string.Empty;
    private int _healthBuildVersion;

    public MainWindow()
    {
        InitializeComponent();
        DataObject.AddPastingHandler(
            ReturnedPackageBox,
            ReturnedPackageBox_Pasting);
        ProviderSelector.ItemsSource = _providerOptions;
        ProviderSelector.SelectedIndex = 0;
        RunHistorySelector.ItemsSource = _runHistory;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshStatus();
        UpdateProviderUi();
        _sessionLog.Add("ParseTiger session started.");
        RefreshPlanningPanels();
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        _retryRequested = false;
        await RunWorkflowAsync();
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFailedRun is null)
        {
            MessageBox.Show(
                "There is no failed Generate & Run attempt available to retry.",
                "Retry Unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (SelectedProvider.Provider == GeneratorProvider.PasteJson)
        {
            MessageBox.Show(
                "Select Gemini or OpenAI before retrying. Retry sends the saved " +
                "failure evidence to the selected provider.",
                "Provider Required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _retryRequested = true;
        _isRetryRunning = true;
        UpdateRetryAvailability();
        await RunWorkflowAsync();
    }

    private async Task RunWorkflowAsync()
    {
        bool mutationStarted = false;
        bool workflowSucceeded = false;
        bool preserveBuiltChanges = false;
        bool retrying = _retryRequested;
        FailedRunRetryContext? retryContext = retrying ? _lastFailedRun : null;
        BeginDiagnostics();
        if (retrying && retryContext is not null)
        {
            _currentDiagnostics!.Run.RepairAttemptNumber =
                retryContext.RetryAttemptNumber;
            _currentDiagnostics.Run.RepairHistoryCount =
                retryContext.Attempts.Count;
            _currentDiagnostics.Run.CumulativeRepairHistoryIncluded = true;
            _currentDiagnostics.Run.LatestRepairFailureStage =
                retryContext.FailedStage;
        }
        SetResetControlsEnabled(false);
        BuildOutput.Clear();
        Busy.Visibility = Visibility.Visible;
        _stopwatch.Restart();
        _timer.Start();
        var progress = new Progress<string>(AppendOutput);
        try
        {
            SetStage(
                retrying ? "Preparing failed-run retry..." : "Starting...",
                retrying
                    ? "Reloading the current project and assembling the previous " +
                      "package, failed stage, diagnostics, and compiler/runtime output."
                    : null);
            if (retrying)
            {
                RecordStateEvent(
                    $"Retry Failed Run attempt {retryContext!.RetryAttemptNumber} " +
                    $"selected; cumulative history with " +
                    $"{retryContext.Attempts.Count:N0} prior failure(s) was included. " +
                    $"Latest failure stage: {retryContext.FailedStage}.");
            }
            AppendOutput(
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] " +
                $"{(retrying ? "Retry Failed Run" : RunButton.Content)} started." +
                $"{Environment.NewLine}" +
                $"Runtime executable: {Environment.ProcessPath ?? "unknown"}" +
                Environment.NewLine +
                $"Runtime assembly: {typeof(MainWindow).Assembly.Location}" +
                Environment.NewLine +
                $"Assembly written: " +
                $"{File.GetLastWriteTime(typeof(MainWindow).Assembly.Location):O}" +
                Environment.NewLine);
            await Dispatcher.Yield(DispatcherPriority.Background);

            // Resolve the target project (needed for grounding, applying, building).
            string solutionPath = SolutionPath.Text?.Trim() ?? string.Empty;
            _currentDiagnostics!.Run.SolutionPath = solutionPath;
            _currentDiagnostics.Start(
                "project",
                "Resolving the selected solution and target project.");
            if (string.IsNullOrWhiteSpace(solutionPath))
            {
                DiagnosticFail(
                    "project",
                    DiagnosticFailureKind.ProjectLoad,
                    "No target solution was selected.");
                Fail("Ready", "Select a target solution first.");
                return;
            }

            SolutionInfo solution;
            ProjectInfo? target;
            try
            {
                AppendOutput($"Target: resolving {solutionPath}.{Environment.NewLine}");
                solution = new SolutionReader().Read(solutionPath);
                target = ResolveTarget(solution);
            }
            catch (Exception exception)
            {
                DiagnosticFail(
                    "project",
                    DiagnosticFailureKind.ProjectLoad,
                    exception.Message);
                Fail("Failed", "Solution error: " + exception.Message);
                return;
            }

            if (target is null)
            {
                DiagnosticFail(
                    "project",
                    DiagnosticFailureKind.ProjectLoad,
                    "No target project was found in the solution.");
                Fail("Failed", "No target project was found in the solution.");
                return;
            }

            _currentDiagnostics.Run.ProjectName = target.Name;
            _currentDiagnostics.Succeed(
                "project",
                $"Resolved {target.Name} ({target.Kind}) at {target.Directory}.");
            AppendOutput(
                $"Target: {target.Name} ({target.Kind}) at {target.Directory}." +
                Environment.NewLine);
            _sessionLog.Add($"Resolved {target.Name} from {Path.GetFileName(solutionPath)}.");
            RefreshSessionViews();

            ProjectIdentity identity = CreateIdentity(solution, target);
            CurrentStateCheck stateCheck = _projectState.OpenOrSelect(identity);
            RecordStateCheck(stateCheck);
            ProjectStateSnapshot runContext = stateCheck.Snapshot;
            _currentDiagnostics.Run.SessionBaselineId =
                _projectState.SessionBaseline?.Id ?? string.Empty;
            _currentDiagnostics.Run.ContextSnapshotId = runContext.Id;

            // 1. Obtain a package through the selected provider. Direct providers
            // only generate text; the same deterministic pipeline validates and
            // applies every result.
            GeneratorOption selectedProvider = SelectedProvider;
            AppendOutput(
                $"Provider: selected {selectedProvider.Display}." +
                Environment.NewLine);
            string json;
            if (selectedProvider.Provider == GeneratorProvider.PasteJson)
            {
                SkipProviderDiagnostics("Manual package mode.");
                if (_manualPackageContext is not null &&
                    SameTarget(_manualPackageContext.Identity, identity))
                {
                    runContext = _manualPackageContext;
                    AppendOutput(
                        $"Context: using copied AI-request snapshot " +
                        $"{runContext.Id}.{Environment.NewLine}");
                }
                else
                {
                    CurrentStateCheck manualCheck =
                        _projectState.EnsureCurrent(identity, "manual package run");
                    RecordStateCheck(manualCheck);
                    runContext = manualCheck.Snapshot;
                }
                FormatReturnedPackageIfValid();
                json = ReturnedPackageBox.Text ?? string.Empty;
                if (string.IsNullOrWhiteSpace(json))
                {
                    Fail("Ready", "Paste a JSON package into Returned ParseTiger Package first.");
                    return;
                }

                SetStage("Reading pasted package...");
                AppendOutput(
                    $"Using pasted JSON (Manual).{Environment.NewLine}{Environment.NewLine}");
            }
            else
            {
                string request = retrying
                    ? FailedRunRetryPrompt.Build(retryContext!)
                    : DirectRequestBox.Text?.Trim() ?? string.Empty;
                if (retrying)
                {
                    AppendOutput(
                        $"Retry attempt {retryContext!.RetryAttemptNumber}: " +
                        $"cumulative repair history included." + Environment.NewLine +
                        $"  Prior failed attempts: {retryContext.Attempts.Count:N0}" +
                        Environment.NewLine +
                        $"  Latest failure stage: {retryContext.FailedStage}" +
                        Environment.NewLine +
                        $"  Diagnostics: {retryContext.Diagnostics.Length:N0} characters" +
                        Environment.NewLine +
                        $"  Build/runtime errors: " +
                        $"{retryContext.BuildAndRuntimeOutput.Length:N0} characters" +
                        Environment.NewLine +
                        $"  Affected files: {retryContext.AffectedPaths.Count:N0}" +
                        Environment.NewLine +
                        "  Original request: included" + Environment.NewLine +
                        "  All prior provider outputs and errors: included" +
                        Environment.NewLine +
                        "  Package rules and project constraints: included" +
                        Environment.NewLine +
                        "  Current file contents: refreshed and appended to the request" +
                        Environment.NewLine);
                }
                if (request.Length == 0)
                {
                    Fail("Ready", "Describe the requested change before using direct generation.");
                    return;
                }

                ProviderRequestPreparation preparation =
                    retrying
                        ? new ProviderRequestPreparation(false, request)
                        : ProviderRequestPreparation.Parse(request);
                ProjectBackup preRunBackup = runContext.ToBackup();
                if (preparation.ResetProjectBaselineFirst)
                {
                    if (!_baselineService.Exists(target.Directory))
                    {
                        Fail(
                            "Project Baseline required",
                            "This request asks to start from the Project Baseline, " +
                            "but no Project Baseline has been saved.");
                        return;
                    }

                    SetStage("Resetting to Project Baseline...");
                    AppendOutput(
                        "Project Baseline reset: restoring deterministically before " +
                        "provider generation." + Environment.NewLine);
                    _lastBackup = preRunBackup;
                    mutationStarted = true;
                    BackupService.Restore(_baselineService.Load(target.Directory));
                    runContext = _projectState.RefreshCurrent(
                        identity,
                        "Project Baseline reset");
                    _projectState.RecordProjectBaselineReset(runContext);
                    RecordStateEvent(
                        $"Project Baseline reset completed; Current State " +
                        $"{runContext.Id}.");
                    _currentDiagnostics.Run.ContextSnapshotId = runContext.Id;
                    request = preparation.CodeChangeRequest;
                    if (string.IsNullOrWhiteSpace(request))
                    {
                        Fail(
                            "No code change requested",
                            "The deterministic Project Baseline reset completed, " +
                            "but no code-change request remained for the provider.");
                        return;
                    }
                }
                else
                {
                    CurrentStateCheck providerCheck =
                        _projectState.EnsureCurrent(identity, "provider preflight");
                    RecordStateCheck(providerCheck);
                    runContext = providerCheck.Snapshot;
                }

                SetStage("Loading provider credential...");
                _currentDiagnostics.Start(
                    "credential",
                    $"Loading the saved {selectedProvider.Display} credential.");
                AppendOutput("Credential load: checking secure provider storage." +
                    Environment.NewLine);
                ProviderCredentialSource credentialSource;
                try
                {
                    credentialSource =
                        _providerSettings.GetApiKeySource(selectedProvider.Provider);
                }
                catch (Exception exception)
                {
                    DiagnosticFail(
                        "credential",
                        DiagnosticFailureKind.Credential,
                        exception.Message);
                    FailWithDialog(
                        "Credential check failed",
                        "ParseTiger could not read the provider credential. " +
                        exception.Message);
                    return;
                }

                AppendOutput(
                    $"Credential load: found in " +
                    $"{DescribeCredentialSource(credentialSource)}." +
                    Environment.NewLine);
                if (credentialSource == ProviderCredentialSource.None)
                {
                    DiagnosticFail(
                        "credential",
                        DiagnosticFailureKind.Credential,
                        "No saved credential was found.");
                    FailWithDialog(
                        "Provider not configured",
                        $"{selectedProvider.Display} API key required. Open AI Provider " +
                        "Settings and add a key. No files were changed.");
                    return;
                }

                string model = _providerSettings.GetModel(selectedProvider.Provider);
                if (model.Length == 0)
                {
                    DiagnosticFail(
                        "credential",
                        DiagnosticFailureKind.Credential,
                        "No model is configured.");
                    Fail(
                        "Provider not configured",
                        "Choose a model in AI Provider Settings.");
                    return;
                }

                bool credentialVerified =
                    _providerSettings.IsApiKeyVerified(selectedProvider.Provider);
                _currentDiagnostics.Succeed(
                    "credential",
                    credentialVerified
                        ? "Credential loaded from secure storage; previously verified."
                        : "Credential loaded; this request will verify it.");
                AppendOutput(
                    "Credential verification: " +
                    (credentialVerified
                        ? "previously verified."
                        : "pending; this request will verify it with the provider.") +
                    Environment.NewLine);
                SetStage($"Creating context for {GetProviderName(selectedProvider)}...");
                _currentDiagnostics.Start(
                    "context",
                    "Collecting current project files for grounding.");
                AppendOutput(
                    $"Model: {model}.{Environment.NewLine}" +
                    (retrying
                        ? $"Retry source: attempt {retryContext!.RetryAttemptNumber}; " +
                          $"latest failed stage {retryContext.FailedStage}; " +
                          $"{retryContext.Attempts.Count:N0} prior failure(s); " +
                          $"{retryContext.AffectedPaths.Count:N0} affected file(s)." +
                          Environment.NewLine
                        : string.Empty) +
                    $"Request: {request.Length:N0} characters." +
                    $"{Environment.NewLine}Context creation: collecting current files." +
                    Environment.NewLine);
                await Dispatcher.Yield(DispatcherPriority.Background);
                try
                {
                    ProjectContextSnapshot currentContext = BuildProjectContext(
                        runContext,
                        retrying ? retryContext!.OriginalRequest : request,
                        retryContext?.AffectedPaths);
                    _currentDiagnostics.Succeed(
                        "context",
                        $"Collected {currentContext.ContentFileCount:N0} files " +
                        $"({currentContext.Text.Length:N0} characters).");
                    AppendOutput(
                        $"Context creation: complete; " +
                        $"{currentContext.ContentFileCount:N0} files, " +
                        $"{currentContext.Text.Length:N0} characters." +
                        Environment.NewLine);
                    IPackageGenerator generator =
                        selectedProvider.Create(model, _providerSettings);
                    AppendOutput(
                        $"Request construction: using {generator.GetType().Name}." +
                        Environment.NewLine);
                    SetStage($"Generating with {GetProviderName(selectedProvider)}...");
                    _currentDiagnostics.Start(
                        "provider-request",
                        $"Sending request with {generator.GetType().Name}; model {model}.");
                    json = await generator.GenerateAsync(
                        request,
                        currentContext.Text,
                        progress,
                        CancellationToken.None);
                    _currentDiagnostics.Succeed(
                        "provider-request",
                        $"Provider completed the request using model {model}.");
                    _currentDiagnostics.Start(
                        "provider-response",
                        "Reading the returned package.");
                    _currentDiagnostics.Succeed(
                        "provider-response",
                        $"Received {json.Length:N0} package characters.");
                    RefreshProviderConfigurationStatus();
                }
                catch (ProviderRequestException exception)
                {
                    DiagnosticFail(
                        "provider-request",
                        ClassifyProviderFailure(exception.Kind),
                        exception.Message);
                    RefreshProviderConfigurationStatus();
                    FailWithDialog(
                        $"{GetProviderName(selectedProvider)} " +
                        $"{DescribeProviderFailure(exception.Kind)}",
                        exception.Message + Environment.NewLine +
                        "No project files were changed.");
                    return;
                }
                catch (Exception exception)
                {
                    string diagnosticStage = _currentDiagnostics.Run.Stages
                        .First(stage => stage.Key == "context").State ==
                        DiagnosticState.Running
                        ? "context"
                        : "provider-request";
                    DiagnosticFail(
                        diagnosticStage,
                        diagnosticStage == "context"
                            ? DiagnosticFailureKind.ContextCreation
                            : DiagnosticFailureKind.Provider,
                        exception.Message);
                    RefreshProviderConfigurationStatus();
                    FailWithDialog(
                        "Generation failed",
                        exception.Message + Environment.NewLine +
                        "No project files were changed.");
                    return;
                }

                ReturnedPackageBox.Text = json;
                FormatReturnedPackageIfValid();
                json = ReturnedPackageBox.Text ?? json;
                JsonBox.Text = json;
                AppendOutput(
                    $"Response processing: generated package displayed " +
                    $"({json.Length:N0} characters). Continuing automatically " +
                    $"to Parse, Validate, Apply, Build, and Launch." +
                    Environment.NewLine);
                await Dispatcher.Yield(DispatcherPriority.Background);
            }

            _currentDiagnostics.Run.ContextSnapshotId = runContext.Id;
            RecordStateEvent(
                $"Run bound to Current State snapshot {runContext.Id}, captured " +
                $"{runContext.CapturedAt:O}.");
            if (!_projectState.DiskMatches(runContext))
            {
                ProjectStateSnapshot refreshed = _projectState.RefreshCurrent(
                    identity,
                    "files changed while the provider/package was in flight");
                RecordStateEvent(
                    $"Context changed before validation; refreshed to " +
                    $"{refreshed.Id}. The package was not applied.");
                Fail(
                    "Context changed",
                    $"Project files changed after context snapshot {runContext.Id} " +
                    $"was created. Current State is now {refreshed.Id}. " +
                    "Generate the package again.");
                return;
            }

            JsonBox.Text = json;

            // 2. Validate (deterministic)
            SetStage("Validating...");
            _currentDiagnostics.Start("parse", "Parsing package JSON.");
            AppendOutput(
                $"{Environment.NewLine}▶ Parse: reading package JSON." +
                Environment.NewLine);
            Package package;
            try
            {
                package = new PackageParser().Parse(json);
            }
            catch (FormatException exception)
            {
                ValidationReportText.Text =
                    PackageValidationReport.ParseFailure(exception.Message);
                DiagnosticFail(
                    "parse",
                    DiagnosticFailureKind.PackageParse,
                    exception.Message);
                Fail("Validation failed", "Parse error: " + exception.Message);
                return;
            }
            _currentDiagnostics.Succeed(
                "parse",
                $"Parsed version {package.Version}; " +
                $"{package.Operations.Count:N0} operations.");
            _currentDiagnostics.Run.OperationCount = package.Operations.Count;
            _currentDiagnostics.Run.ModifiedFiles.Clear();
            foreach (string path in package.Operations
                         .Select(operation => operation.Path ?? string.Empty)
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _currentDiagnostics.Run.ModifiedFiles.Add(path);
            }
            _currentDiagnostics.Run.ProjectChangesDetail =
                "The package has not been applied yet.";
            RefreshRunSummary();
            AppendOutput(
                "✓ Parse succeeded: JSON shape accepted." + Environment.NewLine);

            _currentDiagnostics.Start(
                "validate",
                "Checking package schema, operation types, and paths.");
            AppendOutput("▶ Validate: checking package rules." + Environment.NewLine);
            Models.ValidationResult validation = new PackageValidator().Validate(package);
            ValidationReportText.Text =
                PackageValidationReport.Build(package, validation);
            if (!validation.IsValid)
            {
                DiagnosticFail(
                    "validate",
                    DiagnosticFailureKind.Validation,
                    string.Join(" | ", validation.Errors));
                Fail("Validation failed", string.Join(Environment.NewLine, validation.Errors));
                return;
            }
            _currentDiagnostics.Succeed(
                "validate",
                $"Package rules passed for {package.Operations.Count:N0} operations.");
            AppendOutput(
                $"✓ Validate succeeded ({package.Operations.Count:N0} operations)." +
                Environment.NewLine);

            _currentDiagnostics.Start(
                "preflight",
                "Checking every replace operation against current file contents.");
            AppendOutput(
                "▶ Replace preflight: checking exact oldText matches." +
                Environment.NewLine);
            PackagePreview preflight =
                new PackageExecutor().Preview(package, runContext);
            ValidationReportText.Text =
                PackageValidationReport.Build(package, validation, preflight);
            RecordOperationPreflight(preflight);
            if (!preflight.CanApply)
            {
                string detail = BuildPreflightFailure(preflight);
                DiagnosticFail(
                    "preflight",
                    DiagnosticFailureKind.Validation,
                    detail);
                Fail("Replace preflight failed", detail);
                return;
            }
            _currentDiagnostics.Succeed(
                "preflight",
                $"All {preflight.Operations.Count:N0} operations have one unique match.");
            AppendOutput(
                $"✓ Replace preflight succeeded " +
                $"({preflight.Operations.Count:N0} operations)." +
                Environment.NewLine);

            // 3. Apply (deterministic) — back up the target files first so any
            // later failure can roll them back exactly.
            ProjectFileChangeAssessment protectedChanges =
                new ProjectFileChangeGuard().Assess(package);
            if (protectedChanges.RequiresExplicitConfirmation)
            {
                MessageBoxResult authorization = MessageBox.Show(
                    protectedChanges.BuildConfirmationMessage(),
                    "Protected Project-File Change",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (authorization != MessageBoxResult.Yes)
                {
                    Fail(
                        "Authorization required",
                        "The package was rejected before apply because its " +
                        "project-file changes were not explicitly confirmed.");
                    return;
                }

                AppendOutput(
                    $"{Environment.NewLine}✓ Explicitly confirmed protected " +
                    $"project-file change.{Environment.NewLine}");
            }

            SelfModificationAssessment selfChanges =
                SelfModificationGuard.Assess(target, package);
            if (selfChanges.RequiresConfirmation)
            {
                MessageBoxResult authorization = MessageBox.Show(
                    selfChanges.BuildMessage(),
                    "ParseTiger Self-Modification Safeguard",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (authorization != MessageBoxResult.Yes)
                {
                    Fail(
                        "Self-modification cancelled",
                        "The package was stopped before apply. No files were changed.");
                    return;
                }

                _sessionLog.Add(
                    selfChanges.TouchesCoreSafetyCode
                        ? "User confirmed a safety-critical ParseTiger self-change."
                        : "User confirmed a ParseTiger self-change.");
                RefreshSessionViews();
            }

            SetStage("Creating safety checkpoint...");
            _currentDiagnostics.Start(
                "backup",
                "Capturing the complete pre-run project state.");
            AppendOutput("Apply: capturing pre-run rollback snapshot." + Environment.NewLine);
            if (!mutationStarted)
            {
                _lastBackup = runContext.ToBackup();
                mutationStarted = true;
            }
            _checkpointService.Save(target.Directory, target.Kind);
            _currentDiagnostics.Succeed(
                "backup",
                $"Captured {_lastBackup!.Files.Count:N0} files.");
            GitRecoveryState gitCheckpoint =
                RecoveryStateInspector.InspectGit(target.Directory);
            AppendOutput(
                $"✓ Safety checkpoint captured ({_lastBackup.Files.Count:N0} files). " +
                (gitCheckpoint.Available
                    ? $"Git repository also available at {gitCheckpoint.RepositoryRoot}."
                    : "Exact-byte project snapshot will provide recovery.") +
                Environment.NewLine);
            _sessionLog.Add(
                $"Safety checkpoint captured before apply: " +
                $"{_lastBackup.Files.Count:N0} files.");
            SetStage("Applying...");
            UpdateUndo();
            _currentDiagnostics.Start(
                "apply",
                $"Applying {package.Operations.Count:N0} operations.");
            AppendOutput(
                $"▶ Apply: executing {package.Operations.Count:N0} operations." +
                Environment.NewLine);
            ExecutionResult apply;
            try
            {
                apply = new PackageExecutor().Apply(package, runContext);
            }
            catch (Exception exception)
            {
                DiagnosticFail(
                    "apply",
                    DiagnosticFailureKind.Apply,
                    exception.Message);
                RollBack("Apply failed", "Apply error: " + exception.Message);
                return;
            }

            if (!apply.Succeeded)
            {
                RecordApplyFailure(apply);
                string failedAt = apply.FailedOperationNumber is null
                    ? "Apply failed"
                    : $"Apply failed at operation {apply.FailedOperationNumber} — " +
                      $"{apply.FailedPath}";
                RollBack(failedAt, apply.Message ?? "Apply failed.");
                return;
            }

            _currentDiagnostics.Succeed("apply", apply.Message ?? "Apply succeeded.");
            _currentDiagnostics.Run.ProjectChangesPersisted = true;
            _currentDiagnostics.Run.ProjectChangesDetail =
                $"{_currentDiagnostics.Run.ModifiedFiles.Count:N0} file(s) were " +
                $"modified by {_currentDiagnostics.Run.OperationCount:N0} operation(s).";
            RefreshRunSummary();
            AppendOutput("✓ Apply succeeded." + Environment.NewLine);
            ProjectStateSnapshot appliedState = _projectState.RefreshCurrent(
                identity,
                "successful apply");
            RecordStateEvent(
                $"Current State refreshed after apply: {appliedState.Id}.");
            AppendOutput($"{Environment.NewLine}{Environment.NewLine}✓ {apply.Message}{Environment.NewLine}");

            // 4. Build the solution — auto-roll-back if it fails.
            SetStage("Building...");
            _currentDiagnostics.Start(
                "build",
                $"Building {Path.GetFileName(solutionPath)}.");
            AppendOutput($"{Environment.NewLine}▶ Building {Path.GetFileName(solutionPath)}…{Environment.NewLine}");
            BuildResult build = await Task.Run(() =>
                new SolutionBuilder().Build(solutionPath, progress));
            if (!build.Succeeded)
            {
                _lastBuildHealth = "Failed — see Output and Diagnostics";
                RefreshPlanningPanels();
                DiagnosticFail(
                    "build",
                    DiagnosticFailureKind.Build,
                    SummarizeBuildFailure(build.Output));
                RollBack("Build failed — changes reverted",
                    "Build failed, so your files were restored to their previous state.");
                return;
            }
            _currentDiagnostics.Succeed("build", "Build completed successfully.");
            _lastBuildHealth = "Succeeded";
            RefreshPlanningPanels();
            preserveBuiltChanges = true;
            _currentDiagnostics.Run.ProjectChangesDetail =
                $"{_currentDiagnostics.Run.ModifiedFiles.Count:N0} file(s) were " +
                "successfully modified and the solution built successfully.";
            RefreshRunSummary();
            AppendOutput("✓ Build succeeded." + Environment.NewLine);
            ProjectStateSnapshot builtState = _projectState.RefreshCurrent(
                identity,
                "successful build");
            RecordStateEvent(
                $"Current State confirmed after build: {builtState.Id}.");

            // 5. Launch
            SetStage("Running...");
            _currentDiagnostics.Start("launch", $"Launching {target.Name}.");
            AppendOutput($"Run: launching {target.Name}.{Environment.NewLine}");
            try
            {
                new AppLauncher().Launch(target.Directory, target.Name);
            }
            catch (Exception exception)
            {
                string launchFailure = DescribeLaunchFailure(exception);
                DiagnosticFail(
                    "launch",
                    DiagnosticFailureKind.Launch,
                    launchFailure);
                _currentDiagnostics.Run.ProjectChangesPersisted = true;
                _currentDiagnostics.Run.ProjectChangesDetail =
                    $"{_currentDiagnostics.Run.ModifiedFiles.Count:N0} file(s) were " +
                    "successfully modified and built. Only application launch failed. " +
                    "Undo remains available.";
                _currentDiagnostics.Skip(
                    "rollback",
                    "No rollback: Apply and Build succeeded; only Launch failed.");
                SetStage(
                    "Launch failed — project changes kept",
                    "The project files were successfully modified and built. " +
                    "Only application launch failed. You can use Undo.");
                AppendOutput(
                    $"{Environment.NewLine}PROJECT CHANGES SAVED{Environment.NewLine}" +
                    $"✓ Package applied: {_currentDiagnostics.Run.OperationCount:N0} " +
                    $"operation(s), {_currentDiagnostics.Run.ModifiedFiles.Count:N0} " +
                    $"file(s).{Environment.NewLine}" +
                    $"✓ Build succeeded.{Environment.NewLine}" +
                    $"✗ Launch failed: {launchFailure}{Environment.NewLine}" +
                    $"The files remain modified on disk. Undo is available." +
                    Environment.NewLine);
                UpdateUndo();
                RefreshRunSummary();
                return;
            }

            _currentDiagnostics.Succeed("launch", $"Launched {target.Name}.");
            AppendOutput($"✓ Launch succeeded: {target.Name}." + Environment.NewLine);
            ProjectStateSnapshot runningState = _projectState.RefreshCurrent(
                identity,
                "successful run");
            RecordStateEvent(
                $"Current State confirmed after run: {runningState.Id}.");
            _currentDiagnostics.Skip("rollback", "No rollback was needed.");
            workflowSucceeded = true;
            _projectMemory.RecordSuccess(
                _currentDiagnostics.Run.ModifiedFiles);
            _sessionLog.Add(
                $"Completed successfully; {_currentDiagnostics.Run.ModifiedFiles.Count:N0} " +
                "file(s) changed.");
            RefreshSessionViews();
            SetStage(
                "Completed successfully",
                $"Generated, validated, applied, built, and launched {target.Name}.");
            AppendOutput($"{Environment.NewLine}✓ Launched {target.Name}.{Environment.NewLine}");
        }
        catch (Exception exception)
        {
            MarkUnexpectedDiagnosticFailure(exception);
            FailWithDialog(
                "Unexpected workflow failure",
                exception.Message + Environment.NewLine +
                "No further workflow stages were run.");
        }
        finally
        {
            if (RunOutcomePolicy.ShouldRollback(
                    mutationStarted,
                    workflowSucceeded,
                    preserveBuiltChanges) &&
                _lastBackup is not null && _lastBackup.HasFiles)
            {
                RollBack(
                    "Workflow failed — changes reverted",
                    "The run did not complete, so the pre-run snapshot was restored.");
                if (TryResolveSelectedProject(
                        out SolutionInfo? rollbackSolution,
                        out ProjectInfo? rollbackTarget) &&
                    rollbackSolution is not null && rollbackTarget is not null)
                {
                    ProjectStateSnapshot restored = _projectState.RefreshCurrent(
                        CreateIdentity(rollbackSolution, rollbackTarget),
                        "automatic rollback");
                    RecordStateEvent(
                        $"Current State refreshed after rollback: {restored.Id}.");
                }
            }
            FinishDiagnostics();
            CaptureRetryContextIfEligible(retrying, retryContext);
            _timer.Stop();
            _stopwatch.Stop();
            _isRetryRunning = false;
            Busy.Visibility = Visibility.Collapsed;
            SetResetControlsEnabled(true);
            RefreshStatus();
        }
    }

    private void CopyAiRequest_Click(object sender, RoutedEventArgs e)
    {
        string requestedChange;
        try
        {
            requestedChange = RequestedChange.Require(DirectRequestBox.Text);
        }
        catch (ArgumentException exception)
        {
            StatusText.Text = exception.Message;
            MessageBox.Show(
                exception.Message,
                "Requested Change Required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            DirectRequestBox.Focus();
            return;
        }

        // Rebuild the context at invocation time. The enabled state is only a
        // convenience; this guard prevents keyboard invocation or stale UI
        // state from ever copying an incomplete request.
        if (!TryLoadAiRequestContext(out AiRequestContext context, out string error))
        {
            InvalidateAiRequestContext();
            ShowAiRequestContextRequired(error);
            return;
        }

        _aiRequestContext = context;
        _manualPackageContext = context.Snapshot;
        if (_manualUpload is null ||
            !string.Equals(
                _manualUpload.SnapshotId,
                context.Snapshot.Id,
                StringComparison.Ordinal))
        {
            string sessionId = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            IReadOnlyList<string> parts = ExternalAiMultipartRequest.Build(
                sessionId,
                context.Snapshot.Id,
                context.Solution.Path,
                context.ProjectFilePath,
                context.Target.Name,
                context.Target.Kind,
                context.Target.Directory,
                context.ProjectContext.Text,
                requestedChange);
            _manualUpload = new ManualUploadSession(
                sessionId,
                context.Snapshot.Id,
                parts,
                0);
        }

        int partIndex = _manualUpload.NextPartIndex;
        try
        {
            Clipboard.SetText(_manualUpload.Parts[partIndex]);
            bool final = partIndex == _manualUpload.Parts.Count - 1;
            _manualUpload = _manualUpload with
            {
                NextPartIndex = final ? partIndex : partIndex + 1
            };
            CopyAiRequestButton.Content = final
                ? "Copy Final Part Again"
                : $"Copy Part {partIndex + 2} of {_manualUpload.Parts.Count}";
            StatusText.Text = final
                ? $"Final part {_manualUpload.Parts.Count} of " +
                  $"{_manualUpload.Parts.Count} copied. Paste it into the same AI " +
                  "chat. The requested change is included; paste the returned JSON below."
                : $"Part {partIndex + 1} of {_manualUpload.Parts.Count} copied. " +
                  "Paste it into the same AI chat and wait for the exact " +
                  "PARSETIGER_CONTEXT_ACK response before copying the next part.";
            _sessionLog.Add(
                $"Copied AI context part {partIndex + 1} of {_manualUpload.Parts.Count}.");
            RefreshSessionViews();
        }
        catch (Exception exception)
        {
            StatusText.Text = "Copy failed: " + exception.Message;
        }
    }

    private GeneratorOption SelectedProvider =>
        ProviderSelector.SelectedItem as GeneratorOption ?? _providerOptions[0];

    private void ProviderSelector_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateProviderUi();
        UpdateRetryAvailability();
    }

    private void UpdateProviderUi()
    {
        if (ProviderSelector is null ||
            DirectGenerationPanel is null ||
            ManualCopyPanel is null)
        {
            return;
        }

        GeneratorOption selected = SelectedProvider;
        bool direct = selected.Provider != GeneratorProvider.PasteJson;
        DirectGenerationPanel.Visibility = Visibility.Visible;
        ManualCopyPanel.Visibility = direct ? Visibility.Collapsed : Visibility.Visible;
        ReturnedPackagePlaceholder.Text = direct
            ? "The generated ParseTiger package will appear here"
            : "Paste the ParseTiger JSON package returned by your AI";

        if (!direct)
        {
            ProviderModeDescription.Text =
                "Enter the requested change, copy the focused AI request, then " +
                "paste the returned package below. No API provider is called.";
            ProviderConfigurationStatus.Text =
                "Manual mode — no API key required.";
            ProviderConfigurationStatus.Foreground =
                System.Windows.Media.Brushes.DarkGreen;
            RunButton.Content = "Validate Package & Run";
            RunButton.ToolTip =
                "Validate the pasted package, apply it, build the solution, and launch the application";
            UpdateActionAvailability();
            UpdateReadyStatus();
            return;
        }

        string providerName = GetProviderName(selected);
        ProviderModeDescription.Text =
            $"Direct generation will use {providerName}, then automatically " +
            "validate, apply, build, and run.";
        RunButton.Content = $"Generate with {providerName} & Run";
        RunButton.ToolTip =
            $"Send the requested change to {providerName}, then validate, apply, build, and launch";
        RefreshProviderConfigurationStatus();
        UpdateActionAvailability();
        UpdateReadyStatus();
    }

    private void RefreshProviderConfigurationStatus()
    {
        GeneratorOption selected = SelectedProvider;
        if (selected.Provider == GeneratorProvider.PasteJson)
        {
            return;
        }

        try
        {
            ProviderCredentialSource source =
                _providerSettings.GetApiKeySource(selected.Provider);
            bool configured = source != ProviderCredentialSource.None;
            ProviderConfigurationStatus.Text = configured
                ? $"{selected.Display} — Credential saved " +
                  $"({DescribeCredentialSource(source)}); verification pending"
                : $"{selected.Display} — Not Configured";
            ProviderConfigurationStatus.Foreground = configured
                ? System.Windows.Media.Brushes.DarkGreen
                : System.Windows.Media.Brushes.DarkOrange;
            bool verified =
                configured && _providerSettings.IsApiKeyVerified(selected.Provider);
            ProviderConfigurationStatus.Text = !configured
                ? $"{GetProviderName(selected)} — Not configured"
                : verified
                    ? $"{GetProviderName(selected)} — Verified"
                    : $"{GetProviderName(selected)} — Credential saved " +
                      $"({DescribeCredentialSource(source)}); verification pending";
            ProviderConfigurationStatus.Foreground = !configured
                ? System.Windows.Media.Brushes.DarkOrange
                : verified
                    ? System.Windows.Media.Brushes.DarkGreen
                    : System.Windows.Media.Brushes.DarkGoldenrod;
        }
        catch (Exception exception)
        {
            ProviderConfigurationStatus.Text =
                "Could not read provider configuration: " + exception.Message;
            ProviderConfigurationStatus.Foreground = System.Windows.Media.Brushes.DarkRed;
        }
    }

    private void OpenProviderSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProviderSettingsWindow(_providerSettings) { Owner = this };
        dialog.ShowDialog();
        UpdateProviderUi();
        if (dialog.SettingsChanged)
        {
            StatusText.Text = "AI provider settings updated.";
        }
    }

    private void SolutionPath_TextChanged(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        ClearRepairHistory("selected solution changed");
        InvalidateAiRequestContext();

        // Browse is the normal path, but also support a complete path pasted or
        // typed directly into the box.
        string path = SolutionPath.Text?.Trim() ?? string.Empty;
        if (File.Exists(path))
        {
            RefreshAiRequestContext(showFailure: false);
        }
        else
        {
            _projectFacts = null;
            RefreshPlanningPanels();
        }
    }

    private void Window_Activated(object? sender, EventArgs e)
    {
        if (ProviderSelector is not null)
        {
            UpdateProviderUi();
        }

        // Re-check whenever ParseTiger regains focus so deleting/moving the
        // solution or project while another window is active disables Copy.
        if (!string.IsNullOrWhiteSpace(SolutionPath.Text))
        {
            RefreshAiRequestContext(showFailure: false);
        }
    }

    private void ReturnedPackageBox_TextChanged(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ReturnedPackagePlaceholder is not null)
        {
            ReturnedPackagePlaceholder.Visibility = string.IsNullOrEmpty(ReturnedPackageBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        UpdateActionAvailability();
        RefreshPackageValidationPreview();
    }

    private void DirectRequestBox_TextChanged(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_retryRequested)
        {
            ClearRepairHistory("requested change changed");
        }
        _manualUpload = null;
        if (CopyAiRequestButton is not null)
        {
            CopyAiRequestButton.Content = "Copy AI Request";
        }
        UpdateActionAvailability();
        UpdateRetryAvailability();
        _projectMemory.SetRequest(DirectRequestBox.Text);
        RefreshPlanningPanels();
    }

    private void ReturnedPackageBox_Pasting(
        object sender,
        DataObjectPastingEventArgs e)
    {
        // Pasting is raised before WPF updates Text. Format on the dispatcher
        // after the complete clipboard payload has reached the editor.
        Dispatcher.BeginInvoke(
            FormatReturnedPackageIfValid,
            DispatcherPriority.Background);
    }

    private void ReturnedPackageBox_LostKeyboardFocus(
        object sender,
        System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        FormatReturnedPackageIfValid();
    }

    private void FormatReturnedPackageIfValid()
    {
        if (_isFormattingReturnedPackage)
        {
            return;
        }

        string original = ReturnedPackageBox.Text ?? string.Empty;
        if (!PackageJsonFormatter.TryFormat(original, out string formatted) ||
            string.Equals(original, formatted, StringComparison.Ordinal))
        {
            return;
        }

        _isFormattingReturnedPackage = true;
        try
        {
            ReturnedPackageBox.Text = formatted;
            ReturnedPackageBox.CaretIndex = formatted.Length;
            ReturnedPackageBox.ScrollToHome();
            StatusText.Text = "Package JSON formatted and ready.";
        }
        finally
        {
            _isFormattingReturnedPackage = false;
        }
    }

    private void ClearReturnedPackage_Click(object sender, RoutedEventArgs e)
    {
        ReturnedPackageBox.Clear();
        ClearRepairHistory("run/package text was explicitly cleared");
        ReturnedPackageBox.Focus();
        StatusText.Text = "Returned package text cleared.";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select the target solution",
            Filter = "Visual Studio Solutions (*.sln;*.slnx)|*.sln;*.slnx|" +
                "All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            SolutionPath.Text = dialog.FileName;
            RefreshAiRequestContext(showFailure: true);
        }
    }

    private void RefreshAiRequestContext(bool showFailure)
    {
        string? previousStateId = _projectState.CurrentState?.Id;
        if (TryLoadAiRequestContext(
                out AiRequestContext context,
                out string error))
        {
            _aiRequestContext = context;
            _projectFacts = ProjectHealthInspector.Inspect(
                context.Solution,
                context.Target,
                context.ProjectFilePath);
            _projectMemory.Select(_projectFacts);
            _projectMemory.SetRequest(DirectRequestBox.Text);
            IReadOnlyList<string> planningFiles = ProjectContextSelector.Select(
                context.Snapshot,
                SourceExtensions,
                DirectRequestBox.Text);
            _projectMemory.SetRecentFiles(planningFiles);
            if ((_lastBackup is null || !_lastBackup.HasFiles) &&
                _checkpointService.Exists(context.Target.Directory))
            {
                _lastBackup = _checkpointService.Load(context.Target.Directory);
                _sessionLog.Add(
                    "Loaded the durable pre-apply recovery checkpoint for Undo.");
            }
            if (previousStateId != context.Snapshot.Id)
            {
                AppendOutput(
                    $"Current State loaded/refreshed: {context.Snapshot.Id} " +
                    $"({context.Snapshot.CapturedAt:yyyy-MM-dd HH:mm:ss})." +
                    Environment.NewLine);
                _sessionLog.Add(
                    $"Loaded current project snapshot {context.Snapshot.Id} " +
                    $"with {context.Snapshot.Files.Count:N0} authored files.");
            }
            CopyAiRequestButton.IsEnabled =
                !string.IsNullOrWhiteSpace(DirectRequestBox.Text);
            UpdateStateDisplay();
            UpdateActionAvailability();
            UpdateReadyStatus();
            RefreshPlanningPanels();
            BeginProjectHealthBuild(context);
            return;
        }

        InvalidateAiRequestContext();
        UpdateStateDisplay();
        RefreshPlanningPanels();
        if (showFailure)
        {
            ShowAiRequestContextRequired(error);
        }
    }

    private bool TryLoadAiRequestContext(
        out AiRequestContext context,
        out string error)
    {
        context = null!;
        error = string.Empty;
        string solutionPath = SolutionPath.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            error = "No solution is selected.";
            return false;
        }

        try
        {
            SolutionInfo solution = new SolutionReader().Read(solutionPath);
            ProjectInfo? target = ResolveTarget(solution);
            if (target is null)
            {
                error = "No target project was found in the solution.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(target.Directory) ||
                !Directory.Exists(target.Directory))
            {
                error = "The selected project folder is unavailable.";
                return false;
            }

            string solutionDirectory =
                Path.GetDirectoryName(solution.Path) ?? target.Directory;
            string projectFilePath = Path.GetFullPath(Path.Combine(
                solutionDirectory,
                target.RelativePath
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(projectFilePath))
            {
                error = "The selected project file is unavailable.";
                return false;
            }

            ProjectIdentity identity = CreateIdentity(solution, target);
            CurrentStateCheck stateCheck =
                _projectState.OpenOrSelect(identity);
            ProjectContextSnapshot projectContext = BuildProjectContext(
                stateCheck.Snapshot,
                DirectRequestBox.Text);
            if (projectContext.FileCount == 0 ||
                projectContext.ContentFileCount == 0)
            {
                error = "Current project-file inventory/content is unavailable.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(projectContext.Text))
            {
                error = "The project-aware AI request could not be created.";
                return false;
            }

            context = new AiRequestContext(
                solution,
                target,
                projectFilePath,
                projectContext,
                stateCheck.Snapshot);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private void InvalidateAiRequestContext()
    {
        _aiRequestContext = null;
        _manualPackageContext = null;
        _manualUpload = null;
        if (CopyAiRequestButton is not null)
        {
            CopyAiRequestButton.IsEnabled = false;
            CopyAiRequestButton.Content = "Copy AI Request";
        }

        UpdateActionAvailability();
    }

    private void ShowAiRequestContextRequired(string detail)
    {
        const string message =
            "Select a solution first. ParseTiger needs the selected solution " +
            "and project files before it can create an AI request.";
        StatusText.Text = message;
        MessageBox.Show(
            string.IsNullOrWhiteSpace(detail)
                ? message
                : message + Environment.NewLine + Environment.NewLine + detail,
            "Solution Required",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_lastBackup is null || !_lastBackup.HasFiles)
        {
            return;
        }

        try
        {
            BackupService.Restore(_lastBackup);
            if (TryResolveSelectedProject(out _, out ProjectInfo? undoTarget) &&
                undoTarget is not null)
            {
                _checkpointService.Delete(undoTarget.Directory);
            }
            RefreshCurrentStateAfterLocalChange("Undo");
            AppendOutput($"{Environment.NewLine}↩ Reverted the last change.{Environment.NewLine}");
            StatusText.Text = "Reverted the last change. Run again to rebuild.";
            _lastBackup = null;
            ClearRepairHistory("Undo invalidated the failed-run context");
            _sessionLog.Add("Undo restored the complete pre-change checkpoint.");
            RefreshPlanningPanels();
        }
        catch (Exception exception)
        {
            StatusText.Text =
                "Undo failed; the recovery snapshot is still available: " +
                exception.Message;
            AppendOutput(
                $"{Environment.NewLine}Undo failed. The recovery snapshot was kept " +
                $"so you can try again: {exception.Message}{Environment.NewLine}");
        }

        UpdateUndo();
    }

    private void SetBaseline_Click(object sender, RoutedEventArgs e)
    {
        if (!TryResolveSelectedProject(out _, out ProjectInfo? target))
        {
            return;
        }
        ProjectInfo selectedProject = target!;

        string replacementWarning = _baselineService.Exists(selectedProject.Directory)
            ? Environment.NewLine + Environment.NewLine +
              "This will replace the Project Baseline already saved for this project."
            : string.Empty;
        MessageBoxResult confirmation = MessageBox.Show(
            "Save the selected project's Current State as its durable Project Baseline?" +
            replacementWarning,
            "Set Current as Project Baseline",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        SetResetControlsEnabled(false);
        try
        {
            _baselineService.Save(selectedProject.Directory, selectedProject.Kind);
            _stage = "Project Baseline saved";
            UpdateStateDisplay();
            AppendOutput(
                $"{Environment.NewLine}✓ Saved an exact-byte baseline for " +
                $"{selectedProject.Name}.{Environment.NewLine}" +
                $"  {_baselineService.StorageRoot}{Environment.NewLine}");
        }
        catch (Exception exception)
        {
            Fail("Baseline failed", "Could not save the baseline: " + exception.Message);
        }
        finally
        {
            SetResetControlsEnabled(true);
            RefreshStatus();
        }
    }

    private void ResetBaseline_Click(object sender, RoutedEventArgs e)
    {
        if (!TryResolveSelectedProject(out _, out ProjectInfo? target))
        {
            return;
        }
        ProjectInfo selectedProject = target!;

        if (!_baselineService.Exists(selectedProject.Directory))
        {
            MessageBox.Show(
                "No Project Baseline has been saved for the selected project. " +
                "Use Set Current as Project Baseline first.",
                "No Project Baseline Available",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBoxResult confirmation = MessageBox.Show(
            "Restore this project to its saved Project Baseline?" +
            Environment.NewLine + Environment.NewLine +
            "Files added since the baseline will be removed. Current files are " +
            "snapshotted first so this reset can be undone.",
            "Reset to Project Baseline",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        ExecuteProjectReset(
            selectedProject,
            "Resetting to baseline...",
            () => BackupService.Restore(
                _baselineService.Load(selectedProject.Directory)),
            "Restored the saved project baseline.");
    }

    private void ResetVisualStudioDefault_Click(object sender, RoutedEventArgs e)
    {
        if (!TryResolveSelectedProject(out _, out ProjectInfo? target))
        {
            return;
        }
        ProjectInfo selectedProject = target!;

        if (!_defaultTemplateService.SupportedProjectKinds.Contains(
                selectedProject.Kind, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                $"Visual Studio Default reset currently supports WPF projects. " +
                $"The selected project is {selectedProject.Kind}. No files were changed.",
                "Project Type Not Supported",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBoxResult confirmation = MessageBox.Show(
            "Restore this project to the clean Visual Studio WPF template?" +
            Environment.NewLine + Environment.NewLine +
            "This removes user-added source files and package references. The " +
            "current project is snapshotted first so this reset can be undone.",
            "Reset to Visual Studio Default",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        ExecuteProjectReset(
            selectedProject,
            "Resetting to Visual Studio default...",
            () => _defaultTemplateService.Restore(selectedProject),
            "Restored the clean Visual Studio WPF template.");
    }

    private void ExecuteProjectReset(
        ProjectInfo target,
        string stage,
        Action resetAction,
        string successMessage)
    {
        SetResetControlsEnabled(false);
        SetStage(stage);
        ProjectBackup rollback;
        try
        {
            rollback = BackupService.CaptureProject(target.Directory);
        }
        catch (Exception exception)
        {
            Fail("Reset failed", "Could not snapshot the current project: " +
                exception.Message);
            SetResetControlsEnabled(true);
            return;
        }

        try
        {
            _checkpointService.Save(target.Directory, target.Kind);
            resetAction();
            bool projectBaselineReset = successMessage.Contains(
                "saved project baseline",
                StringComparison.OrdinalIgnoreCase);
            RefreshCurrentStateAfterLocalChange(
                projectBaselineReset
                    ? "Project Baseline reset"
                    : "Visual Studio Default reset",
                projectBaselineReset);
            _lastBackup = rollback;
            ClearRepairHistory("project reset invalidated the failed-run context");
            UpdateUndo();
            _sessionLog.Add(successMessage);
            RefreshPlanningPanels();
            _stage = "Ready";
            AppendOutput(
                $"{Environment.NewLine}✓ {successMessage}{Environment.NewLine}" +
                "  Undo can restore the files that existed immediately before " +
                $"this reset.{Environment.NewLine}");
        }
        catch (Exception exception)
        {
            try
            {
                BackupService.Restore(rollback);
                AppendOutput(
                    $"{Environment.NewLine}↩ Reset failed, so the project was " +
                    $"restored.{Environment.NewLine}");
            }
            catch (Exception restoreException)
            {
                _lastBackup = rollback;
                UpdateUndo();
                AppendOutput(
                    $"{Environment.NewLine}✖ Reset failed and rollback also failed: " +
                    $"{restoreException.Message}{Environment.NewLine}" +
                    "  The pre-reset recovery snapshot was kept; Undo remains " +
                    $"available.{Environment.NewLine}");
            }

            Fail("Reset failed", exception.Message);
        }
        finally
        {
            SetResetControlsEnabled(true);
            RefreshStatus();
        }
    }

    private bool TryResolveSelectedProject(
        out SolutionInfo? solution,
        out ProjectInfo? target)
    {
        solution = null;
        target = null;
        string solutionPath = SolutionPath.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            MessageBox.Show(
                "Select a target solution first.",
                "No Target Solution",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        try
        {
            solution = new SolutionReader().Read(solutionPath);
            target = ResolveTarget(solution);
            if (target is null)
            {
                throw new InvalidOperationException(
                    "No target project was found in the solution.");
            }

            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "The selected project could not be resolved: " + exception.Message,
                "Project Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private void SetResetControlsEnabled(bool enabled)
    {
        SetBaselineButton.IsEnabled = enabled;
        ResetBaselineButton.IsEnabled = enabled;
        ResetVisualStudioDefaultButton.IsEnabled = enabled;
        ProviderSelector.IsEnabled = enabled;
        SolutionPath.IsEnabled = enabled;
        BrowseButton.IsEnabled = enabled;
        ProviderSettingsButton.IsEnabled = enabled;
        DirectRequestBox.IsEnabled = enabled;
        ReturnedPackageBox.IsEnabled = enabled;
        ClearTextButton.IsEnabled = enabled;
        CopyAiRequestButton.IsEnabled =
            enabled &&
            _aiRequestContext is not null &&
            !string.IsNullOrWhiteSpace(DirectRequestBox.Text);
        if (!enabled)
        {
            RunButton.IsEnabled = false;
            RetryButton.IsEnabled = false;
            UndoButton.IsEnabled = false;
        }
        else
        {
            UpdateActionAvailability();
            UpdateRetryAvailability();
            UpdateUndo();
        }
    }

    private void BeginDiagnostics()
    {
        string provider = SelectedProvider.Display;
        _currentDiagnostics = new RunDiagnosticsCollector(
            SolutionPath.Text?.Trim() ?? string.Empty,
            provider);
        foreach (ProjectStateEvent stateEvent in _projectState.Events.TakeLast(12))
        {
            _currentDiagnostics.Run.StateEvents.Add(
                $"[{stateEvent.Timestamp:O}] {stateEvent.Kind} " +
                $"{stateEvent.SnapshotId}: {stateEvent.Detail}");
        }
        _runHistory.Insert(0, _currentDiagnostics.Run);
        while (_runHistory.Count > 10)
        {
            _runHistory.RemoveAt(_runHistory.Count - 1);
        }
        RunHistorySelector.SelectedItem = _currentDiagnostics.Run;
        RefreshDiagnosticsView();
    }

    private void DiagnosticFail(
        string stage,
        DiagnosticFailureKind kind,
        string detail)
    {
        _currentDiagnostics?.Fail(stage, kind, detail);
        RefreshDiagnosticsView();
        if (WorkflowTabs is not null && DiagnosticsTab is not null)
        {
            WorkflowTabs.SelectedItem = DiagnosticsTab;
        }
    }

    private void SkipProviderDiagnostics(string reason)
    {
        _currentDiagnostics?.Skip("context", reason);
        _currentDiagnostics?.Skip("credential", reason);
        _currentDiagnostics?.Skip("provider-request", reason);
        _currentDiagnostics?.Skip("provider-response", reason);
    }

    private void RecordOperationPreflight(PackagePreview preview)
    {
        if (_currentDiagnostics is null)
        {
            return;
        }

        foreach (OperationPreview operation in preview.Operations)
        {
            _currentDiagnostics.AddOperation(new OperationDiagnostic
            {
                Number = operation.Number,
                Type = operation.Type,
                RelativePath = operation.Path,
                OldTextLength = operation.OldTextLength,
                MatchCount = operation.MatchCount,
                State = operation.Applicable
                    ? DiagnosticState.Succeeded
                    : DiagnosticState.Failed,
                CheckedAt = DateTimeOffset.Now,
                FailureReason = operation.Error ?? string.Empty,
                ExpectedOldText = operation.Before,
                NearbyExcerpt = operation.NearbyExcerpt
            });
        }
        RefreshDiagnosticsView();
    }

    private static string BuildPreflightFailure(PackagePreview preview) =>
        string.Join(
            Environment.NewLine,
            preview.Operations
                .Where(operation => !operation.Applicable)
                .Select(operation =>
                    $"Operation {operation.Number} — {operation.Path}: " +
                    $"{operation.Error} Expected oldText length " +
                    $"{operation.OldTextLength:N0}; occurrence count " +
                    $"{operation.MatchCount}."));

    private void RecordApplyFailure(ExecutionResult result)
    {
        string detail =
            $"Operation {result.FailedOperationNumber?.ToString() ?? "unknown"} " +
            $"({result.FailedPath ?? "unknown file"}) failed during apply. " +
            $"Occurrence count: {result.MatchCount}. {result.Message}";
        DiagnosticFail("apply", DiagnosticFailureKind.Apply, detail);
        if (_currentDiagnostics is null || result.FailedOperationNumber is null)
        {
            return;
        }

        OperationDiagnostic? operation = _currentDiagnostics.Run.Operations
            .FirstOrDefault(item => item.Number == result.FailedOperationNumber);
        if (operation is not null)
        {
            operation.State = DiagnosticState.Failed;
            operation.MatchCount = result.MatchCount;
            operation.FailureReason = result.Message ?? "Apply failed.";
            operation.ExpectedOldText = result.ExpectedOldText;
            operation.NearbyExcerpt = result.NearbyExcerpt;
        }
        RefreshDiagnosticsView();
    }

    private static string SummarizeBuildFailure(string output)
    {
        string[] usefulLines = output
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Where(line =>
                line.Contains(" error ", StringComparison.OrdinalIgnoreCase) ||
                line.Contains(": error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .ToArray();
        return usefulLines.Length == 0
            ? "Build returned a non-zero exit code. See Output for compiler details."
            : string.Join(" | ", usefulLines);
    }

    private void MarkUnexpectedDiagnosticFailure(Exception exception)
    {
        if (_currentDiagnostics is null)
        {
            return;
        }

        DiagnosticStage? running = _currentDiagnostics.Run.Stages
            .FirstOrDefault(stage => stage.State == DiagnosticState.Running);
        if (running is not null)
        {
            _currentDiagnostics.Fail(
                running.Key,
                DiagnosticFailureKind.Unexpected,
                exception.Message);
        }
        else
        {
            _currentDiagnostics.Run.FailureKind =
                DiagnosticFailureKind.Unexpected;
            _currentDiagnostics.Run.Summary = "Unexpected workflow failure";
        }
    }

    private void FinishDiagnostics()
    {
        if (_currentDiagnostics is null)
        {
            return;
        }

        string summary = _currentDiagnostics.Run.FailureKind ==
            DiagnosticFailureKind.None
            ? "Succeeded"
            : _currentDiagnostics.Run.Summary;
        _currentDiagnostics.Complete(summary);
        RefreshDiagnosticsView();
    }

    private void CaptureRetryContextIfEligible(
        bool retrying,
        FailedRunRetryContext? previousRetry)
    {
        if (_currentDiagnostics is null)
        {
            _retryRequested = false;
            UpdateRetryAvailability();
            return;
        }

        RunDiagnostic run = _currentDiagnostics.Run;
        DiagnosticStage? failedStage = run.Stages.FirstOrDefault(stage =>
            stage.State == DiagnosticState.Failed);
        bool eligibleFailure = FailedRunRetryPolicy.CanRetry(run);

        if (eligibleFailure)
        {
            string originalRequest = retrying && previousRetry is not null
                ? previousRetry.OriginalRequest
                : DirectRequestBox.Text?.Trim() ?? string.Empty;
            string package = ReturnedPackageBox.Text ?? string.Empty;
            IReadOnlyList<string> affectedPaths = run.ModifiedFiles
                .Concat(run.Operations.Select(operation => operation.RelativePath))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string projectRoot = _projectState.CurrentState?.Identity.ProjectRoot ??
                string.Empty;

            string stageEvidence =
                failedStage!.Key + ": " +
                DiagnosticRedactor.Redact(failedStage.Detail);
            string diagnosticEvidence = DiagnosticRedactor.Redact(
                DiagnosticReportFormatter.Format(run));
            string outputEvidence =
                DiagnosticRedactor.Redact(BuildOutput.Text);
            _lastFailedRun = retrying && previousRetry is not null
                ? previousRetry.Append(
                    package,
                    stageEvidence,
                    diagnosticEvidence,
                    outputEvidence,
                    affectedPaths)
                : new FailedRunRetryContext(
                    run.SolutionPath,
                    projectRoot,
                    DiagnosticRedactor.Redact(originalRequest),
                    package,
                    stageEvidence,
                    diagnosticEvidence,
                    outputEvidence,
                    affectedPaths);
            AppendOutput(
                $"{Environment.NewLine}Retry Failed Run is ready for user-triggered " +
                $"attempt {_lastFailedRun.RetryAttemptNumber}. " +
                $"Failure stage: {_lastFailedRun.FailedStage}. The next retry will " +
                $"include all {_lastFailedRun.Attempts.Count:N0} failed attempt(s), " +
                "the original request, every provider output and exact error, what " +
                "was learned, package rules/project constraints, and freshly loaded " +
                $"current files.{Environment.NewLine}");
        }
        else if (run.FailureKind == DiagnosticFailureKind.None)
        {
            ClearRepairHistory("run succeeded");
        }
        else
        {
            // A provider/credential or other non-repairable failure does not
            // destroy actionable repair evidence. Only the explicit lifecycle
            // events handled by ClearRepairHistory may clear it.
            _lastFailedRun = previousRetry ?? _lastFailedRun;
        }

        _retryRequested = false;
        UpdateRetryAvailability();
    }

    private void ClearRepairHistory(string reason)
    {
        if (_lastFailedRun is not null)
        {
            AppendOutput(
                $"{Environment.NewLine}Repair history cleared: {reason}." +
                Environment.NewLine);
        }
        _lastFailedRun = null;
        _retryRequested = false;
        UpdateRetryAvailability();
    }

    private void RefreshDiagnosticsView()
    {
        if (DiagnosticsOutput is null)
        {
            return;
        }

        RunDiagnostic? selected =
            RunHistorySelector?.SelectedItem as RunDiagnostic ??
            _currentDiagnostics?.Run;
        DiagnosticsOutput.Text = selected is null
            ? "No run diagnostics are available for this session."
            : DiagnosticReportFormatter.Format(selected);
        RunHistorySelector?.Items.Refresh();
        RefreshRunSummary();
    }

    private void RefreshRunSummary()
    {
        if (RunSummaryText is null || ProjectChangesText is null)
        {
            return;
        }

        RunDiagnostic? run = _currentDiagnostics?.Run ?? _runHistory.FirstOrDefault();
        if (run is null)
        {
            RunSummaryText.Text = "No Generate & Run attempt has completed yet.";
            ProjectChangesText.Text = "No project files have been modified.";
            return;
        }

        RunSummaryText.Text =
            $"Package Generated: {ShortStage(run, "provider-response", "Manual package")}" +
            Environment.NewLine +
            $"Package Applied: {ShortStage(run, "apply")}" +
            Environment.NewLine +
            $"Build: {ShortStage(run, "build")}" +
            Environment.NewLine +
            $"Launch: {ShortStage(run, "launch")}";

        string files = run.ModifiedFiles.Count == 0
            ? "No affected files recorded."
            : string.Join(
                Environment.NewLine,
                run.ModifiedFiles.Select(file => "• " + file));
        ProjectChangesText.Text =
            $"Operations: {run.OperationCount:N0}{Environment.NewLine}" +
            $"Files: {run.ModifiedFiles.Count:N0}{Environment.NewLine}" +
            files + Environment.NewLine +
            (run.ProjectChangesPersisted
                ? "Saved on disk. Undo is available."
                : run.ProjectChangesDetail);
    }

    private static string ShortStage(
        RunDiagnostic run,
        string key,
        string skippedText = "Not reached")
    {
        DiagnosticStage? stage = run.Stages.FirstOrDefault(item =>
            item.Key.Equals(key, StringComparison.Ordinal));
        return stage?.State switch
        {
            DiagnosticState.Succeeded => "Succeeded",
            DiagnosticState.Failed => "Failed",
            DiagnosticState.Running => "Running",
            DiagnosticState.Skipped => skippedText,
            _ => "Pending"
        };
    }

    private void RunHistorySelector_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) =>
        RefreshDiagnosticsView();

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (RunHistorySelector.SelectedItem is not RunDiagnostic selected)
        {
            MessageBox.Show(
                "Run ParseTiger first, then select a diagnostic run to copy.",
                "No Diagnostics",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Clipboard.SetText(DiagnosticReportFormatter.Format(selected));
        SetStage("Diagnostics copied");
    }

    private void ClearDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        _runHistory.Clear();
        _currentDiagnostics = null;
        ClearRepairHistory("run diagnostics were explicitly cleared");
        DiagnosticsOutput.Text =
            "No run diagnostics are available for this session.";
        RefreshRunSummary();
    }

    private void RollBack(string stage, string detail)
    {
        _currentDiagnostics?.Start(
            "rollback",
            "Restoring files from the pre-apply byte snapshot.");
        if (_lastBackup is not null)
        {
            bool restored = false;
            try
            {
                BackupService.Restore(_lastBackup);
                restored = true;
                _currentDiagnostics?.Succeed(
                    "rollback",
                    $"Restored {_lastBackup.Files.Count:N0} files exactly.");
                if (_currentDiagnostics is not null)
                {
                    _currentDiagnostics.Run.ProjectChangesPersisted = false;
                    _currentDiagnostics.Run.ProjectChangesDetail =
                        "The project files were restored from the pre-run snapshot.";
                }
                RefreshCurrentStateAfterLocalChange("automatic rollback");
            }
            catch (Exception exception)
            {
                detail += Environment.NewLine + "(Restore also failed: " + exception.Message + ")";
                _currentDiagnostics?.Fail(
                    "rollback",
                    DiagnosticFailureKind.Rollback,
                    exception.Message);
            }

            if (restored)
            {
                string? projectRoot = _projectState.CurrentState?.Identity.ProjectRoot;
                if (!string.IsNullOrWhiteSpace(projectRoot))
                {
                    _checkpointService.Delete(projectRoot);
                }
                _lastBackup = null;
            }
            else
            {
                detail += Environment.NewLine +
                    "The recovery snapshot was kept so Undo can be tried again.";
            }
            UpdateUndo();
        }

        _stage = stage;
        RefreshStatus();
        AppendOutput($"{Environment.NewLine}↩ {detail}{Environment.NewLine}");
        RefreshDiagnosticsView();
    }

    private static string DescribeLaunchFailure(Exception exception)
    {
        const int ApplicationControlBlocked = unchecked((int)0x800711C7);
        bool blocked =
            exception.HResult == ApplicationControlBlocked ||
            exception.Message.Contains(
                "0x800711C7",
                StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains(
                "Application Control policy has blocked",
                StringComparison.OrdinalIgnoreCase);

        return blocked
            ? "Windows Application Control blocked the built application " +
              "(HRESULT 0x800711C7). The project files were modified and the " +
              "build succeeded; only launch was blocked."
            : exception.Message;
    }

    private void UpdateUndo()
    {
        bool available = _lastBackup?.HasFiles == true;
        UndoButton.IsEnabled = available;
        UndoButton.ToolTip = available
            ? $"Restore {_lastBackup!.Files.Count:N0} captured file(s) from before " +
              "the last change."
            : "No ParseTiger recovery snapshot is currently available.";
        if (UndoRecoveryStatus is not null)
        {
            UndoRecoveryStatus.Text = available
                ? $"Undo recovery: available — {_lastBackup!.Files.Count:N0} captured " +
                  "file(s) from before the last change"
                : "Undo recovery: unavailable — no saved snapshot";
        }
    }

    private void SetStage(string stage, string? detail = null)
    {
        _stage = stage;
        _stageDetail = detail ?? stage switch
        {
            "Starting..." => "Resolving the selected solution and project.",
            "Reading pasted package..." => "Reading the package from the JSON editor.",
            "Resetting to Project Baseline..." =>
                "Restoring the saved Project Baseline before generation.",
            "Loading provider credential..." =>
                "Loading the selected provider credential from secure storage.",
            "Validating..." =>
                "Parsing the returned package and checking every operation.",
            "Applying..." =>
                "Writing the validated operations as one rollback-protected change.",
            "Creating safety checkpoint..." =>
                "Capturing an exact-byte recovery point before any file is written.",
            "Building..." =>
                "Building the selected Visual Studio solution.",
            "Running..." =>
                "Launching the application produced by the successful build.",
            _ when stage.StartsWith("Creating context", StringComparison.Ordinal) =>
                "Collecting the current project files for the provider request.",
            _ when stage.StartsWith("Generating with", StringComparison.Ordinal) =>
                "Waiting for the provider to return a ParseTiger package.",
            _ => stage
        };
        _sessionLog.Add(stage);
        PipelineStatusText.Text = FormatPipelineStatus(stage);
        RefreshSessionViews();
        RefreshStatus();
        RefreshDiagnosticsView();
    }

    private void RefreshStatus()
    {
        StatusText.Text = _stopwatch.IsRunning
            ? $"{_stage}  ({_stopwatch.Elapsed.TotalSeconds:0}s)"
            : _stage;
        StatusDetailText.Text = _stageDetail;
    }

    private void AppendOutput(string text)
    {
        BuildOutput.AppendText(text);
        BuildOutput.ScrollToEnd();
        RefreshDiagnosticsView();
    }

    private void Fail(string stage, string detail)
    {
        if (_currentDiagnostics is not null &&
            _currentDiagnostics.Run.FailureKind == DiagnosticFailureKind.None)
        {
            DiagnosticStage? activeStage = _currentDiagnostics.Run.Stages
                .FirstOrDefault(item => item.State == DiagnosticState.Running)
                ?? _currentDiagnostics.Run.Stages
                    .FirstOrDefault(item => item.State == DiagnosticState.Pending);
            if (activeStage is not null)
            {
                _currentDiagnostics.Fail(
                    activeStage.Key,
                    DiagnosticFailureKind.Workflow,
                    detail);
            }
        }

        _stage = stage;
        _stageDetail = detail;
        RefreshStatus();
        string failedStage = _currentDiagnostics?.Run.Stages
            .FirstOrDefault(item => item.State == DiagnosticState.Failed)
            ?.Name ?? stage;
        AppendOutput(
            $"{Environment.NewLine}✖ STOPPED AT {failedStage}: {detail}" +
            Environment.NewLine);
        if (_currentDiagnostics is not null &&
            _currentDiagnostics.Run.FailureKind != DiagnosticFailureKind.None)
        {
            WorkflowTabs.SelectedItem = DiagnosticsTab;
        }
    }

    private void FailWithDialog(string stage, string detail)
    {
        Fail(stage, detail);
        MessageBox.Show(
            detail,
            stage,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void UpdateActionAvailability()
    {
        if (RunButton is null || ProviderSelector is null)
        {
            return;
        }

        bool hasTarget = _aiRequestContext is not null;
        GeneratorOption selected = SelectedProvider;
        bool hasInput = selected.Provider == GeneratorProvider.PasteJson
            ? !string.IsNullOrWhiteSpace(ReturnedPackageBox?.Text)
            : !string.IsNullOrWhiteSpace(DirectRequestBox?.Text);
        bool configured = selected.Provider == GeneratorProvider.PasteJson;
        if (!configured)
        {
            try
            {
                configured =
                    _providerSettings.GetApiKeySource(selected.Provider) !=
                    ProviderCredentialSource.None;
            }
            catch
            {
                configured = false;
            }
        }

        RunButton.IsEnabled =
            !_stopwatch.IsRunning && hasTarget && hasInput && configured;
        if (CopyAiRequestButton is not null)
        {
            CopyAiRequestButton.IsEnabled =
                !_stopwatch.IsRunning &&
                hasTarget &&
                !string.IsNullOrWhiteSpace(DirectRequestBox?.Text);
        }
        UpdateRetryAvailability();
    }

    private void UpdateRetryAvailability()
    {
        if (RetryButton is null ||
            RetryAvailabilityText is null ||
            ProviderSelector is null)
        {
            return;
        }

        GeneratorOption selected = SelectedProvider;
        bool configured = selected.Provider != GeneratorProvider.PasteJson;
        if (configured)
        {
            try
            {
                configured =
                    _providerSettings.GetApiKeySource(selected.Provider) !=
                    ProviderCredentialSource.None;
            }
            catch
            {
                configured = false;
            }
        }

        bool available =
            !_stopwatch.IsRunning &&
            !_isRetryRunning &&
            _lastFailedRun is not null &&
            _aiRequestContext is not null &&
            configured;
        RetryButtonPresentation presentation;
        if (_isRetryRunning)
        {
            presentation = RetryButtonPresentation.Running();
        }
        else if (available)
        {
            presentation = RetryButtonPresentation.Available(_lastFailedRun!);
        }
        else
        {
            string reason = _stopwatch.IsRunning
                ? "another operation is running."
                : _lastFailedRun is null
                    ? "no failed run context."
                    : _aiRequestContext is null
                        ? "the selected solution context is not loaded."
                        : selected.Provider == GeneratorProvider.PasteJson
                            ? "select Gemini or OpenAI to repair the failed run."
                            : !configured
                                ? $"{GetProviderName(selected)} is not configured."
                                : "the previous failure cannot be retried.";
            presentation = RetryButtonPresentation.Unavailable(reason);
        }

        RetryButton.IsEnabled = available;
        RetryButton.Content = presentation.ButtonText;
        RetryButton.Background = (System.Windows.Media.Brush)
            new System.Windows.Media.BrushConverter().ConvertFromString(
                presentation.Background)!;
        RetryButton.Foreground = (System.Windows.Media.Brush)
            new System.Windows.Media.BrushConverter().ConvertFromString(
                presentation.Foreground)!;
        RetryButton.Opacity = presentation.Opacity;
        RetryAvailabilityText.Text = presentation.StatusText;
        RetryAvailabilityText.Foreground = presentation.State switch
        {
            RetryButtonVisualState.Available =>
                System.Windows.Media.Brushes.DarkOrange,
            RetryButtonVisualState.Running =>
                System.Windows.Media.Brushes.SaddleBrown,
            _ => System.Windows.Media.Brushes.DimGray
        };
    }

    private void UpdateReadyStatus()
    {
        if (_stopwatch.IsRunning || StatusText is null || ProviderSelector is null)
        {
            return;
        }

        string? targetName = _aiRequestContext?.Target.Name;
        GeneratorOption selected = SelectedProvider;
        if (selected.Provider == GeneratorProvider.PasteJson)
        {
            StatusText.Text = string.IsNullOrWhiteSpace(targetName)
                ? "Select a solution, then paste a ParseTiger package."
                : $"Ready to validate the pasted package for {targetName}.";
            return;
        }

        bool configured;
        try
        {
            configured =
                _providerSettings.GetApiKeySource(selected.Provider) !=
                ProviderCredentialSource.None;
        }
        catch (Exception exception)
        {
            StatusText.Text = "Provider configuration error: " + exception.Message;
            return;
        }

        string providerName = GetProviderName(selected);
        StatusText.Text = !configured
            ? $"{providerName} is not configured. Open AI Provider Settings."
            : string.IsNullOrWhiteSpace(targetName)
                ? $"Select a solution to generate with {providerName}."
                : $"Ready to generate with {providerName} for {targetName}.";
    }

    private static string GetProviderName(GeneratorOption option) =>
        option.Provider switch
        {
            GeneratorProvider.Gemini => "Gemini",
            GeneratorProvider.OpenAI => "OpenAI",
            _ => "Manual"
        };

    private static string DescribeProviderFailure(ProviderFailureKind kind) =>
        kind switch
        {
            ProviderFailureKind.InvalidCredential => "credential rejected",
            ProviderFailureKind.PermissionDenied => "permission denied",
            ProviderFailureKind.ModelUnavailable => "model unavailable",
            ProviderFailureKind.QuotaExceeded => "quota or rate limit reached",
            ProviderFailureKind.HttpFailure => "HTTP request failed",
            ProviderFailureKind.ResponseParsing => "response parsing failed",
            ProviderFailureKind.Network => "network error",
            ProviderFailureKind.Timeout => "request timed out",
            _ => "request failed"
        };

    private static DiagnosticFailureKind ClassifyProviderFailure(
        ProviderFailureKind kind) =>
        kind switch
        {
            ProviderFailureKind.QuotaExceeded =>
                DiagnosticFailureKind.ProviderQuota,
            ProviderFailureKind.InvalidCredential or
            ProviderFailureKind.PermissionDenied =>
                DiagnosticFailureKind.Credential,
            _ => DiagnosticFailureKind.Provider
        };

    private static string DescribeCredentialSource(
        ProviderCredentialSource source) => source switch
    {
        ProviderCredentialSource.WindowsCredentialManager =>
            "Windows Credential Manager",
        ProviderCredentialSource.EnvironmentVariable =>
            "environment variable",
        _ => "not configured"
    };

    private static ProjectIdentity CreateIdentity(
        SolutionInfo solution,
        ProjectInfo target) => new(
        solution.Path,
        target.Name,
        target.Kind,
        target.Directory);

    private static bool SameTarget(
        ProjectIdentity left,
        ProjectIdentity right) =>
        left.SolutionPath.Equals(
            right.SolutionPath,
            StringComparison.OrdinalIgnoreCase) &&
        left.ProjectRoot.Equals(
            right.ProjectRoot,
            StringComparison.OrdinalIgnoreCase) &&
        left.ProjectName.Equals(
            right.ProjectName,
            StringComparison.OrdinalIgnoreCase);

    private void RecordStateCheck(CurrentStateCheck check)
    {
        if (check.Refreshed)
        {
            AppendOutput(
                $"Current State: {check.Detail} Snapshot {check.Snapshot.Id}." +
                Environment.NewLine);
            RecordStateEvent(
                $"{check.Detail} Snapshot {check.Snapshot.Id}.");
        }
        UpdateStateDisplay();
    }

    private void RecordStateEvent(string detail)
    {
        if (_currentDiagnostics is not null)
        {
            _currentDiagnostics.Run.StateEvents.Add(
                $"[{DateTimeOffset.Now:O}] {detail}");
        }
        RefreshDiagnosticsView();
    }

    private void RefreshCurrentStateAfterLocalChange(
        string reason,
        bool recordProjectBaselineReset = false)
    {
        ProjectIdentity? identity = _projectState.CurrentState?.Identity;
        if (identity is null)
        {
            return;
        }

        ProjectStateSnapshot refreshed =
            _projectState.RefreshCurrent(identity, reason);
        if (recordProjectBaselineReset)
        {
            _projectState.RecordProjectBaselineReset(refreshed);
        }
        RecordStateEvent(
            $"Current State refreshed after {reason}: {refreshed.Id}.");
        UpdateStateDisplay();
    }

    private void UpdateStateDisplay()
    {
        if (SessionBaselineStatus is null ||
            CurrentStateStatus is null ||
            ProjectBaselineStatus is null ||
            GitRecoveryStatus is null)
        {
            return;
        }

        ProjectStateSnapshot? session = _projectState.SessionBaseline;
        ProjectStateSnapshot? current = _projectState.CurrentState;
        UpdateUndo();
        SessionBaselineStatus.Text = session is null
            ? "Session Baseline: select a solution"
            : $"Session Baseline: {session.Id} — created " +
              $"{session.CapturedAt:yyyy-MM-dd HH:mm:ss}; immutable this session";
        CurrentStateStatus.Text = current is null
            ? "Current State: not loaded"
            : $"Current State: {current.Id} — refreshed " +
              $"{current.CapturedAt:yyyy-MM-dd HH:mm:ss}";

        if (current is null)
        {
            ProjectBaselineStatus.Text = "Project Baseline: not checked";
            GitRecoveryStatus.Text = "Git recovery: not checked";
            return;
        }

        GitRecoveryStatus.Text =
            RecoveryStateInspector.InspectGit(current.Identity.ProjectRoot).DisplayText;

        try
        {
            ProjectBaselineInfo? info =
                _baselineService.GetInfo(current.Identity.ProjectRoot);
            ProjectBaselineStatus.Text = info is null
                ? "Project Baseline: not set"
                : $"Project Baseline: saved " +
                  $"{info.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} " +
                  $"({info.FileCount} files)";
        }
        catch (Exception exception)
        {
            ProjectBaselineStatus.Text =
                "Project Baseline: unavailable — " + exception.Message;
        }
    }

    private static ProjectInfo? ResolveTarget(SolutionInfo solution)
    {
        if (solution.Projects.Count <= 1)
        {
            return solution.Projects.Count == 1 ? solution.Projects[0] : null;
        }

        return solution.Projects.FirstOrDefault(project =>
            project.Kind.Equals("WPF", StringComparison.OrdinalIgnoreCase))
            ?? solution.Projects[0];
    }

    private ProjectContextSnapshot BuildProjectContext(
        ProjectStateSnapshot snapshot,
        string? request = null,
        IEnumerable<string>? affectedPaths = null)
    {
        IReadOnlyList<string> selected = ProjectContextSelector.Select(
            snapshot,
            SourceExtensions,
            request,
            affectedPaths);
        return new ProjectContextSnapshot(
            _projectState.BuildTextContext(snapshot, SourceExtensions, selected),
            snapshot.Files.Count,
            selected.Count,
            snapshot.Id);
    }

    private void RefreshPlanningPanels()
    {
        if (IntentSummaryText is null)
        {
            return;
        }

        ProjectStateSnapshot? snapshot = _projectState.CurrentState;
        IReadOnlyList<string> selected = snapshot is null
            ? []
            : ProjectContextSelector.Select(
                snapshot,
                SourceExtensions,
                DirectRequestBox?.Text);
        IntentSummaryText.Text = IntentSummaryFormatter.Format(
            DirectRequestBox?.Text,
            _projectFacts,
            selected.Count);
        ProjectHealthText.Text = _projectFacts is null
            ? "Select a valid solution to run the health check."
            : ProjectHealthInspector.Format(_projectFacts, _lastBuildHealth);
        ContextPreviewText.Text = snapshot is null
            ? "Context is not available until a solution is selected."
            : ContextPreviewFormatter.Format(snapshot, selected);
        _projectMemory.SetRecentFiles(selected);
        RefreshSessionViews();
    }

    private void RefreshPackageValidationPreview()
    {
        if (ValidationReportText is null)
        {
            return;
        }

        string json = ReturnedPackageBox?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            ValidationReportText.Text =
                "Paste or generate a package to see deterministic validation checks.";
            return;
        }

        try
        {
            Package package = new PackageParser().Parse(json);
            Models.ValidationResult validation = new PackageValidator().Validate(package);
            PackagePreview? preview =
                validation.IsValid && _projectState.CurrentState is not null
                    ? new PackageExecutor().Preview(
                        package,
                        _projectState.CurrentState)
                    : null;
            ValidationReportText.Text =
                PackageValidationReport.Build(package, validation, preview);
        }
        catch (FormatException exception)
        {
            ValidationReportText.Text =
                PackageValidationReport.ParseFailure(exception.Message);
        }
    }

    private void BeginProjectHealthBuild(AiRequestContext context)
    {
        if (string.Equals(
                _healthBuildSolution,
                context.Solution.Path,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _healthBuildSolution = context.Solution.Path;
        int version = ++_healthBuildVersion;
        _lastBuildHealth = "Checking in background…";
        _sessionLog.Add(
            $"Project health build started for {context.Target.Name}.");
        RefreshPlanningPanels();
        _ = CompleteProjectHealthBuildAsync(
            context.Solution.Path,
            context.Target.Name,
            version);
    }

    private async Task CompleteProjectHealthBuildAsync(
        string solutionPath,
        string projectName,
        int version)
    {
        BuildResult result;
        try
        {
            result = await Task.Run(() =>
                new SolutionBuilder().Build(solutionPath));
        }
        catch (Exception exception)
        {
            if (version != _healthBuildVersion)
            {
                return;
            }

            _lastBuildHealth = "Could not run — " + exception.Message;
            _sessionLog.Add($"Project health build could not run: {exception.Message}");
            RefreshPlanningPanels();
            return;
        }

        if (version != _healthBuildVersion ||
            !string.Equals(
                SolutionPath.Text?.Trim(),
                solutionPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastBuildHealth = result.Succeeded
            ? "Succeeded on solution load"
            : "Failed on solution load — see a normal run for full streamed output";
        _sessionLog.Add(
            $"Project health build for {projectName}: " +
            (result.Succeeded ? "succeeded." : "failed."));
        RefreshPlanningPanels();
    }

    private void RefreshSessionViews()
    {
        if (ProjectMemoryText is not null)
        {
            ProjectMemoryText.Text = _projectMemory.Format();
        }

        if (SessionLogText is not null)
        {
            SessionLogText.Text = _sessionLog.Format();
            SessionLogText.ScrollToEnd();
        }
    }

    private static string FormatPipelineStatus(string stage)
    {
        string active = stage switch
        {
            var value when value.StartsWith("Starting", StringComparison.Ordinal) =>
                "Load",
            var value when value.StartsWith("Creating context", StringComparison.Ordinal) =>
                "Context",
            var value when value.StartsWith("Generating", StringComparison.Ordinal) =>
                "Generate",
            "Reading pasted package..." => "Receive",
            "Validating..." => "Validate",
            "Creating safety checkpoint..." => "Checkpoint",
            "Applying..." => "Apply",
            "Building..." => "Build",
            "Running..." => "Run",
            var value when value.StartsWith("Completed", StringComparison.Ordinal) =>
                "Done",
            _ => stage.TrimEnd('.')
        };
        return "Load → Inspect → Context → Generate/Receive → Validate → " +
               "Checkpoint → Apply → Build → Run   |   Current: " + active;
    }

    private sealed record ProjectContextSnapshot(
        string Text,
        int FileCount,
        int ContentFileCount,
        string SnapshotId);

    private sealed record AiRequestContext(
        SolutionInfo Solution,
        ProjectInfo Target,
        string ProjectFilePath,
        ProjectContextSnapshot ProjectContext,
        ProjectStateSnapshot Snapshot);

    private sealed record ManualUploadSession(
        string SessionId,
        string SnapshotId,
        IReadOnlyList<string> Parts,
        int NextPartIndex);
}
