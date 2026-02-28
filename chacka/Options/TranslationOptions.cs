namespace chacka.Options;

public class TranslationOptions
{
    /// <summary>
    /// Base URL of the OpenAI endpoint (e.g. https://api.openai.com or https://{your-resource}.openai.azure.com).
    /// </summary>
    public string ApiUrl { get; set; } = "https://api.openai.com";

    /// <summary>
    /// Model or deployment name.
    /// </summary>
    public string ModelName { get; set; } = "gpt-4.1-mini";

    /// <summary>
    /// API key for the chosen OpenAI endpoint.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Temperature for the chat completion request.
    /// </summary>
    public double Temperature { get; set; } = 0.2;

}
