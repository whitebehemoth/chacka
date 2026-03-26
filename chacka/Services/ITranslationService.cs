using System;
using System.Threading.Tasks;
using chacka.Options;

namespace chacka.Services;

public interface ITranslationService
{
    event Action<string>? StatusChanged;
    void UpdateOptions(TranslationOptions options);
    Task<string> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken cancellationToken = default);
}
