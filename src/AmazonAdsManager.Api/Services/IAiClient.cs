namespace AmazonAdsManager.Api.Services;

public interface IAiClient
{
    Task<string> AnalyzeProductAsync(string prompt);
}

public class MockAiClient : IAiClient
{
    public async Task<string> AnalyzeProductAsync(string prompt)
    {
        await Task.Delay(100); // Simulate API latency

        // Mock AI response: return structured JSON recommendations
        return """
{
  "recommendations": [
    {
      "recommendationType": "WatchOnly",
      "severity": "Info",
      "explanation": "Product has sufficient data (>20 clicks). Current ACOS is within acceptable range.",
      "suggestedAction": "Continue monitoring. Review performance in 7 days."
    },
    {
      "recommendationType": "ReduceBudget",
      "severity": "Medium",
      "explanation": "ACOS is slightly elevated. Cost per order exceeds target by 15%.",
      "suggestedAction": "Reduce daily budget by 10-15% to improve profitability.",
      "suggestedBudgetChangePercent": -12
    }
  ]
}
""";
    }
}
