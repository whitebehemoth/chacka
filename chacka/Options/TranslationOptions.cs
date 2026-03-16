namespace chacka.Options;

public class TranslationOptions
{
    /// <summary>
    /// Base URL of the OpenAI endpoint (e.g. https://api.openai.com or https://{your-resource}.openai.azure.com).
    /// </summary>
    public string ApiUrl { get; set; } = string.Empty;

    /// <summary>
    /// Specifies the translation provider: "OpenAI" or "Azure".
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Model or deployment name.
    /// </summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    /// API key for the chosen OpenAI endpoint.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Azure Translator region (e.g., "northeurope", "eastus").
    /// </summary>
    public string AzureRegion { get; set; } = string.Empty;

    /// <summary>
    /// Temperature for the chat completion request.
    /// </summary>
    public double Temperature { get; set; } = 0.2;

}
