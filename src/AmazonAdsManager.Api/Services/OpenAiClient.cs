using Microsoft.Extensions.Configuration;
using OpenAI.Chat;

namespace AmazonAdsManager.Api.Services;

public class OpenAiClient : IAiClient
{
    private readonly ChatClient? _chat;
    private readonly string? _configurationError;

    public OpenAiClient(IConfiguration config)
    {
        var apiKey = config["OpenAI:ApiKey"];
        var model = config["OpenAI:Model"];

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
        {
            _configurationError = "OpenAI is not configured. Add OpenAI:ApiKey and OpenAI:Model to run AI analysis.";
            return;
        }

        var client = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey));
        _chat = client.GetChatClient(model);
    }

    public async Task<string> AnalyzeProductAsync(string prompt)
    {
        return await CompleteAsync(
            "You are an Amazon Ads performance analyst. Return only valid JSON with no markdown code fences or extra commentary.",
            prompt);
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt)
    {
        if (_chat is null)
            throw new InvalidOperationException(_configurationError ?? "OpenAI is not configured. Add OpenAI:ApiKey and OpenAI:Model to run AI analysis.");

        var response = await _chat.CompleteChatAsync(
        [
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        ]);
        return response.Value.Content[0].Text;
    }
}
