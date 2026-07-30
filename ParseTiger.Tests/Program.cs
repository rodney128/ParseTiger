using System.Net;
using System.Text;
using System.Text.Json;
using ParseTiger.Build;
using ParseTiger.Execution;
using ParseTiger.Diagnostics;
using ParseTiger.Generation;
using ParseTiger.Models;
using ParseTiger.Packages;
using ParseTiger.Reset;
using ParseTiger.State;
using ParseTiger.Validation;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Provider UI switches without stale wording", TestProviderUiStateAsync),
    ("External AI request is split into an acknowledged ordered conversation", TestMultipartExternalRequestAsync),
    ("External AI request rejects an empty requested change", TestEmptyRequestedChangeAsync),
    ("Generated output is excluded from project snapshots", TestGeneratedOutputExclusionAsync),
    ("Project context focuses on request-relevant source", TestFocusedProjectContextAsync),
    ("Web launch uses the target content root", TestWebLaunchContextAsync),
    ("Recovery state reports Git availability without mutation", TestRecoveryStateAsync),
    ("Project health reports framework and generated exclusions", TestProjectHealthAsync),
    ("Validation report explains exact replacement checks", TestValidationReportAsync),
    ("Self-modification guard protects ParseTiger core files", TestSelfModificationGuardAsync),
    ("Project memory retains focused session facts", TestProjectMemoryAsync),
    ("Gemini constructs, sends, and parses a request", TestGeminiSuccessAsync),
    ("Gemini classifies invalid credentials", TestGeminiInvalidKeyAsync),
    ("Gemini classifies malformed responses", TestGeminiMalformedResponseAsync),
    ("Manual package validates and applies", TestManualPackageAsync),
    ("Replace accepts unique normalized line endings", TestNormalizedLineEndingReplaceAsync),
    ("Failed replace reports exact diagnostics", TestFailedReplaceDiagnosticsAsync),
    ("Successful replace reports unique match", TestSuccessfulReplaceDiagnosticsAsync),
    ("Diagnostic report redacts secrets", TestDiagnosticRedactionAsync),
    ("Opening creates immutable Session Baseline", TestSessionBaselineAsync),
    ("External edits refresh Current State", TestExternalRefreshAsync),
    ("Successful apply feeds the next Current State context", TestApplyRefreshAsync),
    ("Project Baseline remains separate and durable", TestProjectBaselineAsync),
    ("Provider reset instruction is removed before generation", TestResetPreparationAsync),
    ("Apply is bound to the exact AI context snapshot", TestSnapshotBoundApplyAsync),
    ("Rollback preserves the immutable Session Baseline", TestRollbackPreservesSessionAsync),
    ("Launch-only failure keeps built changes and Undo", TestLaunchFailureOutcomeAsync),
    ("Run summary distinguishes apply, build, and launch", TestRunSummaryAsync),
    ("Retry is offered only after a generated package fails", TestRetryEligibilityAsync),
    ("Retry prompt carries repair evidence and no secret", TestRetryPromptAsync),
    ("Malformed JSON failure is retained for user retry", TestParseRepairHistoryAsync),
    ("OldText failure appends without losing parse evidence", TestApplyRepairHistoryAsync),
    ("Compiler failure appends every prior retry lesson", TestBuildRepairHistoryAsync),
    ("Missing CountText build failure becomes retry evidence", TestMissingCountTextRetryAsync),
    ("Retry button has unmistakable unavailable, available, and running states", TestRetryButtonPresentationAsync)
};
if (args.Contains("--live-gemini", StringComparer.OrdinalIgnoreCase))
{
    tests =
    [
        .. tests,
        ("Live Gemini credential/request smoke test", TestLiveGeminiAsync)
    ];
}

int failures = 0;
foreach ((string name, Func<Task> run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.WriteLine($"FAIL  {name}");
        Console.WriteLine(exception);
    }
}

Console.WriteLine();
Console.WriteLine($"{tests.Length - failures}/{tests.Length} checks passed.");
return failures == 0 ? 0 : 1;

static Task TestProviderUiStateAsync()
{
    var gemini = new GeneratorOption("Gemini API", GeneratorProvider.Gemini);
    ProviderUiState pending = ProviderUiState.Create(
        gemini,
        ProviderCredentialSource.WindowsCredentialManager,
        verified: false,
        "TEST");
    Assert(pending.IsDirect, "Gemini must be direct mode.");
    Assert(pending.PrimaryActionLabel == "Generate with Gemini & Run",
        "Gemini action label is wrong.");
    Assert(pending.PackagePlaceholder.Contains("generated", StringComparison.OrdinalIgnoreCase),
        "Gemini placeholder must describe generated output.");
    Assert(pending.ReadyStatus == "Ready to generate with Gemini for TEST.",
        "Gemini ready status is wrong.");
    Assert(pending.ConfigurationState ==
        ProviderConfigurationState.PendingVerification,
        "Saved unverified Gemini key must be pending.");

    ProviderUiState verified = ProviderUiState.Create(
        gemini,
        ProviderCredentialSource.WindowsCredentialManager,
        verified: true);
    Assert(verified.ConfigurationText == "Gemini — Verified",
        "Verified Gemini state is wrong.");

    var manual = new GeneratorOption(
        "Paste JSON / Manual",
        GeneratorProvider.PasteJson);
    ProviderUiState manualState = ProviderUiState.Create(
        manual,
        ProviderCredentialSource.None,
        verified: false,
        "TEST");
    Assert(!manualState.IsDirect, "Manual mode must not be direct.");
    Assert(manualState.PrimaryActionLabel == "Validate Package & Run",
        "Manual action label is wrong.");
    Assert(manualState.PackagePlaceholder.Contains("Paste", StringComparison.Ordinal),
        "Manual placeholder must ask for pasted JSON.");
    Assert(!manualState.ReadyStatus.Contains("Gemini", StringComparison.OrdinalIgnoreCase) &&
           !manualState.ReadyStatus.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase),
        "Manual status contains stale provider wording.");
    return Task.CompletedTask;
}

