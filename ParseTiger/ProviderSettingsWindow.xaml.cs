using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ParseTiger.Generation;

namespace ParseTiger;

public partial class ProviderSettingsWindow : Window
{
    private readonly IProviderSettingsStore _settings;

    public ProviderSettingsWindow(IProviderSettingsStore settings)
    {
        InitializeComponent();
        _settings = settings;
        GeminiModel.ItemsSource = new[] { "gemini-3.5-flash-lite", "gemini-3.5-flash" };
        OpenAiModel.ItemsSource = new[] { "gpt-5.6-luna", "gpt-5.6-terra", "gpt-5.6-sol" };
        LoadProviderState();
    }

    public bool SettingsChanged { get; private set; }

    private void LoadProviderState()
    {
        try
        {
            GeminiModel.Text = _settings.GetModel(GeneratorProvider.Gemini);
            OpenAiModel.Text = _settings.GetModel(GeneratorProvider.OpenAI);
            RefreshProvider(
                GeneratorProvider.Gemini,
                GeminiStatus,
                GeminiAddChangeButton,
                GeminiRemoveButton);
            RefreshProvider(
                GeneratorProvider.OpenAI,
                OpenAiStatus,
                OpenAiAddChangeButton,
                OpenAiRemoveButton);
        }
        catch (Exception exception)
        {
            ShowError("Could not load AI provider settings: " + exception.Message);
        }
    }

    private void RefreshProvider(
        GeneratorProvider provider,
        TextBlock status,
        Button addChange,
        Button remove)
    {
        ProviderCredentialSource source = _settings.GetApiKeySource(provider);
        bool configured = source != ProviderCredentialSource.None;
        status.Text = configured
            ? source == ProviderCredentialSource.WindowsCredentialManager
                ? "Credential Saved — verified when used"
                : "Environment Credential — verified when used"
            : "Not Configured";
        bool verified = configured && _settings.IsApiKeyVerified(provider);
        if (verified)
        {
            status.Text = "Verified";
        }
        else if (configured)
        {
            status.Text = source == ProviderCredentialSource.WindowsCredentialManager
                ? "Credential Saved — verification pending"
                : "Environment Credential — verification pending";
        }

        status.Foreground = configured ? Brushes.DarkGreen : Brushes.DarkOrange;
        addChange.Content = configured ? "Change API Key" : "Add API Key";
        remove.IsEnabled = configured;
    }

    private void AddOrChangeKey_Click(object sender, RoutedEventArgs e)
    {
        GeneratorProvider provider = ProviderFromTag(sender);
        bool isChange;
        try
        {
            isChange = _settings.HasApiKey(provider);
        }
        catch (Exception exception)
        {
            ShowError("Could not read the current API key status: " + exception.Message);
            return;
        }

        var dialog = new ProviderApiKeyDialog(provider, isChange) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string providerName = DisplayName(provider);
        try
        {
            _settings.SaveApiKey(provider, dialog.ApiKey);
            SettingsChanged = true;
            LoadProviderState();
            ShowSuccess(
                $"{providerName} API key saved securely for this Windows user. " +
                "The key will be verified by the provider when Generate & Run is used.");
            MessageBox.Show(
                $"{providerName} API key saved securely for this Windows user." +
                Environment.NewLine + Environment.NewLine +
                "Saved does not yet mean the provider accepted the key. " +
                "Generate & Run will verify it and show any provider error.",
                "API Key Saved",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ShowError($"Could not save the {providerName} API key: {exception.Message}");
            MessageBox.Show(
                Feedback.Text,
                "API Key Save Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        GeneratorProvider provider = ProviderFromTag(sender);
        string providerName = DisplayName(provider);
        MessageBoxResult confirmation = MessageBox.Show(
            $"Remove the saved {providerName} API key from Windows Credential Manager?",
            "Remove API Key",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _settings.DeleteApiKey(provider);
            SettingsChanged = true;
            LoadProviderState();
            ShowSuccess($"{providerName} API key removed.");
        }
        catch (Exception exception)
        {
            ShowError($"Could not remove the {providerName} API key: {exception.Message}");
            MessageBox.Show(
                Feedback.Text,
                "API Key Removal Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SaveModels_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings.SaveModel(GeneratorProvider.Gemini, GeminiModel.Text);
            _settings.SaveModel(GeneratorProvider.OpenAI, OpenAiModel.Text);
            SettingsChanged = true;
            ShowSuccess("Provider model selections saved for this Windows user.");
        }
        catch (Exception exception)
        {
            ShowError("Could not save provider model settings: " + exception.Message);
            MessageBox.Show(
                Feedback.Text,
                "Settings Save Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static GeneratorProvider ProviderFromTag(object sender)
    {
        string tag = (sender as FrameworkElement)?.Tag?.ToString() ?? string.Empty;
        return Enum.TryParse(tag, out GeneratorProvider provider)
            ? provider
            : throw new InvalidOperationException("The provider action is not configured.");
    }

    private static string DisplayName(GeneratorProvider provider) =>
        provider == GeneratorProvider.Gemini ? "Gemini" : "OpenAI";

    private void ShowSuccess(string message)
    {
        Feedback.Text = message;
        Feedback.Foreground = Brushes.DarkGreen;
    }

    private void ShowError(string message)
    {
        Feedback.Text = message;
        Feedback.Foreground = Brushes.DarkRed;
    }
}
