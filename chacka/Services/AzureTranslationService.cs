using System.Net.Http;
using System.Text;
using System.Text.Json;
using chacka.Options;

namespace chacka.Services;

/// <summary>
/// Translates text using the Azure Cognitive Services Translator REST API.
/// </summary>
public class AzureTranslationService : ITranslationService
{
    private readonly HttpClient _http = new();
    private TranslationOptions _options = new();

    public event Action<string>? StatusChanged;

    public void UpdateOptions(TranslationOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        StatusChanged?.Invoke("Translating via Azure Translator...");
    }

    public async Task<string> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        try
        {

            // If the endpoint is empty, use the global one.
            string endpoint = string.IsNullOrWhiteSpace(_options.ApiUrl) 
                ? "https://api.cognitive.microsofttranslator.com/" 
                : _options.ApiUrl;

            if (!endpoint.EndsWith("/"))
                endpoint += "/";

            string route = $"translate?api-version=3.0&from={sourceLang}&to={targetLang}";
            string url = endpoint + route;

            // Azure Translator expects an array of objects
            var body = new object[] { new { Text = text } };
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = content
            };

            // Set the required headers
            request.Headers.Add("Ocp-Apim-Subscription-Key", _options.ApiKey);

            // If a region is specified, we must include it in the headers
            if (!string.IsNullOrWhiteSpace(_options.AzureRegion))
            {
                request.Headers.Add("Ocp-Apim-Subscription-Region", _options.AzureRegion);
            }

            using var response = await _http.SendAsync(request, cancellationToken);
            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                StatusChanged?.Invoke($"Translation error: {response.StatusCode}");
                return $"[Azure Translation error: {responseBody}]";
            }

            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0)
            {
                var translationsArray = document.RootElement[0].GetProperty("translations");
                if (translationsArray.ValueKind == JsonValueKind.Array && translationsArray.GetArrayLength() > 0)
                {
                    return translationsArray[0].GetProperty("text").GetString()?.Trim() ?? string.Empty;
                }
            }

            return "[Empty response from Azure Translator]";
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Translation error: {ex.Message}");
            return $"[Error: {ex.Message}]";
        }
    }
}