static Task TestMultipartExternalRequestAsync()
{
    string context = string.Join(
        Environment.NewLine,
        Enumerable.Range(1, 900).Select(index =>
            $"=== File{index}.cs ==={Environment.NewLine}class File{index} {{ }}"));
    IReadOnlyList<string> parts = ExternalAiMultipartRequest.Build(
        "SESSION123",
        "SNAPSHOT456",
        @"C:\work\App.sln",
        @"C:\work\App\App.csproj",
        "App",
        "WPF",
        @"C:\work\App",
        context,
        "Add a Save button beside the existing Cancel button.",
        4_000);

    Assert(parts.Count > 1, "Large context was not split.");
    for (int index = 0; index < parts.Count; index++)
    {
        Assert(parts[index].Contains(
            $"Part: {index + 1} of {parts.Count}",
            StringComparison.Ordinal), "A multipart sequence number is missing.");
        Assert(parts[index].Contains("SESSION123", StringComparison.Ordinal) &&
               parts[index].Contains("SNAPSHOT456", StringComparison.Ordinal),
            "A multipart identity is missing.");
    }

    Assert(parts[0].Contains("PARSETIGER PACKAGE RULES:", StringComparison.Ordinal),
        "The first part omitted package rules.");
    Assert(parts[0].Contains(
        $"PARSETIGER_CONTEXT_ACK SESSION123 1/{parts.Count}",
        StringComparison.Ordinal), "The first part omitted its exact acknowledgement.");
    Assert(parts[^1].Contains("FULL CONTEXT DELIVERED", StringComparison.Ordinal) &&
           parts[^1].Contains(
               "REQUESTED CHANGE:" + Environment.NewLine +
               "Add a Save button beside the existing Cancel button.",
               StringComparison.Ordinal),
        "The final part did not authorize processing after full delivery.");
    Assert(!parts[^1].Contains("Reply only: PARSETIGER_CONTEXT_ACK",
        StringComparison.Ordinal), "The final part incorrectly requests an acknowledgement.");
    return Task.CompletedTask;
}

static Task TestEmptyRequestedChangeAsync()
{
    bool rejected = false;
    try
    {
        ExternalAiMultipartRequest.Build(
            "SESSION123",
            "SNAPSHOT456",
            @"C:\work\App.sln",
            @"C:\work\App\App.csproj",
            "App",
            "WPF",
            @"C:\work\App",
            "=== MainWindow.xaml ===\n<Grid />",
            "   ");
    }
    catch (ArgumentException exception)
    {
        rejected = exception.Message.Contains(
            "empty REQUESTED CHANGE",
            StringComparison.OrdinalIgnoreCase);
    }

    Assert(rejected, "An empty requested change was accepted.");
    return Task.CompletedTask;
}

static Task TestGeneratedOutputExclusionAsync()
{
    string root = CreateTemporaryProject("<Grid />");
    try
    {
        foreach (string folder in new[] { "bin", "obj", "publish", "publish-check", "artifacts" })
        {
            string directory = Path.Combine(root, folder);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "generated.json"), "{}");
        }
        File.WriteAllText(Path.Combine(root, "Source.cs"), "class Source { }");

        IReadOnlyList<string> files = ProjectFilePolicy.EnumerateFiles(root);
        Assert(files.Any(path => path.EndsWith("Source.cs", StringComparison.Ordinal)),
            "Authored source was excluded.");
        Assert(!files.Any(path =>
                path.EndsWith("generated.json", StringComparison.OrdinalIgnoreCase)),
            "Generated output leaked into the project snapshot.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
    return Task.CompletedTask;
}

