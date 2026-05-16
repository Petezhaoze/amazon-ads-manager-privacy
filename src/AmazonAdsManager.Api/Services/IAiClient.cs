namespace AmazonAdsManager.Api.Services;

public interface IAiClient
{
    Task<string> AnalyzeProductAsync(string prompt);
    Task<string> CompleteAsync(string systemPrompt, string userPrompt);
}
