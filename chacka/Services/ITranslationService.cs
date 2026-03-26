using chacka.Options;

namespace chacka.Services;

/// <summary>
/// Common interface for all translation backends (Azure Translator, OpenAI, etc.).
/// </summary>
public interface ITranslationService
{
    event Action<string>? StatusChanged;
    void UpdateOptions(TranslationOptions options);
    Task<string> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken cancellationToken = default);
}