static Task TestFocusedProjectContextAsync()
{
    string root = CreateTemporaryProject("<Grid />");
    try
    {
        string pages = Path.Combine(root, "Pages");
        string shared = Path.Combine(pages, "Shared");
        string css = Path.Combine(root, "wwwroot", "css");
        Directory.CreateDirectory(shared);
        Directory.CreateDirectory(css);
        File.WriteAllText(Path.Combine(pages, "Index.cshtml"), "<h1>Home</h1>");
        File.WriteAllText(Path.Combine(pages, "Index.cshtml.cs"), "class IndexModel { }");
        File.WriteAllText(Path.Combine(shared, "_Layout.cshtml"), "<main>@RenderBody()</main>");
        File.WriteAllText(Path.Combine(css, "site.css"), "body { color: black; }");
        for (int index = 0; index < 45; index++)
        {
            File.WriteAllText(
                Path.Combine(root, $"Unrelated{index}.cs"),
                $"class Unrelated{index} {{ }}");
        }

        ProjectStateSnapshot snapshot =
            ProjectStateService.Capture(CreateIdentity(root));
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".cshtml", ".css", ".xaml"
        };
        IReadOnlyList<string> selected = ProjectContextSelector.Select(
            snapshot,
            extensions,
            "Add an announcement to the website front page.");

        Assert(selected.Contains("Pages/Index.cshtml", StringComparer.OrdinalIgnoreCase),
            "The homepage Razor file was not selected.");
        Assert(selected.Contains("Pages/Shared/_Layout.cshtml", StringComparer.OrdinalIgnoreCase),
            "The shared web layout was not selected.");
        Assert(selected.Contains("wwwroot/css/site.css", StringComparer.OrdinalIgnoreCase),
            "The site stylesheet was not selected.");
        Assert(selected.Count < snapshot.Files.Count,
            "Focused context still included the entire project.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
    return Task.CompletedTask;
}

static Task TestWebLaunchContextAsync()
{
    string root = Path.Combine(
        Path.GetTempPath(),
        "ParseTiger.Tests.WebLaunch",
        Guid.NewGuid().ToString("N"));
    try
    {
        string output = Path.Combine(root, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(output);
        File.WriteAllText(
            Path.Combine(root, "WebApp.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
        string executable = Path.Combine(output, "WebApp.exe");
        File.WriteAllBytes(executable, []);

        System.Diagnostics.ProcessStartInfo info =
            AppLauncher.CreateStartInfo(root, "WebApp", executable);
        Assert(info.WorkingDirectory == Path.GetFullPath(root),
            "Web application did not use the target project as its working directory.");
        Assert(info.Environment["ASPNETCORE_CONTENTROOT"] == Path.GetFullPath(root),
            "ASP.NET Core content root was not set to the target project.");
        Assert(!info.UseShellExecute,
            "Web launch cannot carry its content-root environment when shell execution is enabled.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
    return Task.CompletedTask;
}

static Task TestRecoveryStateAsync()
{
    string root = Path.Combine(
        Path.GetTempPath(),
        "ParseTiger.Tests.Recovery",
        Guid.NewGuid().ToString("N"));
    try
    {
        string project = Path.Combine(root, "src", "App");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        GitRecoveryState available = RecoveryStateInspector.InspectGit(project);
        Assert(available.Available && available.RepositoryRoot == Path.GetFullPath(root),
            "Git recovery was not detected from a nested project.");

        Directory.Delete(Path.Combine(root, ".git"));
        GitRecoveryState unavailable = RecoveryStateInspector.InspectGit(project);
        Assert(!unavailable.Available &&
               unavailable.DisplayText.Contains("unavailable", StringComparison.OrdinalIgnoreCase),
            "Missing Git recovery was not reported clearly.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
    return Task.CompletedTask;
}

static async Task TestGeminiSuccessAsync()
{
    var settings = new FakeSettings();
    var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(
            """
            {
              "candidates": [
                {
                  "content": {
                    "parts": [
                      {
                        "text": "{\"version\":\"1.0\",\"operations\":[]}"
                      }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ]
            }
            """,
            Encoding.UTF8,
            "application/json")
    });
    using var http = new HttpClient(handler);
    var progressLines = new List<string>();
    var generator = new GeminiPackageGenerator(
        "gemini-test-model",
        settings,
        http);

    string result = await generator.GenerateAsync(
        "Add a button",
        "=== MainWindow.xaml ===\n<Grid />",
        new InlineProgress(progressLines.Add),
        CancellationToken.None);

    Assert(result == "{\"version\":\"1.0\",\"operations\":[]}",
        "Gemini package extraction failed.");
    Assert(settings.Verified, "Successful Gemini response did not mark the key verified.");
    Assert(handler.Request is not null, "Gemini request was not sent.");
    Assert(handler.Request!.Method == HttpMethod.Post, "Gemini request was not POST.");
    Assert(handler.Request.RequestUri?.AbsoluteUri.Contains(
        "gemini-test-model:generateContent",
        StringComparison.Ordinal) == true,
        "Gemini request targeted the wrong model endpoint.");
    Assert(handler.HadApiKeyHeader, "Gemini request did not include the API-key header.");
    using JsonDocument payload = JsonDocument.Parse(handler.Body!);
    Assert(payload.RootElement.GetProperty("generationConfig")
        .GetProperty("responseMimeType").GetString() == "application/json",
        "Gemini request did not require JSON.");
    string userText = payload.RootElement.GetProperty("contents")[0]
        .GetProperty("parts")[0].GetProperty("text").GetString()!;
    Assert(userText.Contains("Add a button", StringComparison.Ordinal) &&
           userText.Contains("MainWindow.xaml", StringComparison.Ordinal),
        "Gemini request omitted the user request or project context.");
    Assert(progressLines.Any(line => line.Contains("Request sent", StringComparison.Ordinal)),
        "Request-sent diagnostic is missing.");
    Assert(progressLines.Any(line => line.Contains(
        "Credential verification",
        StringComparison.Ordinal)),
        "Credential-verification diagnostic is missing.");
    Assert(progressLines.Any(line => line.Contains("Package received", StringComparison.Ordinal)),
        "Response/package diagnostic is missing.");
}

static async Task TestGeminiInvalidKeyAsync()
{
    var settings = new FakeSettings();
    var handler = new RecordingHandler(_ =>
        new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"error":{"message":"API key not valid. Please pass a valid API key."}}""",
                Encoding.UTF8,
                "application/json")
        });
    using var http = new HttpClient(handler);
    var generator = new GeminiPackageGenerator("gemini-test", settings, http);

    ProviderRequestException error = await ExpectProviderFailureAsync(() =>
        generator.GenerateAsync("change", "context", null, CancellationToken.None));
    Assert(error.Kind == ProviderFailureKind.InvalidCredential,
        "Invalid Gemini key was not classified.");
    Assert(!settings.Verified, "Rejected key was incorrectly marked verified.");
}

static async Task TestGeminiMalformedResponseAsync()
{
    var settings = new FakeSettings();
    var handler = new RecordingHandler(_ =>
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json")
        });
    using var http = new HttpClient(handler);
    var generator = new GeminiPackageGenerator("gemini-test", settings, http);

    ProviderRequestException error = await ExpectProviderFailureAsync(() =>
        generator.GenerateAsync("change", "context", null, CancellationToken.None));
    Assert(error.Kind == ProviderFailureKind.ResponseParsing,
        "Malformed Gemini response was not classified.");
    Assert(settings.Verified,
        "A successful authenticated HTTP response should verify the credential.");
}

static Task TestManualPackageAsync()
{
    string root = Path.Combine(
        Path.GetTempPath(),
        "ParseTiger.Tests",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    string file = Path.Combine(root, "MainWindow.xaml");
    File.WriteAllText(file, "<Grid></Grid>");
    try
    {
        const string json =
            """
            {
              "version": "1.0",
              "operations": [
                {
                  "type": "replace",
                  "path": "MainWindow.xaml",
                  "oldText": "<Grid>",
                  "newText": "<Grid><Button />"
                }
              ]
            }
            """;
        var package = new PackageParser().Parse(json);
        var validation = new PackageValidator().Validate(package);
        Assert(validation.IsValid, string.Join("; ", validation.Errors));
        var result = new PackageExecutor().Apply(package, root);
        Assert(result.Succeeded, result.Message ?? "Manual apply failed.");
        Assert(File.ReadAllText(file) == "<Grid><Button /></Grid>",
            "Manual package produced the wrong file content.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }

    return Task.CompletedTask;
}

static Task TestNormalizedLineEndingReplaceAsync()
{
    string root = CreateTemporaryProject("<Grid>\r\n    <Button />\r\n</Grid>");
    try
    {
        Package package = new PackageParser().Parse(
            """
            {
              "version": "1.0",
              "operations": [
                {
                  "type": "replace",
                  "path": "MainWindow.xaml",
                  "oldText": "<Grid>\n    <Button />\n</Grid>",
                  "newText": "<Grid />"
                }
              ]
            }
            """);
        PackagePreview preview = new PackageExecutor().Preview(package, root);
        OperationPreview operation = preview.Operations.Single();
        Assert(preview.CanApply,
            "A unique match differing only by CRLF versus LF should pass.");
        Assert(operation.Number == 1, "Operation number was not recorded.");
        Assert(operation.OldTextLength > 0, "oldText length was not recorded.");
        Assert(operation.MatchCount == 1, "Normalized unique match should be one.");
        ExecutionResult result = new PackageExecutor().Apply(package, root);
        Assert(result.Succeeded, result.Message ?? "Normalized apply failed.");
        Assert(File.ReadAllText(Path.Combine(root, "MainWindow.xaml")) == "<Grid />",
            "Normalized replacement produced the wrong content.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
    return Task.CompletedTask;
}

static Task TestFailedReplaceDiagnosticsAsync()
{
    string root = CreateTemporaryProject("<Grid>\r\n    <Button />\r\n</Grid>");
    try
    {
        Package package = new PackageParser().Parse(
            """
            {
              "version": "1.0",
              "operations": [
                {
                  "type": "replace",
                  "path": "MainWindow.xaml",
                  "oldText": "<Grid>\n    <TextBlock />\n</Grid>",
                  "newText": "<Grid />"
                }
              ]
            }
            """);
        PackagePreview preview = new PackageExecutor().Preview(package, root);
        OperationPreview operation = preview.Operations.Single();
        Assert(!preview.CanApply, "Genuinely different oldText should fail.");
        Assert(operation.MatchCount == 0, "Missing text should have zero matches.");
        Assert(operation.NearbyExcerpt.Contains("<Grid>", StringComparison.Ordinal),
            "Closest context was not included.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
    return Task.CompletedTask;
}

static Task TestSuccessfulReplaceDiagnosticsAsync()
{
    string root = CreateTemporaryProject("<Grid><Button /></Grid>");
    try
    {
        Package package = new PackageParser().Parse(
            """
            {
              "version": "1.0",
              "operations": [
                {
                  "type": "replace",
                  "path": "MainWindow.xaml",
                  "oldText": "<Button />",
                  "newText": "<TextBlock />"
                }
              ]
            }
            """);
        PackagePreview preview = new PackageExecutor().Preview(package, root);
        OperationPreview operation = preview.Operations.Single();
        Assert(preview.CanApply, "Valid exact replacement did not pass preflight.");
        Assert(operation.MatchCount == 1, "Unique match count should be one.");
        ExecutionResult result = new PackageExecutor().Apply(package, root);
        Assert(result.Succeeded, result.Message ?? "Apply failed.");
        Assert(File.ReadAllText(Path.Combine(root, "MainWindow.xaml"))
            .Contains("<TextBlock />", StringComparison.Ordinal),
            "Successful operation did not update the file.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
    return Task.CompletedTask;
}

static Task TestDiagnosticRedactionAsync()
{
    var collector = new RunDiagnosticsCollector("C:\\safe\\TEST.slnx", "Gemini");
    collector.Start("provider-request", "Authorization: Bearer abcdefghijklmnop");
    collector.Fail(
        "provider-request",
        DiagnosticFailureKind.Provider,
        "x-goog-api-key=AIzaABCDEFGHIJKLMNOPQRSTUV");
    collector.Complete("Provider request failed");
    string report = DiagnosticReportFormatter.Format(collector.Run);
    Assert(!report.Contains("abcdefghijklmnop", StringComparison.Ordinal),
        "Bearer token leaked into diagnostics.");
    Assert(!report.Contains("AIzaABCDEFGHIJKLMNOPQRSTUV", StringComparison.Ordinal),
        "API key leaked into diagnostics.");
    Assert(report.Contains("[REDACTED]", StringComparison.Ordinal),
        "Redaction marker is missing.");
    return Task.CompletedTask;
}

static Task TestSessionBaselineAsync()
{
    string root = CreateTemporaryProject("<Grid />");
    try
    {
        var state = new ProjectStateService();
        ProjectIdentity identity = CreateIdentity(root);
        CurrentStateCheck opened = state.OpenOrSelect(identity);
        string sessionId = state.SessionBaseline!.Id;

        File.WriteAllText(
            Path.Combine(root, "MainWindow.xaml"),
            "<Grid><Button /></Grid>");
        ProjectStateSnapshot refreshed =
            state.RefreshCurrent(identity, "successful apply");

        Assert(opened.Snapshot.Id == sessionId,
            "Opening did not use the Session Baseline as Current State.");
        Assert(refreshed.Id != sessionId,
            "Current State did not change after the project changed.");
        Assert(state.SessionBaseline.Id == sessionId,
            "Refreshing Current State overwrote the immutable Session Baseline.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
    return Task.CompletedTask;
}

static Task TestExternalRefreshAsync()
{
    string root = CreateTemporaryProject("<Grid />");
    try
    {
        var state = new ProjectStateService();
        ProjectIdentity identity = CreateIdentity(root);
        state.OpenOrSelect(identity);
        File.WriteAllText(
            Path.Combine(root, "MainWindow.xaml"),
            "<Grid><TextBlock /></Grid>");

        CurrentStateCheck check =
            state.EnsureCurrent(identity, "provider preflight");
        string context = state.BuildTextContext(
            check.Snapshot,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".xaml" });

        Assert(check.Refreshed, "External file change was not detected.");
        Assert(context.Contains("<TextBlock />", StringComparison.Ordinal),
            "Refreshed provider context did not use the current disk file.");
        Assert(state.Events.Any(item =>
                item.Detail.Contains("External", StringComparison.OrdinalIgnoreCase)),
            "External refresh was not recorded as a state event.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
    return Task.CompletedTask;
}

static Task TestApplyRefreshAsync()
{
    string root = CreateTemporaryProject("<Grid />");
    try
    {
        var state = new ProjectStateService();
        ProjectIdentity identity = CreateIdentity(root);
        ProjectStateSnapshot context = state.OpenOrSelect(identity).Snapshot;
        var package = new Package
        {
            Version = "1.0",
            Operations =
            [
                new Operation
                {
                    Type = "replace",
                    Path = "MainWindow.xaml",
                    OldText = "<Grid />",
                    NewText = "<Grid><Button /></Grid>"
                }
            ]
        };

        ExecutionResult applied = new PackageExecutor().Apply(package, context);
        Assert(applied.Succeeded, applied.Message ?? "Snapshot apply failed.");
        ProjectStateSnapshot refreshed =
            state.RefreshCurrent(identity, "successful apply/build/run");
        string nextContext = state.BuildTextContext(
            refreshed,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".xaml" });

        Assert(nextContext.Contains("<Button />", StringComparison.Ordinal),
            "The next request context did not use the successfully applied files.");
        Assert(refreshed.Id != context.Id,
            "Successful apply did not produce a new Current State ID.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
    return Task.CompletedTask;
}

static Task TestProjectBaselineAsync()
{
    string root = CreateTemporaryProject("<Grid />");
    string storage = Path.Combine(
        Path.GetTempPath(),
        "ParseTiger.Tests.Baselines",
        Guid.NewGuid().ToString("N"));
    try
    {
        var state = new ProjectStateService();
        ProjectIdentity identity = CreateIdentity(root);
        state.OpenOrSelect(identity);
        string sessionId = state.SessionBaseline!.Id;
        var baseline = new BaselineService(storage);
        baseline.Save(root, "WPF");

        File.WriteAllText(
            Path.Combine(root, "MainWindow.xaml"),
            "<Grid><Button /></Grid>");
        state.RefreshCurrent(identity, "successful run");
        BackupService.Restore(baseline.Load(root));
        ProjectStateSnapshot restored =
            state.RefreshCurrent(identity, "Project Baseline reset");
        state.RecordProjectBaselineReset(restored);

        Assert(File.ReadAllText(Path.Combine(root, "MainWindow.xaml")) == "<Grid />",
            "Project Baseline did not restore the durable checkpoint.");
        Assert(state.SessionBaseline.Id == sessionId,
            "Project Baseline reset overwrote the Session Baseline.");
        Assert(baseline.GetInfo(root)?.FileCount == 1,
            "Project Baseline metadata was not preserved.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
        if (Directory.Exists(storage))
        {
            Directory.Delete(storage, recursive: true);
        }
    }
    return Task.CompletedTask;
}

static Task TestResetPreparationAsync()
{
    ProviderRequestPreparation prepared =
        ProviderRequestPreparation.Parse(
            "Reset the project to the ParseTiger baseline. Then add a map.");
    Assert(prepared.ResetProjectBaselineFirst,
        "Project Baseline reset was not detected.");
    Assert(!prepared.CodeChangeRequest.Contains(
            "baseline",
            StringComparison.OrdinalIgnoreCase),
        "Reset wording leaked into the provider request.");
    Assert(prepared.CodeChangeRequest.Contains(
            "add a map",
            StringComparison.OrdinalIgnoreCase),
        "The actual code-change request was removed.");
    return Task.CompletedTask;
}

static Task TestSnapshotBoundApplyAsync()
{
    string root = CreateTemporaryProject("<Grid />");
    try
    {
        var state = new ProjectStateService();
        ProjectStateSnapshot context =
            state.OpenOrSelect(CreateIdentity(root)).Snapshot;
        var package = new Package
        {
            Version = "1.0",
            Operations =
            [
                new Operation
                {
                    Type = "replace",
                    Path = "MainWindow.xaml",
                    OldText = "<Grid />",
                    NewText = "<Grid><Button /></Grid>"
                }
            ]
        };
        File.WriteAllText(
            Path.Combine(root, "MainWindow.xaml"),
            "<Grid><TextBlock /></Grid>");

        ExecutionResult result = new PackageExecutor().Apply(package, context);
        Assert(!result.Succeeded,
            "Apply ignored a file change made after context creation.");
        Assert(File.ReadAllText(Path.Combine(root, "MainWindow.xaml"))
                .Contains("TextBlock", StringComparison.Ordinal),
            "Snapshot-bound apply overwrote the external edit.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
    return Task.CompletedTask;
}

static Task TestRollbackPreservesSessionAsync()
{
    string root = CreateTemporaryProject("<Grid />");
    try
    {
        var state = new ProjectStateService();
        ProjectIdentity identity = CreateIdentity(root);
        ProjectStateSnapshot opened = state.OpenOrSelect(identity).Snapshot;
        ProjectBackup preRun = opened.ToBackup();

        File.WriteAllText(
            Path.Combine(root, "MainWindow.xaml"),
            "<broken>");
        BackupService.Restore(preRun);
        state.RefreshCurrent(identity, "failed apply rollback");

        Assert(File.ReadAllText(Path.Combine(root, "MainWindow.xaml")) == "<Grid />",
            "Rollback did not restore the pre-run state.");
        Assert(state.SessionBaseline!.Id == opened.Id,
            "Rollback overwrote the immutable Session Baseline.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
    return Task.CompletedTask;
}

static Task TestLaunchFailureOutcomeAsync()
{
    Assert(
        !RunOutcomePolicy.ShouldRollback(
            mutationStarted: true,
            workflowSucceeded: false,
            preserveBuiltChanges: true),
        "A launch-only failure incorrectly rolls back successfully built changes.");
    Assert(
        RunOutcomePolicy.ShouldRollback(
            mutationStarted: true,
            workflowSucceeded: false,
            preserveBuiltChanges: false),
        "An apply/build workflow failure must still roll back.");
    return Task.CompletedTask;
}

static Task TestRunSummaryAsync()
{
    var collector = new RunDiagnosticsCollector("TEST.slnx", "Gemini API");
    collector.Succeed("provider-response", "Package received.");
    collector.Succeed("apply", "Applied 4 operations.");
    collector.Succeed("build", "Build completed successfully.");
    collector.Fail(
        "launch",
        DiagnosticFailureKind.Launch,
        "Windows Application Control blocked TEST.dll (HRESULT 0x800711C7).");
    collector.Skip(
        "rollback",
        "No rollback: Apply and Build succeeded; only Launch failed.");
    collector.Run.OperationCount = 4;
    collector.Run.ModifiedFiles.Add("MainWindow.xaml");
    collector.Run.ModifiedFiles.Add("MainWindow.xaml.cs");
    collector.Run.ProjectChangesPersisted = true;
    collector.Run.ProjectChangesDetail =
        "Files were successfully modified and built. Only launch failed.";

    string summary = RunSummaryFormatter.Format(collector.Run);
    Assert(summary.Contains("Package Generated: Succeeded", StringComparison.Ordinal),
        "Run summary omitted package generation.");
    Assert(summary.Contains("Package Applied:   Succeeded", StringComparison.Ordinal),
        "Run summary omitted successful apply.");
    Assert(summary.Contains("Build:             Succeeded", StringComparison.Ordinal),
        "Run summary omitted successful build.");
    Assert(summary.Contains("Launch:            Failed", StringComparison.Ordinal),
        "Run summary omitted launch failure.");
    Assert(summary.Contains("MainWindow.xaml.cs", StringComparison.Ordinal),
        "Run summary omitted a modified file.");
    Assert(summary.Contains("Undo remains available", StringComparison.Ordinal),
        "Run summary did not preserve Undo after launch failure.");
    return Task.CompletedTask;
}

static Task TestRetryEligibilityAsync()
{
    var buildFailure = new RunDiagnosticsCollector("TEST.slnx", "Gemini API");
    buildFailure.Succeed("provider-response", "Package received.");
    buildFailure.Fail(
        "build",
        DiagnosticFailureKind.Build,
        "CS0103: CountText does not exist.");
    Assert(
        FailedRunRetryPolicy.CanRetry(buildFailure.Run),
        "A build failure after package generation should enable retry.");

    var providerFailure = new RunDiagnosticsCollector("TEST.slnx", "Gemini API");
    providerFailure.Fail(
        "provider-request",
        DiagnosticFailureKind.ProviderQuota,
        "Quota exceeded.");
    Assert(
        !FailedRunRetryPolicy.CanRetry(providerFailure.Run),
        "A failure before any package was received must not enable repair retry.");
    return Task.CompletedTask;
}

static Task TestRetryButtonPresentationAsync()
{
    RetryButtonPresentation unavailable =
        RetryButtonPresentation.Unavailable("no failed run context.");
    Assert(unavailable.State == RetryButtonVisualState.Unavailable,
        "Unavailable retry state was not returned.");
    Assert(unavailable.Opacity < 0.7,
        "Unavailable retry state is not visibly faded.");
    Assert(unavailable.StatusText.Contains("unavailable", StringComparison.OrdinalIgnoreCase),
        "Unavailable retry state does not explain itself.");

    var context = new FailedRunRetryContext(
        "TEST.slnx",
        "TEST",
        "Add a task list.",
        "{\"version\":\"1.0\",\"operations\":[]}",
        "build: Build failed.",
        "Build failed.",
        "MainWindow.xaml.cs(35): error CS0103\n" +
        "MainWindow.xaml.cs(42): error CS0103\n" +
        "MainWindow.xaml.cs(49): error CS0246",
        ["MainWindow.xaml.cs"]);
    RetryButtonPresentation available =
        RetryButtonPresentation.Available(context);
    Assert(available.State == RetryButtonVisualState.Available,
        "Available retry state was not returned.");
    Assert(available.Background == "#D97706" && available.Opacity == 1.0,
        "Available retry state is not strongly highlighted in amber.");
    Assert(available.StatusText.Contains("3 errors", StringComparison.Ordinal),
        "Available retry status did not explain the previous build failure.");

    RetryButtonPresentation running = RetryButtonPresentation.Running();
    Assert(running.State == RetryButtonVisualState.Running,
        "Running retry state was not returned.");
    Assert(running.ButtonText == "Retrying Failed Run...",
        "Running retry button text is not explicit.");
    Assert(running.StatusText.Contains("in progress", StringComparison.OrdinalIgnoreCase),
        "Running retry status does not explain the active operation.");
    return Task.CompletedTask;
}

static Task TestRetryPromptAsync()
{
    var context = new FailedRunRetryContext(
        @"C:\safe\TEST.slnx",
        @"C:\safe\TEST",
        "Add a task list while preserving the counter.",
        "{\"version\":\"1.0\",\"operations\":[]}",
        "Build: CS0103 CountText does not exist",
        "Build failed in MainWindow.xaml.cs.",
        "MainWindow.xaml.cs(35): error CS0103",
        ["MainWindow.xaml", "MainWindow.xaml.cs"]);

    string prompt = FailedRunRetryPrompt.Build(context);
    Assert(prompt.Contains("ORIGINAL REQUESTED CHANGE:", StringComparison.Ordinal),
        "Retry prompt omitted the original request.");
    Assert(prompt.Contains("Provider package/output:", StringComparison.Ordinal),
        "Retry prompt omitted the previous package.");
    Assert(prompt.Contains("CS0103", StringComparison.Ordinal),
        "Retry prompt omitted compiler diagnostics.");
    Assert(prompt.Contains("Validation / apply / build / launch output:",
        StringComparison.Ordinal),
        "Retry prompt omitted the build/runtime error section.");
    Assert(prompt.Contains("MainWindow.xaml.cs", StringComparison.Ordinal),
        "Retry prompt omitted the affected file.");
    Assert(prompt.Contains("smallest exact unique oldText", StringComparison.Ordinal),
        "Retry prompt omitted the exact-anchor repair instruction.");
    Assert(prompt.Contains("Do not repeat operations", StringComparison.Ordinal),
        "Retry prompt omitted the successful-operation preservation rule.");
    Assert(prompt.Contains("PARSETIGER PACKAGE RULES", StringComparison.Ordinal),
        "Retry prompt omitted package rules and project constraints.");
    return Task.CompletedTask;
}

static Task TestParseRepairHistoryAsync()
{
    var context = new FailedRunRetryContext(
        @"C:\safe\TEST.slnx",
        @"C:\safe\TEST",
        "Add saved locations while preserving the map.",
        "{\"version\":\"1.0\",\"operations\":[",
        "parse: Invalid JSON at byte 36.",
        "Parse stage rejected malformed JSON.",
        "Unexpected end of JSON input.",
        ["MainWindow.xaml.cs"]);

    string prompt = FailedRunRetryPrompt.Build(context);
    Assert(context.Attempts.Count == 1,
        "Initial parse failure was not retained as attempt 1.");
    Assert(prompt.Contains("FAILED ATTEMPT 1", StringComparison.Ordinal),
        "Parse retry prompt omitted attempt numbering.");
    Assert(prompt.Contains("Failure stage classification: parse", StringComparison.Ordinal),
        "Parse retry prompt omitted its stage classification.");
    Assert(prompt.Contains("Unexpected end of JSON input", StringComparison.Ordinal),
        "Parse retry prompt omitted the exact parse error.");
    Assert(prompt.Contains(context.OriginalRequest, StringComparison.Ordinal),
        "Parse retry prompt omitted the original request.");
    Assert(prompt.Contains("CURRENT PROJECT FILES appended", StringComparison.Ordinal),
        "Parse retry prompt did not require refreshed current file contents.");
    return Task.CompletedTask;
}

static Task TestApplyRepairHistoryAsync()
{
    var context = new FailedRunRetryContext(
        @"C:\safe\TEST.slnx",
        @"C:\safe\TEST",
        "Add saved locations while preserving the map.",
        "{\"version\":\"1.0\",\"operations\":[",
        "parse: Invalid JSON at byte 36.",
        "Parse stage rejected malformed JSON.",
        "Unexpected end of JSON input.",
        ["MainWindow.xaml.cs"]);

    context = context.Append(
        """
        {"version":"1.0","operations":[{"type":"replace","path":"MainWindow.xaml.cs","oldText":"missing","newText":"fixed"}]}
        """,
        "preflight: oldText was not found; occurrence count 0.",
        "Operation 1 failed exact-anchor preflight.",
        "MainWindow.xaml.cs: expected oldText length 7; occurrence count 0.",
        ["MainWindow.xaml.cs"]);

    string prompt = FailedRunRetryPrompt.Build(context);
    Assert(context.Attempts.Count == 2,
        "OldText failure replaced rather than appended to parse history.");
    Assert(prompt.Contains("FAILED ATTEMPT 1", StringComparison.Ordinal) &&
           prompt.Contains("FAILED ATTEMPT 2", StringComparison.Ordinal),
        "Second retry prompt did not contain both attempts.");
    Assert(prompt.Contains("Unexpected end of JSON input", StringComparison.Ordinal),
        "Second retry prompt lost the first parse error.");
    Assert(prompt.Contains("\"oldText\":\"missing\"", StringComparison.Ordinal),
        "Second retry prompt omitted the failed provider package.");
    Assert(prompt.Contains(
        "Failure stage classification: package validation",
        StringComparison.Ordinal),
        "Preflight failure was not classified as package validation.");
    Assert(prompt.Contains("occurrence count 0", StringComparison.Ordinal),
        "Second retry prompt omitted the exact oldText error.");
    Assert(prompt.Contains(context.OriginalRequest, StringComparison.Ordinal),
        "Second retry prompt lost the original request.");
    return Task.CompletedTask;
}

static Task TestBuildRepairHistoryAsync()
{
    var context = new FailedRunRetryContext(
        @"C:\safe\TEST.slnx",
        @"C:\safe\TEST",
        "Add saved locations while preserving the map.",
        "{\"version\":\"1.0\",\"operations\":[",
        "parse: Invalid JSON at byte 36.",
        "Parse diagnostics.",
        "Unexpected end of JSON input.",
        ["MainWindow.xaml.cs"]);
    context = context.Append(
        "{\"version\":\"1.0\",\"operations\":[]}",
        "validate: Package contained no effective operations.",
        "Validation diagnostics.",
        "No real file changes remained.",
        ["MainWindow.xaml"]);
    context = context.Append(
        "{\"version\":\"1.0\",\"operations\":[{\"type\":\"replace\"}]}",
        "build: Compilation failed.",
        "Build diagnostics include CS0103.",
        "MainWindow.xaml.cs(35,13): error CS0103: CountText does not exist.",
        ["MainWindow.xaml.cs"]);

    string prompt = FailedRunRetryPrompt.Build(context);
    Assert(context.Attempts.Count == 3,
        "Build failure did not append as the third failed attempt.");
    for (int attempt = 1; attempt <= 3; attempt++)
    {
        Assert(prompt.Contains($"FAILED ATTEMPT {attempt}", StringComparison.Ordinal),
            $"Build retry prompt omitted failed attempt {attempt}.");
    }
    Assert(prompt.Contains("Unexpected end of JSON input", StringComparison.Ordinal),
        "Build retry prompt lost parse evidence.");
    Assert(prompt.Contains("No real file changes remained", StringComparison.Ordinal),
        "Build retry prompt lost validation evidence.");
    Assert(prompt.Contains("CS0103", StringComparison.Ordinal) &&
           prompt.Contains("CountText", StringComparison.Ordinal),
        "Build retry prompt omitted compiler evidence.");
    Assert(prompt.Contains("What was learned:", StringComparison.Ordinal),
        "Build retry prompt omitted per-attempt lessons.");
    Assert(prompt.Contains(context.OriginalRequest, StringComparison.Ordinal),
        "Build retry prompt lost the original request.");
    Assert(prompt.Contains("do not repeat any prior failed", StringComparison.OrdinalIgnoreCase),
        "Build retry prompt did not prohibit repeating failed approaches.");

    var collector = new RunDiagnosticsCollector(@"C:\safe\TEST.slnx", "Gemini API");
    collector.Run.RepairAttemptNumber = context.RetryAttemptNumber;
    collector.Run.RepairHistoryCount = context.Attempts.Count;
    collector.Run.CumulativeRepairHistoryIncluded = true;
    collector.Run.LatestRepairFailureStage = context.FailedStage;
    string diagnostics = DiagnosticReportFormatter.Format(collector.Run);
    Assert(diagnostics.Contains("Retry attempt number: 3", StringComparison.Ordinal),
        "Run diagnostics omitted the retry attempt number.");
    Assert(diagnostics.Contains(
        "Cumulative repair history included: Yes",
        StringComparison.Ordinal),
        "Run diagnostics did not confirm cumulative history inclusion.");
    Assert(diagnostics.Contains(
        "Latest repair failure stage: build",
        StringComparison.Ordinal),
        "Run diagnostics omitted the latest failure classification.");
    return Task.CompletedTask;
}

static async Task TestMissingCountTextRetryAsync()
{
    string root = Path.Combine(
        Path.GetTempPath(),
        "ParseTiger.Tests",
        "MissingCountText-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    string projectPath = Path.Combine(root, "TEST.csproj");
    try
    {
        await File.WriteAllTextAsync(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0-windows</TargetFramework>
                <UseWPF>true</UseWPF>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "App.xaml"),
            """
            <Application x:Class="TEST.App"
                         xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         StartupUri="MainWindow.xaml" />
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "App.xaml.cs"),
            """
            using System.Windows;
            namespace TEST;
            public partial class App : Application { }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "MainWindow.xaml"),
            """
            <Window x:Class="TEST.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Grid />
            </Window>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "MainWindow.xaml.cs"),
            """
            using System.Windows;
            namespace TEST;
            public partial class MainWindow : Window
            {
                public MainWindow()
                {
                    InitializeComponent();
                    CountText.Text = "1";
                }
            }
            """);

        BuildResult build = await Task.Run(() =>
            new SolutionBuilder().Build(projectPath));
        Assert(!build.Succeeded, "The missing CountText fixture unexpectedly built.");
        Assert(build.Output.Contains("CountText", StringComparison.Ordinal),
            "Compiler output did not identify CountText.");
        Assert(build.Output.Contains("CS0103", StringComparison.Ordinal),
            "Compiler output did not include CS0103.");

        var collector = new RunDiagnosticsCollector(projectPath, "Gemini API");
        collector.Succeed("provider-response", "Package received.");
        collector.Fail("build", DiagnosticFailureKind.Build, build.Output);
        Assert(FailedRunRetryPolicy.CanRetry(collector.Run),
            "The missing CountText build failure was not retryable.");

        string prompt = FailedRunRetryPrompt.Build(new FailedRunRetryContext(
            projectPath,
            root,
            "Add a task list while preserving the counter.",
            "{\"version\":\"1.0\",\"operations\":[]}",
            "Build",
            DiagnosticReportFormatter.Format(collector.Run),
            build.Output,
            ["MainWindow.xaml", "MainWindow.xaml.cs"]));
        Assert(prompt.Contains("CountText", StringComparison.Ordinal),
            "Retry prompt omitted the missing CountText compiler evidence.");
        Assert(prompt.Contains("preserving the counter", StringComparison.Ordinal),
            "Retry prompt omitted the original requested behavior.");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static Task TestProjectHealthAsync()
{
    string root = CreateTemporaryProject("<Grid />");
    try
    {
        string projectFile = Path.Combine(root, "TEST.csproj");
        File.WriteAllText(
            projectFile,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<TargetFramework>net10.0-windows</TargetFramework>" +
            "</PropertyGroup></Project>");
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        var solution = new SolutionInfo
        {
            Name = "TEST",
            Path = Path.Combine(root, "TEST.slnx")
        };
        var project = new ProjectInfo
        {
            Name = "TEST",
            Directory = root,
            RelativePath = "TEST.csproj",
            Kind = "WPF"
        };

        AiProjectFacts facts =
            ProjectHealthInspector.Inspect(solution, project, projectFile);
        string report = ProjectHealthInspector.Format(facts);
        Assert(facts.TargetFramework == "net10.0-windows",
            "Health check missed the target framework.");
        Assert(facts.GeneratedFolders.Contains("bin"),
            "Health check did not identify generated output.");
        Assert(report.Contains("Generated folders excluded", StringComparison.Ordinal),
            "Health report did not explain generated-folder handling.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
    return Task.CompletedTask;
}

static Task TestValidationReportAsync()
{
    string root = CreateTemporaryProject("<Grid />");
    try
    {
        var package = new Package
        {
            Version = "1.0",
            Operations =
            [
                new Operation
                {
                    Type = "replace",
                    Path = "MainWindow.xaml",
                    OldText = "<Grid />",
                    NewText = "<Grid><Button /></Grid>"
                }
            ]
        };
        ValidationResult validation = new PackageValidator().Validate(package);
        PackagePreview preview = new PackageExecutor().Preview(package, root);
        string report = PackageValidationReport.Build(package, validation, preview);
        Assert(report.Contains("JSON parsed", StringComparison.Ordinal) &&
               report.Contains("one exact match", StringComparison.Ordinal),
            "Detailed validation report omitted a successful exact-match check.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
    return Task.CompletedTask;
}

static Task TestSelfModificationGuardAsync()
{
    var project = new ProjectInfo
    {
        Name = "ParseTiger",
        Directory = Path.Combine("C:", "work", "ParseTiger")
    };
    var package = new Package
    {
        Version = "1.0",
        Operations =
        [
            new Operation
            {
                Type = "replace",
                Path = "Validation/PackageValidator.cs",
                OldText = "old",
                NewText = "new"
            }
        ]
    };
    SelfModificationAssessment result =
        SelfModificationGuard.Assess(project, package);
    Assert(result.RequiresConfirmation && result.TouchesCoreSafetyCode,
        "ParseTiger core self-modification was not protected.");
    return Task.CompletedTask;
}

static Task TestProjectMemoryAsync()
{
    var memory = new ProjectSessionMemory();
    memory.Select(new AiProjectFacts(
        "TEST.slnx",
        "TEST",
        "TEST.csproj",
        "TEST",
        "WPF",
        "Microsoft.NET.Sdk",
        "net10.0-windows",
        false,
        "Git unavailable",
        2,
        []));
    memory.SetRequest("Add a Save button.");
    memory.SetRecentFiles(["MainWindow.xaml", "MainWindow.xaml.cs"]);
    memory.RecordSuccess(["MainWindow.xaml"]);
    string report = memory.Format();
    Assert(report.Contains("Add a Save button", StringComparison.Ordinal) &&
           report.Contains("MainWindow.xaml", StringComparison.Ordinal) &&
           report.Contains("Recent successful patches", StringComparison.Ordinal),
        "Project memory omitted current-session facts.");
    return Task.CompletedTask;
}

static string CreateTemporaryProject(string content)
{
    string root = Path.Combine(
        Path.GetTempPath(),
        "ParseTiger.Tests",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "MainWindow.xaml"), content);
    return root;
}

static ProjectIdentity CreateIdentity(string root) => new(
    Path.Combine(root, "TEST.slnx"),
    "TEST",
    "WPF",
    root);

static async Task TestLiveGeminiAsync()
{
    var settings = new WindowsProviderSettingsStore();
    Assert(
        settings.GetApiKeySource(GeneratorProvider.Gemini) !=
        ProviderCredentialSource.None,
        "No Gemini credential is configured.");
    string model = settings.GetModel(GeneratorProvider.Gemini);
    var progressLines = new List<string>();
    var generator = new GeminiPackageGenerator(model, settings);
    string packageJson = await generator.GenerateAsync(
        "Return an empty operations array because no code change is requested.",
        "PROJECT FILE INVENTORY:\n- MainWindow.xaml\n\n" +
        "PROJECT FILE CONTENTS:\n=== MainWindow.xaml ===\n<Grid />",
        new InlineProgress(progressLines.Add),
        CancellationToken.None);
    var package = new PackageParser().Parse(packageJson);
    Assert(package.Version == "1.0", "Live Gemini response has the wrong package version.");
    Assert(settings.IsApiKeyVerified(GeneratorProvider.Gemini),
        "Live Gemini request did not persist Verified status.");
    Assert(progressLines.Any(line => line.Contains("Response received", StringComparison.Ordinal)),
        "Live Gemini response diagnostic is missing.");
}

static async Task<ProviderRequestException> ExpectProviderFailureAsync(
    Func<Task<string>> action)
{
    try
    {
        await action();
    }
    catch (ProviderRequestException exception)
    {
        return exception;
    }

    throw new InvalidOperationException("Expected a provider failure.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class InlineProgress(Action<string> report) : IProgress<string>
{
    public void Report(string value) => report(value);
}

sealed class FakeSettings : IProviderSettingsStore
{
    public bool Verified { get; private set; }

    public bool HasApiKey(GeneratorProvider provider) => true;
    public string? GetApiKey(GeneratorProvider provider) => "test-key-not-a-secret";
    public ProviderCredentialSource GetApiKeySource(GeneratorProvider provider) =>
        ProviderCredentialSource.WindowsCredentialManager;
    public bool IsApiKeyVerified(GeneratorProvider provider) => Verified;
    public void MarkApiKeyVerified(GeneratorProvider provider) => Verified = true;
    public void SaveApiKey(GeneratorProvider provider, string apiKey) =>
        throw new NotSupportedException();
    public void DeleteApiKey(GeneratorProvider provider) =>
        throw new NotSupportedException();
    public string GetModel(GeneratorProvider provider) => "test-model";
    public void SaveModel(GeneratorProvider provider, string model) =>
        throw new NotSupportedException();
}

sealed class RecordingHandler(
    Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }
    public string? Body { get; private set; }
    public bool HadApiKeyHeader { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Request = request;
        HadApiKeyHeader = request.Headers.Contains("x-goog-api-key");
        Body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return responseFactory(request);
    }
}
