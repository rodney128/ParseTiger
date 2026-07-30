namespace ParseTiger.Generation;

public enum GeneratorProvider
{
    PasteJson,
    Gemini,
    OpenAI
}

/// <summary>A selectable AI option shown in the UI, able to build its generator.</summary>
public sealed record GeneratorOption(string Display, GeneratorProvider Provider)
{
    public override string ToString() => Display;

    public IPackageGenerator Create(string model, IProviderSettingsStore settings) => Provider switch
    {
        GeneratorProvider.Gemini => new GeminiPackageGenerator(model, settings),
        GeneratorProvider.OpenAI => new OpenAiPackageGenerator(model, settings),
        _ => new PassthroughPackageGenerator()
    };
}
