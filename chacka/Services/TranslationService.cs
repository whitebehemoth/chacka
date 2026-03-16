using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using chacka.Options;

namespace chacka.Services;

public class TranslationService : ITranslationService
{
    private readonly HttpClient _http = new();
    private TranslationOptions _options = new();

    public void UpdateOptions(TranslationOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        StatusChanged?.Invoke($"Translating via OpenAI ({options.ModelName})");
    }

    public event Action<string>? StatusChanged;

    
    public async Task<string> TranslateAsync(string text, string sourceLang, string targetLang)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        try
        {

            string prompt = $"Translate the following text from {sourceLang} to {targetLang}:\n\n{text}";

            var payload = new Dictionary<string, object>
            {
                ["messages"] = new[]
                {
                    new { role = "system", content = "You are a concise translator. Return only the translated text." },
                    new { role = "user", content = prompt }
                },
                ["temperature"] = _options.Temperature
            };

            payload["model"] = _options.ModelName;
            

            string url = _options.ApiUrl;
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = content
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            using var response = await _http.SendAsync(request);
            string responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                StatusChanged?.Invoke($"Translation error: {response.StatusCode}");
                return $"[Translation error: {responseBody}]";
            }

            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var message = choices[0].GetProperty("message");
                if (message.TryGetProperty("content", out var contentElement))
                {
                    return contentElement.GetString()?.Trim() ?? string.Empty;
                }
            }

            return "[Empty response]";
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Translation error: {ex.Message}");
            return $"[Error: {ex.Message}]";
        }
    }
}


