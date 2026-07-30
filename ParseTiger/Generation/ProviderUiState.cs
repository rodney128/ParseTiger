namespace ParseTiger.Generation;

/// <summary>
/// The complete provider-dependent UI state. Keeping this in one value prevents
/// provider switching from leaving stale Manual or provider-specific wording.
/// </summary>
public sealed record ProviderUiState(
    bool IsDirect,
    string ProviderName,
    string ModeDescription,
    string ConfigurationText,
    ProviderConfigurationState ConfigurationState,
    string PackagePlaceholder,
    string PrimaryActionLabel,
    string PrimaryActionToolTip,
    string ReadyStatus)
{
    public static ProviderUiState Create(
        GeneratorOption option,
        ProviderCredentialSource source,
        bool verified,
        string? targetProjectName = null)
    {
        string target = string.IsNullOrWhiteSpace(targetProjectName)
            ? string.Empty
            : $" for {targetProjectName}";

        if (option.Provider == GeneratorProvider.PasteJson)
        {
            return new ProviderUiState(
                IsDirect: false,
                ProviderName: "Manual",
                ModeDescription:
                    "Paste a ParseTiger JSON package below. No API provider is called.",
                ConfigurationText: "Manual mode — no API key required.",
                ConfigurationState: ProviderConfigurationState.Ready,
                PackagePlaceholder:
                    "Paste the ParseTiger JSON package returned by your AI",
                PrimaryActionLabel: "Validate Package & Run",
                PrimaryActionToolTip:
                    "Validate the pasted package, apply it, build the solution, and launch the application",
                ReadyStatus: $"Ready to validate the pasted package{target}.");
        }

        string providerName = option.Provider == GeneratorProvider.Gemini
            ? "Gemini"
            : "OpenAI";
        bool configured = source != ProviderCredentialSource.None;
        string configurationText = !configured
            ? $"{providerName} — Not configured"
            : verified
                ? $"{providerName} — Verified"
                : $"{providerName} — Credential saved; verification pending";
        ProviderConfigurationState configurationState = !configured
            ? ProviderConfigurationState.NotConfigured
            : verified
                ? ProviderConfigurationState.Verified
                : ProviderConfigurationState.PendingVerification;

        return new ProviderUiState(
            IsDirect: true,
            ProviderName: providerName,
            ModeDescription:
                $"Generate with {providerName}, then automatically validate, apply, build, and run.",
            ConfigurationText: configurationText,
            ConfigurationState: configurationState,
            PackagePlaceholder:
                "The generated ParseTiger package will appear here",
            PrimaryActionLabel: $"Generate with {providerName} & Run",
            PrimaryActionToolTip:
                $"Send the requested change to {providerName}, then validate, apply, build, and launch",
            ReadyStatus: configured
                ? $"Ready to generate with {providerName}{target}."
                : $"{providerName} is not configured. Add an API key in AI Provider Settings.");
    }
}

public enum ProviderConfigurationState
{
    Ready,
    NotConfigured,
    PendingVerification,
    Verified,
    Error
}
