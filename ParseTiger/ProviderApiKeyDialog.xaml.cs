using System.Windows;
using ParseTiger.Generation;

namespace ParseTiger;

public partial class ProviderApiKeyDialog : Window
{
    public ProviderApiKeyDialog(GeneratorProvider provider, bool isChange)
    {
        InitializeComponent();
        Provider = provider;
        string providerName = provider == GeneratorProvider.Gemini ? "Gemini" : "OpenAI";
        Title = $"{(isChange ? "Change" : "Add")} {providerName} API Key";
        Heading.Text = $"{(isChange ? "Change" : "Add")} {providerName} API Key";
        Loaded += (_, _) => ApiKeyBox.Focus();
    }

    public GeneratorProvider Provider { get; }

    public string ApiKey => ApiKeyBox.Password;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            ValidationMessage.Text = "Enter an API key before saving.";
            ApiKeyBox.Focus();
            return;
        }

        DialogResult = true;
    }
}
